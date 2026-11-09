using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ClaudeGraft.Core.Pool;

/// The relay over loopback against a stand-in for the service: which account
/// each request reached, and that everything that cannot change accounts
/// arrives exactly as it was sent.
static class RelayTests
{
    const string TokenA = "token-of-claude-a";      // the sender's own, as Desktop hands it to Claude Code
    const string TokenB = "token-lent-by-claude-b";
    const string TokenC = "token-lent-by-claude-c";
    const string Stranger = "token-nobody-knows";

    sealed class StandIn
    {
        public readonly ConcurrentQueue<(string Auth, string Target, byte[] Raw)> Seen = new();
        public Func<string, string, byte[], (int Status, string Headers, string Body, bool Chunked)> Answer =
            (auth, _, _) => (200, "", "{\"servedBy\":\"" + auth + "\"}", false);
        public int Port;
        TcpListener? listener;

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = await listener.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(() => Serve(c));
                }
            });
        }

        async Task Serve(TcpClient c)
        {
            var wire = new Wire(c.GetStream());
            try
            {
                while (await wire.ReadHeadAsync(65536) is var (head, raw))
                {
                    var body = await wire.ReadBodyAsync(head.RequestBody ?? HttpBody.NoBody, 1 << 24);
                    var auth = PoolRequest.Bearer(head.Fields) ?? "";
                    Seen.Enqueue((auth, head.Target, [.. raw, .. body]));
                    var (status, headers, text, chunked) = Answer(auth, head.Target, body);
                    var payload = Encoding.UTF8.GetBytes(text);
                    var reply = new StringBuilder($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\n{headers}");
                    if (chunked)
                    {
                        reply.Append("Transfer-Encoding: chunked\r\n\r\n");
                        var half = payload.Length / 2;
                        reply.Append($"{half:x}\r\n").Append(Encoding.UTF8.GetString(payload, 0, half)).Append("\r\n");
                        reply.Append($"{payload.Length - half:x}\r\n").Append(Encoding.UTF8.GetString(payload, half, payload.Length - half)).Append("\r\n0\r\n\r\n");
                        await wire.SendAsync(Encoding.UTF8.GetBytes(reply.ToString()));
                    }
                    else
                    {
                        reply.Append($"Content-Length: {payload.Length}\r\n\r\n");
                        await wire.SendAsync([.. Encoding.UTF8.GetBytes(reply.ToString()), .. payload]);
                    }
                }
            }
            catch { }
            wire.Dispose();
        }

        public void Stop() => listener?.Stop();
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// What Claude Code does: CONNECT through the proxy, TLS trusting only the
    /// relay's authority, then one request.
    static async Task<(int Status, string Body)> Ask(int relayPort, X509Certificate2 authority, string method, string target,
                                                     string bearer, string body = "", string? session = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, relayPort);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {UsagePool.Host}:443 HTTP/1.1\r\nHost: {UsagePool.Host}:443\r\n\r\n"));
        var plain = new Wire(stream);
        var connected = await plain.ReadHeadAsync(4096);
        if (connected?.Head.Status != 200) return (connected?.Head.Status ?? 0, "");
        using var tls = new SslStream(stream, false, (_, cert, _, _) =>
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(authority);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(new X509Certificate2(cert!));
        });
        await tls.AuthenticateAsClientAsync(UsagePool.Host);
        var wire = new Wire(tls);
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"{method} {target} HTTP/1.1\r\nHost: {UsagePool.Host}\r\nAuthorization: Bearer {bearer}\r\nContent-Type: application/json\r\n" +
                   (session != null ? $"X-Claude-Code-Session-Id: {session}\r\n" : "") +
                   (method == "POST" ? $"Content-Length: {bytes.Length}\r\n" : "") + "\r\n";
        await wire.SendAsync([.. Encoding.ASCII.GetBytes(head), .. bytes]);
        var answer = await wire.ReadHeadAsync(65536);
        var reply = await wire.ReadBodyAsync(answer!.Value.Head.ResponseBody(method), 1 << 20);
        return (answer.Value.Head.Status ?? 0, Encoding.UTF8.GetString(reply));
    }

    static PoolMember Member(string id, string account, string? lends, string identifyToken, List<string>? borrows = null, DateTime? weekReset = null) => new()
    {
        Id = id, Name = id, Account = account, Token = lends,
        Identify = [UsagePool.Digest(identifyToken)],
        BorrowsFrom = borrows,
        Limits = weekReset == null ? null : new PoolLimits(0.1, null, 0.2, weekReset, null, DateTime.UtcNow),
    };

    public static async Task Run(Action<bool, string> check)
    {
        var now = DateTime.UtcNow;

        // ---- The rules, with no network in them ----
        PoolPolicy.Account A(string id, double week = 0, DateTime? reset = null, bool usable = true, double five = 0) =>
            new(id, new PoolLimits(five, null, week, reset, null, now), usable);
        check(PoolPolicy.Order("a", [A("a", reset: now.AddDays(3)), A("b", reset: now.AddDays(1))], null, now).SequenceEqual(["b", "a"]),
              "the account whose week resets soonest answers first");
        check(PoolPolicy.Order("a", [A("a", reset: now.AddDays(1)), A("b", reset: now.AddDays(1))], null, now)[0] == "a",
              "a tie goes to the sender, since staying costs nothing");
        check(PoolPolicy.Order("a", [A("a"), A("b", reset: now.AddDays(5))], null, now)[0] == "b",
              "a known reset goes before an unknown one");
        check(PoolPolicy.Order("a", [A("a", five: 0.99), A("b")], null, now).SequenceEqual(["b", "a"]),
              "a sender with no room goes last, and is still tried");
        check(PoolPolicy.Order("a", [A("a"), A("b", week: 0.995)], null, now).SequenceEqual(["a"]),
              "an account at 99% of its week is not asked");
        check(PoolPolicy.Order("a", [A("a"), A("b", usable: false)], null, now).SequenceEqual(["a"]), "a refused login is not asked");
        check(PoolPolicy.Order("a", [A("a", reset: now.AddDays(1)), A("b", reset: now.AddDays(3))], ["b"], now)[0] == "b",
              "a preferred account goes in front while it has room");
        check(PoolPolicy.HasRoom(new PoolLimits(0.99, now.AddMinutes(-1), 0, null, null, now), now),
              "a window whose reset has passed counts as empty");
        check(!PoolPolicy.HasRoom(new PoolLimits(0, null, 0, null, now.AddMinutes(5), now), now), "an account the service refused has no room until it said");
        check(PoolPolicy.Decide(429, [], true, now).Kind == PoolPolicy.OutcomeKind.Exhausted, "a 429 marks an account full");
        check(PoolPolicy.Decide(401, [], true, now).Kind == PoolPolicy.OutcomeKind.LoginRefused, "a 401 on a borrowed login marks it refused");
        check(PoolPolicy.Decide(401, [], false, now).Kind == PoolPolicy.OutcomeKind.Deliver, "a 401 on the sender's own login is the sender's to see");
        check(PoolPolicy.Decide(404, [], true, now).Kind == PoolPolicy.OutcomeKind.RefusedForAccount, "a 404 on a borrowed login sends the request home");
        var parsed = PoolLimits.Parse([("anthropic-ratelimit-unified-5h-utilization", "0.42"), ("anthropic-ratelimit-unified-7d-reset", "1800000000")], now);
        check(parsed?.FiveHour == 0.42 && parsed.WeekReset == DateTime.UnixEpoch.AddSeconds(1800000000), "limits are read from the service's headers");
        check(PoolLimits.Parse([("anthropic-ratelimit-unified-status", "rejected")], now)?.RefusedUntil != null, "a rejected status is a refusal");
        byte[] B(string s) => Encoding.UTF8.GetBytes(s);
        check(PoolRequest.MustStayWithSender(B("{\"content\":[{\"source\":{\"file_id\":\"f1\"}}]}"), null), "a request naming an uploaded file stays home");
        check(!PoolRequest.MustStayWithSender(B("{\"tools\":[{\"input_schema\":{\"properties\":{\"container\":{\"type\":\"object\"}}}}]}"), null),
              "a tool schema with a property named container does not pin a request");
        check(!PoolRequest.MustStayWithSender(B("{\"text\":\"what is a file_id?\"}"), null), "the words in a prompt pin nothing");
        check(PoolRequest.MustStayWithSender(B("{}"), "gzip"), "a body that cannot be read stays home");
        check(PoolRequest.CarriesBoundReasoning(B("{\"model\":\"claude-sonnet-5-5\",\"messages\":[{\"type\":\"thinking\"}]}")),
              "reasoning from a model that binds it holds a conversation where it is");
        check(!PoolRequest.CarriesBoundReasoning(B("{\"model\":\"claude-opus-5-5\",\"messages\":[{\"type\":\"thinking\"}]}")),
              "reasoning from a model measured to travel does not");
        var lender = Member("b", "acc-b", TokenB, TokenB);
        var sameAccountClaude = Member("b2", "acc-b", null, "other");
        var sender = Member("a", "acc-a", null, TokenA, ["b2", "b"]);
        var counted = PoolState.Candidates(sender, [sender, lender, sameAccountClaude]);
        check(counted.Count == 2 && counted[1].Id == "b", "one account is counted once, under the name of it that can lend");

        // ---- Over loopback ----
        var standIn = new StandIn();
        standIn.Start();
        var port = FreePort();
        var pipe = "ClaudeGraftTests." + Guid.NewGuid().ToString("N")[..8];
        var relay = new Relay(new Relay.Options(port, pipe, new Upstream(standIn.Port)));
        var running = relay.RunAsync();
        await Task.Delay(300);
        var authority = PoolCertificates.Authority();

        var first = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"messages\":[]}");
        check(first.Status == 200 && first.Body.Contains(TokenA), "an unconfigured relay sends every request as it came");
        check(Relay.Send(new PoolCommand(), pipe) is { Configured: false }, "the relay answers on its pipe");

        var config = new PoolConfiguration
        {
            Enabled = true,
            Members =
            [
                Member("a", "acc-a", null, TokenA, ["b", "c"], weekReset: now.AddDays(4)),
                Member("b", "acc-b", TokenB, TokenB, weekReset: now.AddDays(1)),
                Member("c", "acc-c", TokenC, TokenC, weekReset: now.AddDays(2)),
            ],
        };
        check(Relay.Send(new PoolCommand { Configure = config }, pipe) is { Configured: true, Enabled: true }, "the relay takes its configuration over the pipe");

        var lent = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"messages\":[]}", "s1");
        check(lent.Body.Contains(TokenB), "a model request goes out under the account whose week resets soonest");

        var stranger = await Ask(port, authority, "POST", "/v1/messages", Stranger, "{}");
        check(stranger.Body.Contains(Stranger), "a request nobody owns passes through with its own login");

        standIn.Seen.Clear();
        var models = await Ask(port, authority, "GET", "/v1/models?x=1", TokenA);
        check(models.Body.Contains(TokenA) && standIn.Seen.TryDequeue(out var seenGet) &&
              Encoding.ASCII.GetString(seenGet.Raw).Contains($"Authorization: Bearer {TokenA}"),
              "anything but a model request keeps the sender's login, untouched");

        var pinned = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"x\":{\"file_id\":\"f\"}}");
        check(pinned.Body.Contains(TokenA), "a request naming an uploaded file goes home");

        // A conversation stays while its cache lives.
        var turn1 = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"messages\":[]}", "kept");
        config.Members[2].Limits = new PoolLimits(0, null, 0, now.AddHours(1), null, DateTime.UtcNow.AddSeconds(1));
        Relay.Send(new PoolCommand { Configure = config }, pipe);
        var turn2 = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"messages\":[]}", "kept");
        check(turn1.Body.Contains(TokenB) && turn2.Body.Contains(TokenB),
              "a conversation stays on the account that answered it while its cache lives");
        var fresh = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"messages\":[]}", "new-one");
        check(fresh.Body.Contains(TokenC), "while a new conversation goes where the ranking says");

        // A choice from the menu goes in front.
        config.Assignments = new() { ["kept"] = "a" };
        Relay.Send(new PoolCommand { Configure = config }, pipe);
        var chosen = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"messages\":[]}", "kept");
        check(chosen.Body.Contains(TokenA), "a conversation moved from the menu goes to the account chosen");

        // A moved thread refused as not found is handed to Claude Code.
        config.Assignments = new() { ["thread"] = "c" };
        Relay.Send(new PoolCommand { Configure = config }, pipe);
        standIn.Answer = (auth, _, _) => auth == TokenC
            ? (404, "", "{\"type\":\"error\",\"error\":{\"type\":\"not_found_error\",\"message\":\"thread_not_found\"}}", false)
            : (200, "", "{\"servedBy\":\"" + auth + "\"}", false);
        var thread = await Ask(port, authority, "POST", "/v1/messages", TokenA,
            "{\"thread\":{\"type\":\"continue\",\"previous_message_id\":\"m\"}}", "thread");
        check(thread.Status == 404 && thread.Body.Contains("thread_not_found"),
              "thread_not_found from where a thread moved is Claude Code's to hear, so it starts it again there");
        standIn.Answer = (auth, _, _) => auth == TokenC ? (404, "", "{\"error\":{\"type\":\"not_found_error\"}}", false) : (200, "", "{\"servedBy\":\"" + auth + "\"}", false);
        var other404 = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"messages\":[]}", "thread");
        check(other404.Status == 200 && other404.Body.Contains(TokenA), "any other 404 on a borrowed login sends the request home");

        // A full account is passed over and remembered as full.
        config.Assignments = null;
        Relay.Send(new PoolCommand { Configure = config }, pipe);
        standIn.Answer = (auth, _, _) => auth == TokenC
            ? (429, "anthropic-ratelimit-unified-status: rejected\r\nanthropic-ratelimit-unified-reset: " +
                    ((long)(DateTime.UtcNow.AddHours(1) - DateTime.UnixEpoch).TotalSeconds) + "\r\n", "{}", false)
            : (200, "", "{\"servedBy\":\"" + auth + "\"}", false);
        var moved = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"messages\":[]}");
        check(moved.Body.Contains(TokenB), "an account that answers 429 is passed over for the next");
        standIn.Seen.Clear();
        var after = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"messages\":[]}");
        check(after.Body.Contains(TokenB) && !standIn.Seen.Any(s => s.Auth == TokenC), "a full account is not asked again until its reset");

        // A refused login is retried, never shown.
        standIn.Answer = (auth, _, _) => auth == TokenB ? (401, "", "{}", false) : (200, "", "{\"servedBy\":\"" + auth + "\"}", false);
        var refused = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"messages\":[]}");
        check(refused.Status == 200 && refused.Body.Contains(TokenA), "a borrowed login refused falls back to the sender's own");

        // Streams arrive whole.
        standIn.Answer = (auth, _, _) => (200, "", "{\"stream\":\"" + new string('x', 5000) + "\"}", true);
        var streamed = await Ask(port, authority, "POST", "/v1/messages", TokenA, "{\"messages\":[]}");
        check(streamed.Body.Contains(new string('x', 5000)), "a chunked answer is forwarded whole");

        var status = Relay.Send(new PoolCommand(), pipe);
        check(status?.Sessions.Any(s => s.Id == "kept" && s.ServedBy == "a") == true, "the relay lists the conversations it carried and who answered");
        check(status?.Members.Single(m => m.Id == "c").Limits?.RefusedUntil > DateTime.UtcNow, "an account that answered 429 is remembered as full until its reset");
        check(status?.Members.Single(m => m.Id == "b").TokenRefused == true, "a login the service refused is remembered as refused");

        // Node, which is what Claude Code runs on, trusts the relay through
        // NODE_EXTRA_CA_CERTS alone.
        await NodeProbe(check, port);

        // Stepping aside frees the port for a replacement at once.
        Relay.Send(new PoolCommand { Quit = true }, pipe);
        await Task.Delay(800);
        var replacement = new TcpListener(IPAddress.Loopback, port);
        var freed = false;
        try { replacement.Start(); freed = true; replacement.Stop(); } catch { }
        check(freed, "a relay stepping aside frees its port for the next one");
        await running.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
        standIn.Stop();
    }

    static async Task NodeProbe(Action<bool, string> check, int port)
    {
        var node = FindNode();
        if (node == null) { Console.WriteLine("(node not found: the Node check is skipped)"); return; }
        var script = Path.Combine(Path.GetTempPath(), "graft-node-" + Guid.NewGuid().ToString("N")[..6] + ".js");
        File.WriteAllText(script, """
            const net = require('net'), tls = require('tls');
            const [port, host] = [Number(process.argv[2]), process.argv[3]];
            const s = net.connect(port, '127.0.0.1', () => s.write(`CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: api.anthropic.com:443\r\n\r\n`));
            s.once('data', d => {
              if (!String(d).startsWith('HTTP/1.1 200')) { console.log('noconnect'); process.exit(2); }
              const t = tls.connect({ socket: s, servername: host }, () => { console.log(t.authorized ? 'trusted' : 'untrusted'); process.exit(0); });
              t.on('error', e => { console.log('error ' + e.code); process.exit(0); });
            });
            """);
        string Run(string host)
        {
            var info = new ProcessStartInfo(node, $"\"{script}\" {port} {host}")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            info.Environment["NODE_EXTRA_CA_CERTS"] = PoolCertificates.AuthorityFile;
            info.Environment.Remove("NODE_USE_SYSTEM_CA");
            using var p = Process.Start(info)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(15000);
            return output;
        }
        check(Run("api.anthropic.com") == "trusted", "Node trusts the relay as api.anthropic.com through NODE_EXTRA_CA_CERTS");
        check(Run("example.com").StartsWith("error"), "and refuses it under any other name");
        try { File.Delete(script); } catch { }
        await Task.CompletedTask;
    }

    static string? FindNode()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var candidate = Path.Combine(dir.Trim(), "node.exe"); if (File.Exists(candidate)) return candidate; } catch { }
        }
        return null;
    }
}
