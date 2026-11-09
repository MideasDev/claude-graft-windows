using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core.Pool;

/// An account signed in from the window only to lend: no Claude, no
/// profile. Its one login is a year-long `user:inference` token, sealed with
/// this Windows user's DPAPI; the refresh token that comes with it is never
/// read.
public sealed class PoolAccount
{
    public string Id { get; set; } = "";
    public string? AccountUuid { get; set; }
    public string? Email { get; set; }
    public string? OrganizationName { get; set; }
    public DateTime Expires { get; set; }
    public DateTime Added { get; set; }
    public string Sealed { get; set; } = "";

    public string Name => Email ?? OrganizationName ?? "Account";

    public string? Token()
    {
        try { return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(Sealed))); }
        catch { return null; }
    }
}

/// `usage-sharing.json`: lending belongs to the Claude that borrows. Each
/// Claude names the ones it may draw on; there is no pool to join.
public sealed class SharingSettings
{
    public Dictionary<string, List<string>> Lending { get; set; } = [];
    public Dictionary<string, string> Assignments { get; set; } = [];
    public List<PoolAccount> Accounts { get; set; } = [];

    static string FilePath => Path.Combine(Paths.GraftHome, "usage-sharing.json");
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static SharingSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<SharingSettings>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
        catch (Exception e) { Diagnostics.Note("pool.settings-unreadable", e.Message); return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.GraftHome);
        Fs.WriteAtomic(FilePath, JsonSerializer.SerializeToUtf8Bytes(this, Json));
    }

    public List<string> LendersOf(string claudeId) => Lending.GetValueOrDefault(claudeId) ?? [];
    public bool Borrows(string claudeId) => LendersOf(claudeId).Count > 0;
}

/// Claude Graft's side of shared usage: who the members are, the relay
/// process, and the lines in each closed profile.
public static class SharedUsage
{
    public const string MainId = "main";
    public const string AccountPrefix = "account:";

    public static string IdOf(Shortcut? s) => s == null ? MainId : s.Folder;

    public static string? ProfileOf(string id, Store store) =>
        id == MainId ? Paths.MainProfile : store.Find(id)?.ProfileDir;

    /// Everything the relay needs to know, read from each profile's token
    /// cache: every token it holds, to recognise its requests, and the one
    /// it lends (Claude Code's own scope, else inference), never a refresh
    /// token. Called only once some Claude borrows.
    public static PoolConfiguration Members(Store store, SharingSettings settings, Func<string, UsageFigure?>? usage = null)
    {
        var now = DateTime.UtcNow;
        var members = new List<PoolMember>();
        var claudes = new List<(string Id, string Name, string? Profile)> { (MainId, S.T("Claude (main)"), Paths.MainProfile) };
        claudes.AddRange(store.Data.Shortcuts.Select(s => (s.Folder, s.Name, (string?)s.ProfileDir)));
        foreach (var (id, name, profile) in claudes)
        {
            if (profile == null || !Directory.Exists(profile)) continue;
            var (tokens, _) = Credentials.Tokens(profile);
            var current = tokens?.Where(t => t.IsCurrent(now)).ToList() ?? [];
            var lending = current.Where(t => t.Scopes.Contains(UsagePool.LendingScope)).MaxBy(t => t.Expires)
                       ?? current.Where(t => t.Scopes.Contains(UsagePool.InferenceScope)).MaxBy(t => t.Expires);
            members.Add(new PoolMember
            {
                Id = id,
                Name = name,
                Account = Graft.Account(profile),
                Identify = tokens?.Select(t => UsagePool.Digest(t.Value)).Distinct().ToList() ?? [],
                Token = lending?.Value,
                Limits = usage?.Invoke(profile) is { } f ? PoolLimits.FromUsage(f) : null,
                BorrowsFrom = settings.LendersOf(id) is { Count: > 0 } lenders ? lenders : null,
            });
        }
        foreach (var account in settings.Accounts.Where(a => a.Expires > now))
        {
            members.Add(new PoolMember
            {
                Id = account.Id, Name = account.Name, Account = account.AccountUuid, Token = account.Token(),
            });
        }
        var assignments = settings.Assignments.Count > 0 ? new Dictionary<string, string>(settings.Assignments) : null;
        return new PoolConfiguration { Enabled = true, Members = members, Assignments = assignments };
    }

    // ---- The relay process ----

    /// Up and built from this copy of Claude Graft, replacing one built
    /// from another; started detached otherwise, and waited for.
    public static bool EnsureRelay()
    {
        var binary = Relay.Binary();
        var status = Relay.Send(new PoolCommand(), timeoutMs: 1500);
        if (status != null && (status.Binary == binary || binary.Length == 0)) return true;
        if (status != null)
        {
            Diagnostics.Note("relay.replacing", "built from another copy");
            Relay.Send(new PoolCommand { Quit = true }, timeoutMs: 1500);
            Thread.Sleep(600);
        }
        if (Environment.ProcessPath is not { } exe) return false;
        try
        {
            // Through the shell, so the relay holds none of this process's
            // handles and outlives it.
            Process.Start(new ProcessStartInfo(exe, "--relay") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Exception e)
        {
            Diagnostics.Note("relay.spawn-failed", e.Message);
            return false;
        }
        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(100);
            if (Relay.Send(new PoolCommand(), timeoutMs: 500) != null) return true;
        }
        return false;
    }

    /// Whether any profile still holds the lines: the relay may go only once
    /// none does, because a Claude Code pointed at a proxy that is not there
    /// reaches nothing.
    public static bool AnyHoldsLines(Store store) =>
        AllProfiles(store).Any(p => PoolEnvironment.Settle(p, wanted: false, relayReady: false, apply: false).HoldsLines);

