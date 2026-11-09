using ClaudeGraft.Core;
using ClaudeGraft.Core.Web;

namespace ClaudeGraft.Web;

/// The claude.ai accounts and everything asked of them: one sign-in serves
/// Artifacts, Projects and Search Chats, so an account signed in or out from
/// any of the three is signed in or out in all of them. Nothing here runs on
/// a timer: lists are asked for when a page is shown or refreshed, a search
/// on Return or the button.
sealed class WebCenter
{
    public List<WebAccount> Accounts { get; private set; } = WebStore.LoadAccounts();
    public List<ArtifactGroup> Groups { get; private set; } = WebStore.LoadGroups();
    /// Accounts whose sign-in has lapsed since the last time they answered.
    public HashSet<Guid> SignedOut { get; } = [];
    public event Action? Changed;

    readonly Dictionary<Guid, WebSession> sessions = [];

    public WebCenter() => SweepRemovedProfiles();

    WebSession Session(WebAccount a)
    {
        if (!sessions.TryGetValue(a.Id, out var s)) sessions[a.Id] = s = new WebSession(a);
        return s;
    }

    void Notify() => Changed?.Invoke();

    // ---- Accounts ----

    public WebAccount? SignIn(IWin32Window owner)
    {
        var id = Guid.NewGuid();
        ArtifactApi.Identity? identity;
        using (var window = new SignInWindow(id))
        {
            window.ShowDialog(owner);
            identity = window.Result;
        }
        if (identity == null) { RemoveProfile(id); return null; }

        // The same account signed in twice keeps its first profile, so it is
        // never listed under two names.
        if (Accounts.FirstOrDefault(a => a.AccountUuid == identity.AccountUuid) is { } existing)
        {
            RemoveProfile(id);
            SignedOut.Remove(existing.Id);
            sessions.Remove(existing.Id, out var old);
            old?.Dispose();
            existing.Org = identity.Org;
            existing.Email = identity.Email;
            WebStore.SaveAccounts(Accounts);
            Notify();
            return existing;
        }
        var account = new WebAccount { Id = id, Email = identity.Email, AccountUuid = identity.AccountUuid, Org = identity.Org };
        Accounts.Add(account);
        WebStore.SaveAccounts(Accounts);
        Diagnostics.Note("artifacts.signin", "account added");
        Notify();
        return account;
    }

    /// Signing out removes the account's whole browser profile, cookie and all.
    public void SignOut(WebAccount account)
    {
        if (sessions.Remove(account.Id, out var s)) s.Dispose();
        Accounts.Remove(account);
        SignedOut.Remove(account.Id);
        WebStore.SaveAccounts(Accounts);
        RemoveProfile(account.Id);
        Notify();
    }

