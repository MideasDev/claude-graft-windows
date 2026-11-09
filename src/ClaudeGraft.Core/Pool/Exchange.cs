using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace ClaudeGraft.Core.Pool;

/// Where requests go: the service, or a stand-in on loopback for the suite.
public sealed record Upstream(int? LoopbackPort = null)
{
    public async Task<Wire> ConnectAsync()
    {
        var tcp = new TcpClient { NoDelay = true };
        if (LoopbackPort is { } port)
        {
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port).ConfigureAwait(false);
            return new Wire(tcp.GetStream());
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await tcp.ConnectAsync(UsagePool.Host, 443, timeout.Token).ConfigureAwait(false);
        var tls = new SslStream(tcp.GetStream(), false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = UsagePool.Host,
            ApplicationProtocols = [SslApplicationProtocol.Http11],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, timeout.Token).ConfigureAwait(false);
        return new Wire(tls);
    }
}

/// One client connection, decrypted: request after request on it. Only a
/// model request from a member is taken apart and written again; everything
/// else goes out as it came, byte for byte, head included.
public sealed class Exchange(Wire client, PoolState state, Upstream upstream)
{
    const int HeadLimit = 64 * 1024;
    const long BodyLimit = 256L * 1024 * 1024;

    Wire? connection;
    string? refusal;