    static IEnumerable<string> AllProfiles(Store store)
    {
        if (Paths.MainProfile is { } main) yield return main;
        foreach (var s in store.Data.Shortcuts) yield return s.ProfileDir;
    }

    /// On the way to opening a closed Claude: the lines in if it borrows and
    /// the relay can be reached or started, out otherwise.
    public static void PrepareLaunch(Shortcut? shortcut, Store store)
    {
        try
        {
            var profile = shortcut?.ProfileDir ?? Paths.MainProfile;
            if (profile == null) return;
            var settings = SharingSettings.Load();
            var wanted = settings.Borrows(IdOf(shortcut));
            var holding = PoolEnvironment.Settle(profile, wanted, false, apply: false).HoldsLines;
            if (!wanted && !holding) return;
            var ready = wanted && EnsureRelay();
            if (ready) Relay.Send(new PoolCommand { Configure = Members(store, settings) });
            var result = PoolEnvironment.Settle(profile, wanted, ready, apply: true);
            Diagnostics.Note("pool.launch", $"{IdOf(shortcut)} {result.State}");
        }
        catch (Exception e) { Diagnostics.Note("pool.launch-failed", e.Message); }
    }

    // ---- Signing in an account to lend ----

    const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    const string AuthorizeUrl = "https://claude.com/cai/oauth/authorize";
    const string ExchangeUrl = "https://platform.claude.com/v1/oauth/token";
    const int Lifetime = 365 * 24 * 60 * 60;

    static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// The browser hands its code to a listener on loopback alone, checked
    /// against a `state` of its own and a PKCE verifier; the code goes to
    /// platform.claude.com and nowhere else.
    public static async Task<(PoolAccount? Account, string? Problem)> SignInAsync(string? excludeAccount, CancellationToken cancel)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://localhost:{port}/callback";
        var verifier = RandomToken();
        var state = RandomToken();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string E(string v) => Uri.EscapeDataString(v);
        var url = $"{AuthorizeUrl}?code=true&client_id={E(ClientId)}&response_type=code&redirect_uri={E(redirect)}" +
                  $"&scope={E(UsagePool.InferenceScope)}&code_challenge={E(challenge)}&code_challenge_method=S256&state={E(state)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                var wire = new Wire(client.GetStream());
                var head = await wire.ReadHeadAsync(16384);
                if (head == null || !head.Value.Head.Target.StartsWith("/callback")) { await Respond(wire, 404, null); continue; }
                var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + head.Value.Head.Target).Query);
                if (query["state"] != state) { await Respond(wire, 400, S.T("This sign-in link is out of date. Start again from Claude Graft.")); continue; }
                if (query["code"] is not { } code)
                {
                    var why = query["error_description"] ?? query["error"] ?? S.T("The sign-in was declined.");
                    await Respond(wire, 200, why);
                    return (null, why);
                }
                var (account, problem) = await FinishAsync(code, redirect, verifier, state, excludeAccount);
                await Respond(wire, 200, account != null
                    ? S.F("{0} is signed in. You can close this tab and go back to Claude Graft.", account.Name)
                    : problem!);
                return (account, problem);
            }
        }
        catch (OperationCanceledException) { return (null, S.T("The sign-in took too long, or was cancelled.")); }
        finally { listener.Stop(); }
    }

    static async Task Respond(Wire wire, int status, string? message)
    {
        var page = message == null ? "" : "<!doctype html><meta charset=utf-8><title>Claude Graft</title>" +
            "<style>body{font:15px system-ui,sans-serif;display:grid;place-items:center;min-height:100vh;margin:0}p{max-width:28em;text-align:center}</style>" +
            "<p>" + System.Net.WebUtility.HtmlEncode(message) + "</p>";
        var body = Encoding.UTF8.GetBytes(page);
        var head = $"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        try { await wire.SendAsync([.. Encoding.ASCII.GetBytes(head), .. body]); } catch { }
        wire.Dispose();
    }

    static async Task<(PoolAccount?, string?)> FinishAsync(string code, string redirect, string verifier, string state, string? excludeAccount)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var request = new JsonObject
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect,
            ["client_id"] = ClientId, ["code_verifier"] = verifier, ["state"] = state, ["expires_in"] = Lifetime,
        };
        HttpResponseMessage response;
        try { response = await http.PostAsync(ExchangeUrl, new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json")); }
        catch (Exception e) { return (null, e.Message); }
        var text = await response.Content.ReadAsStringAsync();
        JsonObject? answer = null;
        try { answer = JsonNode.Parse(text) as JsonObject; } catch { }
        if (!response.IsSuccessStatusCode)
            return (null, S.F("The sign-in was refused ({0}).", (int)response.StatusCode));
        var token = answer?["access_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token)) return (null, S.T("The sign-in service sent something unexpected."));
        var uuid = answer?["account"]?["uuid"]?.GetValue<string>();
        if (uuid != null && uuid == excludeAccount)
            return (null, S.T("That is this Claude's own account, which has nothing to lend it."));
        var lifetime = answer?["expires_in"]?.GetValue<double>() ?? Lifetime;
        return (new PoolAccount
        {
            Id = AccountPrefix + (uuid ?? Guid.NewGuid().ToString()),
            AccountUuid = uuid,
            Email = answer?["account"]?["email_address"]?.GetValue<string>(),
            OrganizationName = answer?["organization"]?["name"]?.GetValue<string>(),
            Expires = DateTime.UtcNow.AddSeconds(lifetime),
            Added = DateTime.UtcNow,
            Sealed = Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(token))),
        }, null);
    }
}
