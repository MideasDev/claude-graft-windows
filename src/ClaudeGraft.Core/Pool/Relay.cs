using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core.Pool;

/// The relay: `ClaudeGraft.exe --relay`, a process of its own, because every
/// request a connected Claude Code makes goes through it and it cannot stop
/// when Claude Graft does. Listens on loopback alone, takes apart a CONNECT
/// for api.anthropic.com:443 and passes every other through as bytes, and is
/// told everything over a named pipe only this Windows user can open.
public sealed class Relay
{
    public sealed record Options(int Port = UsagePool.Port, string? Pipe = null, Upstream? Upstream = null);

    public static string DefaultPipe => "ClaudeGraftWindows.Relay." + Hash(Paths.GraftHome);
    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..16];

    readonly Options options;
    readonly PoolState state = new();
    readonly DateTime started = DateTime.UtcNow;
    readonly CancellationTokenSource stopping = new();
    readonly string binary;
    TcpListener? front;
    X509Certificate2? identity;

    public Relay(Options options)
    {
        this.options = options;
        binary = Binary();
    }

    public PoolState State => state;
    string PipeName => options.Pipe ?? DefaultPipe;
    Upstream Upstream => options.Upstream ?? new Upstream();

    /// What a relay was built from, so Claude Graft can replace one built
    /// from a different copy of itself.
    public static string Binary()
    {
        try { return Environment.ProcessPath is { } p ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))) : ""; }
        catch { return ""; }
    }

    /// Runs until asked to step aside. Returns false when another relay
    /// already holds this one's place.
    public async Task<bool> RunAsync()
    {
        // The port is the one-instance rule: bound exclusively, so a second
        // relay finds it taken and leaves, and one stepping aside frees it
        // the moment it stops listening, for its replacement to take.
        front = new TcpListener(IPAddress.Loopback, options.Port);
        front.Server.ExclusiveAddressUse = true;
        try { front.Start(256); }
        catch (SocketException) { return false; }
        try
        {
            identity = PoolCertificates.Ensure();
            Diagnostics.Note("relay.started", $"port={options.Port}");
            var control = Task.Run(ControlLoopAsync);
            while (!stopping.IsCancellationRequested)
            {
                TcpClient accepted;
                try { accepted = await front.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) when (stopping.IsCancellationRequested) { break; }
                accepted.NoDelay = true;
                _ = Task.Run(() => TunnelAsync(accepted));
            }
            // Stepped aside: a replacement can bind the port at once, and the
            // last exchange is let finish, a quarter of an hour at most.
            var deadline = DateTime.UtcNow.AddMinutes(15);
            await Task.Delay(500).ConfigureAwait(false);
            while (state.InFlight > 0 && DateTime.UtcNow < deadline) await Task.Delay(1000).ConfigureAwait(false);
            await control.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { }).ConfigureAwait(false);
            return true;
        }
        finally
        {
            identity?.Dispose();
        }
    }

    void StepAside()
    {
        if (stopping.IsCancellationRequested) return;
        Diagnostics.Note("relay.stepping-aside");
        stopping.Cancel();
        try { front?.Stop(); } catch { }
    }

    async Task TunnelAsync(TcpClient accepted)
    {
        var client = new Wire(accepted.GetStream());
        try
        {
            if (await client.ReadHeadAsync(16384).ConfigureAwait(false) is not var (head, raw)) { client.Dispose(); return; }
            if (head.Method == "CONNECT")
            {
                if (Authority(head.Target) is not var (host, port))
                {
                    await client.SendAsync(HttpMessage.Failure(400, "Bad Request", "The relay could not read that address.")).ConfigureAwait(false);
                    client.Dispose();
                    return;
                }
                var intercept = host.Equals(UsagePool.Host, StringComparison.OrdinalIgnoreCase) && port == 443;
                if (intercept)
                {
                    await client.SendAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
                    var tls = new SslStream(new BufferedPrefix(client), false);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = identity,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = false,
                    }).ConfigureAwait(false);
                    await new Exchange(new Wire(tls), state, Upstream).RunAsync().ConfigureAwait(false);
                    return;
                }
                var far = new TcpClient { NoDelay = true };
                try { await far.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false); }
                catch
                {
                    await client.SendAsync(HttpMessage.Failure(502, "Bad Gateway", $"The relay could not reach {host}.")).ConfigureAwait(false);
                    client.Dispose();
                    return;
                }
                await client.SendAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
                await Splice.RunAsync(client, new Wire(far.GetStream())).ConfigureAwait(false);
            }
            else if (Uri.TryCreate(head.Target, UriKind.Absolute, out var url) && url.Scheme == "http")
            {
                var far = new TcpClient { NoDelay = true };
                await far.ConnectAsync(url.Host, url.Port).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                var wire = new Wire(far.GetStream());
                await wire.SendAsync(raw).ConfigureAwait(false);
                await Splice.RunAsync(client, wire).ConfigureAwait(false);
            }
            else
            {
                await client.SendAsync(HttpMessage.Failure(400, "Bad Request", "The relay only forwards CONNECT.")).ConfigureAwait(false);
                client.Dispose();
            }
        }
        catch { client.Dispose(); }
    }

    public static (string Host, int Port)? Authority(string target)
    {
        if (target.StartsWith('['))
        {
            var close = target.IndexOf(']');
            if (close < 0 || close + 1 >= target.Length || target[close + 1] != ':' || !int.TryParse(target[(close + 2)..], out var p6)) return null;
            return (target[1..close], p6);
        }
        var colon = target.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(target[(colon + 1)..], out var port) || port is < 1 or > 65535) return null;
        return (target[..colon], port);
    }

    // ---- Control ----

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    async Task ControlLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Diagnostics.Note("relay.control-failed", e.Message); await Task.Delay(1000).ConfigureAwait(false); continue; }
            _ = Task.Run(async () =>
            {
                using (pipe)
                {
                    try
                    {
                        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        var command = line == null ? new PoolCommand() : JsonSerializer.Deserialize<PoolCommand>(line, Json) ?? new PoolCommand();
                        if (command.Configure != null) state.Configure(command.Configure, DateTime.UtcNow);
                        var status = state.Status(binary, started, DateTime.UtcNow);
                        var answer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(status, Json) + "\n");
                        await pipe.WriteAsync(answer).ConfigureAwait(false);
                        await pipe.FlushAsync().ConfigureAwait(false);
                        if (command.Quit == true) StepAside();
                    }
                    catch (Exception e) { Diagnostics.Note("relay.control-garbled", e.Message); }
                }
            });
        }
    }

    /// Claude Graft's side of the pipe. Null when no relay answers.
    public static PoolStatus? Send(PoolCommand command, string? pipe = null, int timeoutMs = 4000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipe ?? DefaultPipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, Json) + "\n");
            client.Write(payload);
            client.Flush();
            using var reader = new StreamReader(client, Encoding.UTF8);
            var line = reader.ReadLine();
            return line == null ? null : JsonSerializer.Deserialize<PoolStatus>(line, Json);
        }
        catch { return null; }
    }
}

/// A stream that first gives back whatever a Wire already buffered, so a
/// TLS hello that arrived with the CONNECT is not lost.
sealed class BufferedPrefix(Wire wire) : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => wire.Stream.Flush();
    public override Task FlushAsync(CancellationToken cancel) => wire.Stream.FlushAsync(cancel);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
    {
        if (wire.Buffered > 0)
        {
            var count = Math.Min(buffer.Length, wire.Buffered);
            wire.Take(count).CopyTo(buffer);
            return count;
        }
        return await wire.Stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) =>
        ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => wire.Stream.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default) => wire.Stream.WriteAsync(buffer, cancel);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancel) => wire.Stream.WriteAsync(buffer, offset, count, cancel);

    protected override void Dispose(bool disposing)
    {
        if (disposing) wire.Dispose();
        base.Dispose(disposing);
    }
}