    public async Task RunAsync()
    {
        try { while (await ServeOneAsync().ConfigureAwait(false)) { } }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or SocketException or ObjectDisposedException or AuthenticationException) { }
        catch (Exception e) { Diagnostics.Note("relay.exchange-failed", e.GetType().Name + " " + e.Message); }
        connection?.Dispose();
        client.Dispose();
    }

    async Task<bool> ServeOneAsync()
    {
        if (await client.ReadHeadAsync(HeadLimit).ConfigureAwait(false) is not var (head, raw)) return false;
        if (head.Line.Length != 3 || head.Method == "CONNECT" || head.RequestBody is not { } framing)
        {
            await Try(client.SendAsync(HttpMessage.Failure(400, "Bad Request", "The relay could not read that request."))).ConfigureAwait(false);
            return false;
        }
        state.Began();
        try
        {
            if (head.Tokens("expect").Contains("100-continue"))
                await client.SendAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray()).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            string? own = PoolRequest.Bearer(head.Fields) is { } bearer ? state.MemberFor(bearer, now) : null;
            if (!PoolRequest.IsInference(head.Method, head.Target) || own == null || head.AsksToUpgrade)
                return await PassThroughAsync(head, raw, framing).ConfigureAwait(false);

            byte[] body;
            try { body = await client.ReadBodyAsync(framing, BodyLimit).ConfigureAwait(false); }
            catch (RelayTooLarge)
            {
                await Try(client.SendAsync(HttpMessage.Failure(413, "Payload Too Large", "That request is too large for the relay."))).ConfigureAwait(false);
                return false;
            }

            var session = head.Value("x-claude-code-session-id");
            var pinned = PoolRequest.MustStayWithSender(body, head.Value("content-encoding"));
            var continuing = PoolRequest.ContinuesThread(body);
            var attempts = state.Plan(own, session, pinned, continuing || PoolRequest.CarriesBoundReasoning(body), now);
            var model = PoolRequest.Model(body);
            var lifetime = PoolPolicy.CacheLifetime(body);

            var passed = new List<PoolPass>();
            var index = 0;
            while (index < attempts.Count)
            {
                var attempt = attempts[index];
                var last = index == attempts.Count - 1;
                var borrowed = attempt.Token != null;
                var request = HttpMessage.Request(head, body, borrowed ? "Bearer " + attempt.Token : null, plainAnswer: continuing && borrowed);
                (HttpHead Head, byte[] Raw) answer;
                try { answer = await SendAsync(request).ConfigureAwait(false); }
                catch (Exception)
                {
                    connection?.Dispose();
                    connection = null;
                    if (last)
                    {
                        await Try(client.SendAsync(HttpMessage.Failure(502, "Bad Gateway", $"The relay could not reach {UsagePool.Host}."))).ConfigureAwait(false);
                        return false;
                    }
                    passed.Add(new PoolPass(attempt.Member, PassReason.Unreachable));
                    index++;
                    continue;
                }

                var status = answer.Head.Status ?? 502;
                state.Observe(attempt.Member, answer.Head.Fields, DateTime.UtcNow);
                var outcome = PoolPolicy.Decide(status, answer.Head.Fields, borrowed, DateTime.UtcNow);

                // A moved thread refused as not found is Claude Code's to
                // hear: it starts the thread again where it went.
                if (outcome.Kind == PoolPolicy.OutcomeKind.RefusedForAccount && status == 404 && continuing && !last &&
                    await ThreadMissingAsync(answer, head.Method).ConfigureAwait(false) is { } missing)
                {
                    Diagnostics.Note("relay.thread-restarted", attempt.Member);
                    state.Served(attempt.Member, own, session, status, passed, model, lifetime, true,
                                 PoolRequest.StartsConversation(body), DateTime.UtcNow);
                    return await DeliverReadAsync(answer, missing, head.Method, head.KeepsAlive).ConfigureAwait(false);
                }
                if (outcome.Kind != PoolPolicy.OutcomeKind.Deliver && !last)
                {
                    state.Apply(outcome, attempt.Member, DateTime.UtcNow);
                    connection?.Dispose();
                    connection = null;
                    switch (outcome.Kind)
                    {
                        case PoolPolicy.OutcomeKind.Exhausted: passed.Add(new(attempt.Member, PassReason.Full)); index++; break;
                        case PoolPolicy.OutcomeKind.LoginRefused: passed.Add(new(attempt.Member, PassReason.Login)); index++; break;
                        default: passed.Add(new(attempt.Member, PassReason.Refused)); index = attempts.Count - 1; break;
                    }
                    Diagnostics.Note("relay.passed-over", $"{attempt.Member} {status} {passed[^1].Reason}" +
                                     (continuing ? " thread=continue" : "") + (refusal != null ? $" error={refusal}" : ""));
                    refusal = null;
                    continue;
                }
                state.Served(attempt.Member, own, session, status, passed, model, lifetime,
                             PoolRequest.CarriesThread(body), PoolRequest.StartsConversation(body), DateTime.UtcNow);
                return await DeliverStreamAsync(answer, head.Method, head.KeepsAlive).ConfigureAwait(false);
            }
            return false;
        }
        finally { state.Ended(); }
    }

    static async Task Try(Task task) { try { await task.ConfigureAwait(false); } catch { } }

    /// What cannot change accounts. An upgrade gets a connection of its own
    /// and is spliced after its 101; a chunked body is forwarded as it
    /// arrives while the answer comes back.
    async Task<bool> PassThroughAsync(HttpHead head, byte[] raw, HttpBody framing)
    {
        var upgrade = head.AsksToUpgrade;
        (HttpHead Head, byte[] Raw) answer;
        Task? upload = null;
        if (!upgrade && framing is not HttpBody.Chunked)
        {
            byte[] body;
            try { body = await client.ReadBodyAsync(framing, BodyLimit).ConfigureAwait(false); }
            catch (RelayTooLarge)
            {
                await Try(client.SendAsync(HttpMessage.Failure(413, "Payload Too Large", "That request is too large for the relay."))).ConfigureAwait(false);
                return false;
            }
            try { answer = await SendAsync([.. raw, .. body]).ConfigureAwait(false); }
            catch
            {
                connection?.Dispose();
                connection = null;
                await Try(client.SendAsync(HttpMessage.Failure(502, "Bad Gateway", $"The relay could not reach {UsagePool.Host}."))).ConfigureAwait(false);
                return false;
            }
        }
        else
        {
            connection?.Dispose();
            connection = null;
            Wire fresh;
            try { fresh = await upstream.ConnectAsync().ConfigureAwait(false); }
            catch
            {
                await Try(client.SendAsync(HttpMessage.Failure(502, "Bad Gateway", $"The relay could not reach {UsagePool.Host}."))).ConfigureAwait(false);
                return false;
            }
            connection = fresh;
            await fresh.SendAsync(raw).ConfigureAwait(false);
            upload = ForwardAsync(framing, fresh);
            answer = await FinalHeadAsync(fresh).ConfigureAwait(false) ?? throw new EndOfStreamException();
        }

        if (upgrade && answer.Head.Status == 101 && connection is { } up)
        {
            if (upload != null) await upload.ConfigureAwait(false);
            await client.SendAsync(answer.Raw).ConfigureAwait(false);
            connection = null;
            await Splice.RunAsync(client, up).ConfigureAwait(false);
            return false;
        }
        var more = await DeliverStreamAsync(answer, head.Method, head.KeepsAlive).ConfigureAwait(false);
        if (upload != null) await upload.ConfigureAwait(false);
        return more;
    }

    async Task ForwardAsync(HttpBody framing, Wire up)
    {
        switch (framing)
        {
            case HttpBody.Length { Count: var total }:
                var remaining = total;
                while (remaining > 0)
                {
                    if (client.Buffered == 0 && !await client.FillAsync().ConfigureAwait(false)) throw new EndOfStreamException();
                    var piece = client.Take((int)Math.Min(remaining, client.Buffered));
                    remaining -= piece.Length;
                    await up.SendAsync(piece).ConfigureAwait(false);
                }
                break;
            case HttpBody.Chunked:
                var chunked = new ChunkedFraming();
                while (true)
                {
                    if (client.Buffered > 0)
                    {
                        var taken = chunked.Consume(client.Pending, null);
                        await up.SendAsync(client.Take(taken)).ConfigureAwait(false);
                    }
                    if (chunked.IsDone) return;
                    if (!await client.FillAsync().ConfigureAwait(false)) throw new EndOfStreamException();
                }
        }
    }

    /// A kept connection the service has dropped is tried again on a fresh
    /// one, but only if nothing at all came back on it.
    async Task<(HttpHead Head, byte[] Raw)> SendAsync(byte[] request)
    {
        if (connection is { } reused)
        {
            var before = reused.Received;
            try
            {
                await reused.SendAsync(request).ConfigureAwait(false);
                if (await FinalHeadAsync(reused).ConfigureAwait(false) is { } answer) return answer;
            }
            catch when (reused.Received == before) { }
            if (reused.Received != before) throw new EndOfStreamException();
            reused.Dispose();
            connection = null;
        }
        var fresh = await upstream.ConnectAsync().ConfigureAwait(false);
        connection = fresh;
        await fresh.SendAsync(request).ConfigureAwait(false);
        return await FinalHeadAsync(fresh).ConfigureAwait(false) ?? throw new EndOfStreamException();
    }

    static async Task<(HttpHead Head, byte[] Raw)?> FinalHeadAsync(Wire wire)
    {
        while (true)
        {
            if (await wire.ReadHeadAsync(HeadLimit).ConfigureAwait(false) is not { } answer) return null;
            if (answer.Head.Status is >= 100 and < 200 and not 101) continue;
            return answer;
        }
    }

    /// Reads a 404's body to tell `thread_not_found` from any other refusal.
    /// A compressed refusal cannot be read, and is treated as any other.
    async Task<byte[]?> ThreadMissingAsync((HttpHead Head, byte[] Raw) answer, string method)
    {
        if (connection is not { } up) return null;
        var framing = answer.Head.ResponseBody(method);
        if (framing is HttpBody.UntilClose) return null;
        var encoding = answer.Head.Value("content-encoding");
        if (!string.IsNullOrEmpty(encoding) && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) return null;
        var body = await up.ReadBodyAsync(framing, 64 * 1024).ConfigureAwait(false);
        if (Bytes.Contains(body, PoolRequest.ThreadMissing)) return body;
        try { refusal = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("type").GetString(); }
        catch { refusal = "unreadable"; }
        return null;
    }

    async Task<bool> DeliverReadAsync((HttpHead Head, byte[] Raw) answer, byte[] body, string method, bool keepAlive)
    {
        await client.SendAsync(answer.Raw).ConfigureAwait(false);
        if (answer.Head.ResponseBody(method) is HttpBody.Chunked)
        {
            if (body.Length > 0)
                await client.SendAsync([.. System.Text.Encoding.ASCII.GetBytes(body.Length.ToString("x") + "\r\n"), .. body, .. "\r\n"u8.ToArray()]).ConfigureAwait(false);
            await client.SendAsync("0\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
        }
        else await client.SendAsync(body).ConfigureAwait(false);
        if (!answer.Head.KeepsAlive || connection?.Buffered > 0) { connection?.Dispose(); connection = null; }
        return keepAlive && answer.Head.KeepsAlive;
    }

    /// The answer, streamed to the client as it arrives.
    async Task<bool> DeliverStreamAsync((HttpHead Head, byte[] Raw) answer, string method, bool keepAlive)
    {
        if (connection is not { } up) return false;
        await client.SendAsync(answer.Raw).ConfigureAwait(false);
        switch (answer.Head.ResponseBody(method))
        {
            case HttpBody.Length { Count: var total }:
                var remaining = total;
                while (remaining > 0)
                {
                    if (up.Buffered == 0 && !await up.FillAsync().ConfigureAwait(false)) throw new EndOfStreamException();
                    var piece = up.Take((int)Math.Min(remaining, up.Buffered));
                    remaining -= piece.Length;
                    await client.SendAsync(piece).ConfigureAwait(false);
                }
                break;
            case HttpBody.Chunked:
                var chunked = new ChunkedFraming();
                while (true)
                {
                    if (up.Buffered > 0)
                    {
                        var taken = chunked.Consume(up.Pending, null);
                        await client.SendAsync(up.Take(taken)).ConfigureAwait(false);
                    }
                    if (chunked.IsDone) break;
                    if (!await up.FillAsync().ConfigureAwait(false)) throw new EndOfStreamException();
                }
                break;
            case HttpBody.UntilClose:
                await client.SendAsync(up.TakeAll()).ConfigureAwait(false);
                while (await up.FillAsync().ConfigureAwait(false)) await client.SendAsync(up.TakeAll()).ConfigureAwait(false);
                up.Dispose();
                connection = null;
                return false;
        }
        if (!answer.Head.KeepsAlive || up.Buffered > 0) { up.Dispose(); connection = null; }
        return keepAlive && answer.Head.KeepsAlive;
    }
}

/// Two connections joined: whatever either has already buffered goes first,
/// then bytes in both directions until either side ends.
public static class Splice
{
    public static async Task RunAsync(Wire a, Wire b)
    {
        async Task Pump(Wire from, Wire to)
        {
            try
            {
                if (from.Buffered > 0) await to.SendAsync(from.TakeAll()).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await from.Stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read <= 0) break;
                    await to.Stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    await to.Stream.FlushAsync().ConfigureAwait(false);
                }
            }
            catch { }
        }
        await Task.WhenAny(Pump(a, b), Pump(b, a)).ConfigureAwait(false);
        a.Dispose();
        b.Dispose();
    }
}