    /// The browser can hold a profile's files for a moment after it closes;
    /// a folder that will not go yet is marked and removed at the next start.
    static void RemoveProfile(Guid id)
    {
        var dir = WebStore.ProfileOf(id);
        if (!Directory.Exists(dir)) return;
        Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                try { Directory.Delete(dir, true); return; }
                catch { await Task.Delay(1000); }
            }
            try { File.WriteAllText(Path.Combine(dir, ".remove"), ""); } catch { }
        });
    }

    void SweepRemovedProfiles()
    {
        try
        {
            if (!Directory.Exists(WebStore.ProfilesDir)) return;
            var kept = Accounts.Select(a => a.Id.ToString("N")).ToHashSet();
            foreach (var dir in Directory.GetDirectories(WebStore.ProfilesDir))
                if (!kept.Contains(Path.GetFileName(dir)))
                    try { Directory.Delete(dir, true); } catch { }
        }
        catch { }
    }

    async Task<T?> Ask<T>(WebAccount a, Func<WebSession, Task<T>> work, List<string> problems)
    {
        try
        {
            var result = await work(Session(a));
            if (SignedOut.Remove(a.Id)) Notify();
            return result;
        }
        catch (SignedOutException)
        {
            if (SignedOut.Add(a.Id)) Notify();
            problems.Add(S.F("{0} needs to sign in again.", a.Name));
        }
        catch (Exception e)
        {
            problems.Add($"{a.Name}: {e.Message}");
            Diagnostics.Note("web.request-failed", e.GetType().Name);
        }
        return default;
    }

    // ---- Artifacts ----

    public async Task<(List<Artifact> Artifacts, List<string> Problems)> ListArtifacts()
    {
        var problems = new List<string>();
        var lists = new List<List<Artifact>>();
        foreach (var a in Accounts.ToList())
        {
            if (a.Org is not { } org) continue;
            var got = await Ask(a, async s =>
            {
                var all = new List<Artifact>();
                foreach (var rel in new[] { "mine", "shared" })
                {
                    string? token = null;
                    for (var page = 0; page < ArtifactApi.MaximumPages; page++)
                    {
                        var (status, body) = await s.Send("GET", ArtifactApi.ListPath(rel, org, token));
                        if (status != 200 || ArtifactApi.ParsePage(body, a.Id, org) is not { } parsed) break;
                        all.AddRange(parsed.Artifacts);
                        if ((token = parsed.Next) == null) break;
                    }
                }
                return all;
            }, problems);
            if (got != null) lists.Add(got);
        }
        return (ArtifactApi.Merge(lists), problems);
    }

    WebAccount AccountOf(Artifact a) => Accounts.First(x => x.Id == a.Account);

    async Task<string?> Act(Artifact a, Func<WebSession, Task<string?>> work)
    {
        var problems = new List<string>();
        var answer = await Ask(AccountOf(a), work, problems);
        return problems.FirstOrDefault() ?? answer;
    }

    static string Failed(int status, string body) =>
        ArtifactApi.Reason(body) is { } reason ? S.F("claude.ai refused it ({0}).", reason) : S.F("claude.ai answered {0}.", status);

    public Task<string?> Rename(Artifact a, string title) => Act(a, async s =>
    {
        var (status, body) = await s.Send("POST", ArtifactApi.RetitlePath(a.Id, a.Org), ArtifactApi.RetitleBody(title));
        return status is >= 200 and < 300 ? null
             : ArtifactApi.Reason(body) == "title_invalid" ? S.T("claude.ai will not take that title.") : Failed(status, body);
    });

    public Task<string?> Duplicate(Artifact a) => Act(a, async s =>
    {
        var (status, body) = await s.Send("POST", ArtifactApi.DuplicatePath(a.Id, a.Org));
        return status is >= 200 and < 300 ? null : Failed(status, body);
    });

    /// claude.ai keeps no bin for artifacts; one already gone is not an error.
    public Task<string?> Delete(Artifact a) => Act(a, async s =>
    {
        var (status, body) = await s.Send("DELETE", ArtifactApi.ArtifactPath(a.Id, a.Org));
        return status is >= 200 and < 300 || (status == 404 && ArtifactApi.Reason(body) == "not_found") ? null : Failed(status, body);
    });

    public async Task<(ArtifactApi.Permissions? Permissions, string? Problem)> PermissionsOf(Artifact a)
    {
        ArtifactApi.Permissions? found = null;
        var problem = await Act(a, async s =>
        {
            var (status, body) = await s.Send("GET", ArtifactApi.PermPath(a.Id, a.Org));
            if (status != 200) return Failed(status, body);
            found = ArtifactApi.ParsePermissions(body);
            return found == null ? S.T("claude.ai sent something unexpected.") : null;
        });
        return (found, problem);
    }

    /// Opening sends public; closing sends whoever was invited, or owner.
    public Task<string?> SetPublic(Artifact a, bool open) => Act(a, async s =>
    {
        var (status, body) = await s.Send("GET", ArtifactApi.PermPath(a.Id, a.Org));
        if (status != 200 || ArtifactApi.ParsePermissions(body) is not { } p) return Failed(status, body);
        var headers = p.Version != null ? new Dictionary<string, string> { ["If-Match"] = p.Version } : null;
        var (changed, answer) = await s.Send("PATCH", ArtifactApi.PermPath(a.Id, a.Org), ArtifactApi.ReadModeBody(open, p.Invited), headers);
        return changed is >= 200 and < 300 ? null : Failed(changed, answer);
    });

    public sealed record InviteResult(int Sent, int Lowered, List<string> Failed);

    /// Each person invited in turn, with the most of their role the artifact
    /// takes; one that fails does not stop the rest. claude.ai emails them.
    public async Task<InviteResult> Invite(Artifact a, IReadOnlyList<(string Email, ArtifactRole Role)> people)
    {
        var failed = new List<string>();
        int sent = 0, lowered = 0;
        var (permissions, problem) = await PermissionsOf(a);
        if (permissions == null) return new(0, 0, [problem ?? S.T("claude.ai sent something unexpected.")]);
        foreach (var (email, wanted) in people)
        {
            var role = Roles.Offered(wanted, permissions);
            var error = await Act(a, async s =>
            {
                var (status, body) = await s.Send("POST", ArtifactApi.InvitePath(a.Id, a.Org), ArtifactApi.InviteBody(email, role));
                return status is >= 200 and < 300 ? null : Failed(status, body);
            });
            if (error != null) failed.Add($"{email}: {error}");
            else { sent++; if (role != wanted) lowered++; }
        }
        return new(sent, lowered, failed);
    }

    public void SaveGroups() { WebStore.SaveGroups(Groups); Notify(); }

    // ---- Projects ----

    public async Task<(List<ClaudeProject> Projects, List<string> Problems)> ListProjects()
    {
        var problems = new List<string>();
        var all = new List<ClaudeProject>();
        foreach (var a in Accounts.ToList())
        {
            if (a.Org is not { } org) continue;
            var got = await Ask(a, async s =>
            {
                var list = new List<ClaudeProject>();
                for (var page = 0; page < ProjectsApi.MaximumProjectPages; page++)
                {
                    var (status, body) = await s.Send("GET", ProjectsApi.ProjectsPath(org, page * ProjectsApi.ProjectPageSize));
                    if (status != 200 || ProjectsApi.ParseProjects(body, a.Id) is not { } parsed) break;
                    list.AddRange(parsed.Projects);
                    if (!parsed.HasMore) break;
                }
                return list;
            }, problems);
            if (got != null) all.AddRange(got);
        }
        return ([.. all.OrderByDescending(p => p.Updated ?? DateTime.MinValue)], problems);
    }

    // ---- Search ----

    readonly Dictionary<Guid, (DateTime At, List<CodeSession> Sessions)> codeCache = [];

    /// One POST per account, plus its Code session list, kept two minutes so
    /// a second search in a row does not walk it again.
    public async Task<(List<SearchHit> Hits, List<string> Problems)> Search(string query)
    {
        var problems = new List<string>();
        var hits = new List<SearchHit>();
        foreach (var a in Accounts.ToList())
        {
            if (a.Org is not { } org) continue;
            var chats = await Ask(a, async s =>
            {
                var (status, body) = await s.Send("POST", ProjectsApi.SearchPath(org), ProjectsApi.SearchBody(query));
                return status == 200 ? ProjectsApi.ParseChats(body, a.Id) ?? [] : [];
            }, problems);
            if (chats != null) hits.AddRange(chats);

            var sessions = await Ask(a, async s =>
            {
                if (codeCache.TryGetValue(a.Id, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(2)) return cached.Sessions;
                var list = new List<CodeSession>();
                string? cursor = null;
                for (var page = 0; page < ProjectsApi.MaximumCodePages; page++)
                {
                    var (status, body) = await s.Send("GET", ProjectsApi.CodeSessionsPath(cursor), null, ProjectsApi.CodeHeaders(org));
                    if (status != 200 || ProjectsApi.ParseCodeSessions(body) is not { } parsed) break;
                    list.AddRange(parsed.Sessions);
                    if ((cursor = parsed.Next) == null) break;
                }
                codeCache[a.Id] = (DateTime.UtcNow, list);
                return list;
            }, problems);
            if (sessions != null)
                hits.AddRange(sessions.Where(s => ProjectsApi.CodeMatches(query, s)).Select((s, i) =>
                    new SearchHit(s.Id, a.Id, HitKind.Code, s.Title, s.Repository ?? "", [], null, s.Updated,
                                  s.IsArchived, false, s.InCloud, 1000 + i, ProjectsApi.CodeSessionUrl(s.Id))));
        }
        return (hits, problems);
    }

    // ---- Exporting ----

    /// An artifact's files, read as its viewer reads them: the version and a
    /// short-lived token from claude.ai, then each file from the file host
    /// with no cookie, into a folder, at most 200MB, never a path outside it.
    public async Task<(string? Folder, string? Title, string? Problem)> FetchFiles(Artifact a)
    {
        string? folder = null, title = null;
        var problem = await Act(a, async s =>
        {
            var (status, body) = await s.Send("GET", ArtifactApi.ArtifactPath(a.Id, a.Org));
            if (status != 200 || ArtifactApi.ParseSource(body) is not { } source) return Failed(status, body);
            title = source.Title ?? a.Title;
            var dir = Path.Combine(Path.GetTempPath(), "ClaudeGraft-export-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            long total = 0;
            foreach (var path in source.Files)
            {
                if (ArtifactApi.FileUrl(a.Id, source, path) is not { } url) continue;
                var (code, data) = await s.Download(url);
                if (code != 200) { if (path == "index.html") return S.F("claude.ai answered {0}.", code); continue; }
                total += data.Length;
                if (total > ArtifactApi.MaximumExportBytes) return S.T("That artifact is larger than 200 MB.");
                var target = Path.GetFullPath(Path.Combine(dir, path.Replace('/', '\\')));
                if (!target.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, data);
            }
            folder = dir;
            return null;
        });
        return (folder, title, problem);
    }

    public void Dispose()
    {
        foreach (var s in sessions.Values) s.Dispose();
        sessions.Clear();
    }
}
