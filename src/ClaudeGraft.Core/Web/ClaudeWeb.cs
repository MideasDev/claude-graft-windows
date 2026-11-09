using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeGraft.Core.Web;

// Artifacts, projects and chat search across accounts, ported from the macOS
// original. None of claude.ai's requests used here is documented; every shape
// was read off claude.ai's own web client (November 2026), and each request
// is kept to exactly what that page sends. The original's notes/artifacts.md
// and notes/projects-and-search.md say how each was found.

/// One claude.ai sign-in, made in Claude Graft's own window and kept in a
/// browser profile of its own. Claude Graft never reads its cookie.
public sealed class WebAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Email { get; set; }
    public string? AccountUuid { get; set; }
    public string? Org { get; set; }

    public string Name => Email ?? S.T("Account");
}

public enum ArtifactRole { Reader, Commenter, Editor }

public static class Roles
{
    public static string Wire(ArtifactRole r) => r switch
    {
        ArtifactRole.Editor => "editor",
        ArtifactRole.Commenter => "commenter",
        _ => "reader",
    };

    public static ArtifactRole? Parse(string? text) => text switch
    {
        "reader" => ArtifactRole.Reader,
        "commenter" => ArtifactRole.Commenter,
        "editor" => ArtifactRole.Editor,
        _ => null,
    };

    public static string Label(ArtifactRole r) => r switch
    {
        ArtifactRole.Editor => S.T("Can edit"),
        ArtifactRole.Commenter => S.T("Can comment"),
        _ => S.T("Can view"),
    };

    /// A role an artifact does not take is lowered, never raised: an editor
    /// becomes a commenter where comments are on, a viewer otherwise.
    public static ArtifactRole Offered(ArtifactRole wanted, ArtifactApi.Permissions p) => wanted switch
    {
        ArtifactRole.Editor when p.Editors => ArtifactRole.Editor,
        ArtifactRole.Editor or ArtifactRole.Commenter => p.Commenters ? ArtifactRole.Commenter : ArtifactRole.Reader,
        _ => ArtifactRole.Reader,
    };
}

/// A group of people to share with: Claude Graft's own, since claude.ai has
/// groups only for team organizations.
public sealed class ArtifactGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<string> Emails { get; set; } = [];
    /// Only people who are not viewers: an address missing here is a viewer,
    /// so a group of viewers is written exactly as before roles existed, and
    /// an older reader of the file invites less than asked, never more.
    public Dictionary<string, string>? Roles { get; set; }

    public ArtifactRole RoleOf(string email) =>
        Web.Roles.Parse(Roles?.GetValueOrDefault(email)) ?? ArtifactRole.Reader;

    public void SetRole(string email, ArtifactRole role)
    {
        Roles ??= [];
        if (role == ArtifactRole.Reader) Roles.Remove(email); else Roles[email] = Web.Roles.Wire(role);
        if (Roles.Count == 0) Roles = null;
    }

    public void SetRoleForEveryone(ArtifactRole role)
    {
        Roles = null;
        foreach (var e in Emails) SetRole(e, role);
    }

    public ArtifactRole? CommonRole
    {
        get
        {
            var all = Emails.Select(RoleOf).Distinct().ToList();
            return Emails.Count == 0 ? ArtifactRole.Reader : all.Count == 1 ? all[0] : null;
        }
    }

    /// Addresses read the way people paste them: one per line, or out of a
    /// mail header, each once.
    public static List<string> ParseAddresses(string text)
    {
        var found = new List<string>();
        foreach (Match m in Regex.Matches(text, @"[A-Za-z0-9._%+\-']+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}"))
        {
            var email = m.Value.Trim('.').ToLowerInvariant();
            if (!found.Contains(email)) found.Add(email);
        }
        return found;
    }

    /// New addresses join with the role everyone in the group already has.
    public void Add(IEnumerable<string> addresses)
    {
        var common = CommonRole ?? ArtifactRole.Reader;
        foreach (var e in addresses)
        {
            if (Emails.Contains(e)) continue;
            Emails.Add(e);
            SetRole(e, common);
        }
    }
}

public sealed record Artifact(string Id, Guid Account, string Org, string Title, string Kind, string? OpenUrl,
                              string Audience, string Rel, DateTime? SortTime, string? OwnerAccount,
                              bool Renamable, bool Duplicable, bool DuplicateAvailable)
{
    public bool IsOwn => Rel == "mine";

    public string AccessLabel => Audience switch
    {
        "public" or "link" => S.T("Anyone with the link"),
        "org" or "space" => S.T("Your organization"),
        "users" => S.T("Only people invited"),
        _ => S.T("Only you"),
    };

    public bool IsPublic => Audience is "public" or "link";
}

public sealed record ClaudeProject(string Id, Guid Account, string Name, string Summary, bool IsPrivate, bool IsStarred,
                                   bool IsExample, string? Creator, DateTime? Updated)
{
    public string OpenUrl => "https://claude.ai/project/" + Id;
}

public enum HitKind { Chat, Code }

public sealed record SearchHit(string Id, Guid Account, HitKind Kind, string Title, string Snippet,
                               IReadOnlyList<(int Start, int End)> SnippetMatches, string? Project, DateTime? Updated,
                               bool IsArchived, bool IsStarred, bool InCloud, int Rank, string OpenUrl);

public sealed record CodeSession(string Id, string Title, bool IsArchived, bool InCloud, DateTime? Updated, string? Repository);

public static class ArtifactApi
{
    public const string Origin = "https://claude.ai";
    public const string AccountPath = "/api/account";
    public const string SignInUrl = "https://claude.ai/login";
    public const int PageSize = 48;
    public const int MaximumPages = 5;
    public const long MaximumExportBytes = 200L * 1024 * 1024;

    /// Every `/api/frame/` request carries these, and no other request does:
    /// without the first, the service answers 404 to a request otherwise
    /// identical to the page's, which reads like an artifact that does not exist.
    public static Dictionary<string, string> HeadersFor(string path) =>
        path.StartsWith("/api/frame/", StringComparison.Ordinal)
            ? new() { ["X-Frame-CP"] = "go", ["X-Frame-Platform"] = "web" }
            : [];

    static string E(string s) => Uri.EscapeDataString(s);
    static string Query(params (string, string)[] pairs) => string.Join("&", pairs.Select(p => E(p.Item1) + "=" + E(p.Item2)));

    public static string ListPath(string rel, string org, string? after = null)
    {
        var q = new List<(string, string)> { ("rel", rel), ("empty", "exclude"), ("limit", PageSize.ToString()), ("org", org), ("sort", "last_viewed") };
        if (after != null) q.Add(("page_token", after));
        return "/api/frame/artifacts?" + Query([.. q]);
    }

    public static string PermPath(string id, string org) => $"/api/frame/perm/{E(id)}?{Query(("org", org))}";
    public static string InvitePath(string id, string org) => $"/api/frame/invite/{E(id)}?{Query(("org", org))}";
    public static string RetitlePath(string id, string org) => $"/api/frame/retitle/{E(id)}?{Query(("org", org))}";
    public static string DuplicatePath(string id, string org) => $"/api/frame/duplicate/{E(id)}?{Query(("org", org))}";
    public static string ArtifactPath(string id, string org) => $"/api/frame/{E(id)}?{Query(("org", org))}";

    public static string InviteBody(string email, ArtifactRole role) =>
        new JsonObject { ["email"] = email, ["role"] = Roles.Wire(role) }.ToJsonString();

    public static string RetitleBody(string title) => new JsonObject { ["title"] = title }.ToJsonString();

    /// Closing an artifact sends `users` with whoever was invited, or
    /// `owner` when nobody was, as the share panel does.
    public static string ReadModeBody(bool isPublic, IReadOnlyList<string> invited)
    {
        JsonObject read = isPublic ? new() { ["mode"] = "public" }
            : invited.Count == 0 ? new() { ["mode"] = "owner" }
            : new() { ["mode"] = "users", ["users"] = new JsonArray(invited.Select(i => (JsonNode)i).ToArray()) };
        return new JsonObject { ["read"] = read }.ToJsonString();
    }

    public sealed record Identity(string AccountUuid, string Email, string Org);

    /// The organization whose capabilities include chat: an account also in
    /// a team belongs to both, and the first listed is not reliably the right one.
    public static Identity? ParseIdentity(string json)
    {
        try
        {
            var o = JsonNode.Parse(json) as JsonObject;
            var uuid = o?["uuid"]?.GetValue<string>();
            var email = o?["email_address"]?.GetValue<string>();
            if (uuid == null || email == null || o?["memberships"] is not JsonArray memberships) return null;
            var orgs = memberships.Select(m => m?["organization"] as JsonObject).OfType<JsonObject>().ToList();
            var chat = orgs.FirstOrDefault(org => org["capabilities"] is JsonArray caps && caps.Any(c => c?.GetValue<string>() == "chat"))
                       ?? orgs.FirstOrDefault();
            var orgId = chat?["uuid"]?.GetValue<string>();
            return orgId == null ? null : new Identity(uuid, email, orgId);
        }
        catch { return null; }
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    static DateTime? Date(JsonNode? n) => Str(n) is { } t &&
        DateTime.TryParse(t, System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static (List<Artifact> Artifacts, string? Next)? ParsePage(string json, Guid account, string org)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o["artifacts"] is not JsonArray rows) return null;
            var list = new List<Artifact>();
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (Str(row["id"]) is not { } id) continue;
                var title = Str(row["title"]);
                list.Add(new Artifact(id, account, org, string.IsNullOrEmpty(title) ? S.T("Untitled") : title,
                    Str(row["kind"]) ?? "page", Str(row["open_url"]), Str(row["audience"]) ?? "owner", Str(row["rel"]) ?? "mine",
                    Date(row["sort_time"]) ?? Date(row["updated_at"]), Str(row["owner_account"]),
                    Bool(row["renamable"]) ?? false, Bool(row["duplicable"]) ?? false, Bool(row["duplicate_available"]) ?? true));
            }
            var next = Str(o["next_page_token"]);
            return (list, string.IsNullOrEmpty(next) ? null : next);
        }
        catch { return null; }
    }

    public sealed record Permissions(string? Version, List<string> Invited, bool Commenters, bool Editors);

    /// A flag that is missing offers nothing.
    public static Permissions? ParsePermissions(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var invited = (o["read"]?["users"] as JsonArray)?.Select(Str).OfType<string>().ToList() ?? [];
            var version = Str(o["version"]) ?? (o["version"] is JsonValue v && v.TryGetValue<long>(out var n) ? n.ToString() : null);
            return new Permissions(version, invited, Bool(o["commentersEnabled"]) ?? false,
                                   Bool(o["editorsEnabled"]) ?? Bool(o["writersEnabled"]) ?? false);
        }
        catch { return null; }
    }

    public static string? Reason(string json)
    {
        try
        {
            var o = JsonNode.Parse(json);
            return Str(o?["reason"]) ?? Str(o?["error"]?["reason"]);
        }
        catch { return null; }
    }

    public sealed record Source(string Version, string Token, string? Title, List<string> Files);

    public static Source? ParseSource(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var token = Str(o["assetToken"]);
            var version = Str(o["ver"]) ?? (o["ver"] is JsonValue v && v.TryGetValue<long>(out var n) ? n.ToString() : null);
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(version)) return null;
            var files = new List<string> { "index.html" };
            foreach (var f in (o["files"] as JsonArray ?? []).OfType<JsonObject>())
                if (Str(f["path"]) is { } path && IsSafeFilePath(path) && !files.Contains(path)) files.Add(path);
            return new Source(version, token, Str(o["title"]), files);
        }
        catch { return null; }
    }

    /// A path that stays inside the folder it is written into.
    public static bool IsSafeFilePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':')) return false;
        return path.Split('/').All(p => p.Length > 0 && p != "." && p != "..");
    }

    /// Where the viewer reads a file from: the file host, with a token of a
    /// few minutes in the address and no cookie.
    public static string? FileUrl(string id, Source source, string path)
    {
        var host = new string(id.ToLowerInvariant().Where(c => Uri.IsHexDigit(c) || c == '-').ToArray());
        if (host.Length == 0 || !IsSafeFilePath(path)) return null;
        var encoded = string.Join("/", path.Split('/').Select(E));
        return $"https://{host}.frame.claudeusercontent.com/_f/{E(source.Version)}/_src/{encoded}?{Query(("__frame_t", source.Token))}";
    }

    /// One artifact listed once, as the account's own where it is, newest first.
    public static List<Artifact> Merge(IEnumerable<IEnumerable<Artifact>> lists)
    {
        var merged = new List<Artifact>();
        var seen = new Dictionary<string, int>();
        foreach (var a in lists.SelectMany(l => l))
        {
            if (seen.TryGetValue(a.Id, out var i))
            {
                if (a.IsOwn && !merged[i].IsOwn) merged[i] = a;
                continue;
            }
            seen[a.Id] = merged.Count;
            merged.Add(a);
        }
        return [.. merged.OrderByDescending(a => a.SortTime ?? DateTime.MinValue)];
    }
}

public static class ProjectsApi
{
    public const int ProjectPageSize = 30;
    public const int MaximumProjectPages = 5;
    public const int SearchLimit = 25;
    public const int CodePageSize = 200;
    public const int MaximumCodePages = 10;

    static string E(string s) => Uri.EscapeDataString(s);

    public static string ProjectsPath(string org, int offset = 0) =>
        $"/api/organizations/{E(org)}/projects_v2?include_harmony_projects=true&limit={ProjectPageSize}&offset={offset}&order_by=latest_chat";

    public static string SearchPath(string org) => $"/api/organizations/{E(org)}/conversation/search/v2";

    public static string SearchBody(string query) =>
        new JsonObject { ["n"] = SearchLimit, ["query"] = query, ["target_snippet_size"] = 100 }.ToJsonString();

    public static string CodeSessionsPath(string? cursor = null) =>
        $"/v1/code/sessions?limit={CodePageSize}" + (cursor != null ? "&cursor=" + E(cursor) : "");

    /// Without these the session list answers 400.
    public static Dictionary<string, string> CodeHeaders(string org) => new()
    {
        ["anthropic-beta"] = "ccr-byoc-2025-07-29", ["anthropic-version"] = "2023-06-01", ["x-organization-uuid"] = org,
    };

    /// Listed as `cse_X`, opened at `/code/session_X`.
    public static string CodeSessionUrl(string id) =>
        "https://claude.ai/code/" + (id.StartsWith("cse_") ? "session_" + id[4..] : id);

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    static DateTime? Date(JsonNode? n) => Str(n) is { } t &&
        DateTime.TryParse(t, System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// An archived project is off the Projects page, so it is off here.
    public static (List<ClaudeProject> Projects, bool HasMore)? ParseProjects(string json, Guid account)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o["data"] is not JsonArray rows) return null;
            var list = new List<ClaudeProject>();
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (Str(row["uuid"]) is not { } id) continue;
                if (row["archived_at"] is JsonNode archived && archived.GetValueKind() != JsonValueKind.Null) continue;
                var name = Str(row["name"]);
                list.Add(new ClaudeProject(id, account, string.IsNullOrEmpty(name) ? S.T("Untitled") : name,
                    Str(row["description"]) ?? "", Bool(row["is_private"]) ?? true, Bool(row["is_starred"]) ?? false,
                    Bool(row["is_starter_project"]) ?? false, Str(row["creator"]?["full_name"]), Date(row["updated_at"])));
            }
            return (list, Bool(o["pagination"]?["has_more"]) ?? false);
        }
        catch { return null; }
    }

    static List<(int, int)> Ranges(JsonNode? node) =>
        (node as JsonArray)?.OfType<JsonObject>()
            .Select(r => (r["start"]?.GetValue<int>() ?? 0, r["end"]?.GetValue<int>() ?? 0))
            .Where(r => r.Item2 > r.Item1).ToList() ?? [];

    /// Archived chats come back with the rest, marked; the page hides them
    /// until asked, and asking sends nothing new.
    public static List<SearchHit>? ParseChats(string json, Guid account)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o["data"] is not JsonArray rows) return null;
            var hits = new List<SearchHit>();
            var index = 0;
            foreach (var row in rows.OfType<JsonObject>())
            {
                var conversation = row["conversation"] as JsonObject;
                if (Str(conversation?["uuid"]) is not { } id) { index++; continue; }
                var title = Str(conversation!["name"]);
                var rank = row["semantic_rank"] is JsonValue r && r.TryGetValue<int>(out var rk) ? rk : index;
                hits.Add(new SearchHit(id, account, HitKind.Chat, string.IsNullOrEmpty(title) ? S.T("Untitled") : title,
                    Str(row["matched_snippet"]?["text"]) ?? "", Ranges(row["matched_snippet"]?["matches"]),
                    Str(conversation["project_uuid"]), Date(conversation["updated_at"]),
                    Bool(conversation["is_archived"]) ?? false, Bool(conversation["is_starred"]) ?? false, false, rank,
                    "https://claude.ai/chat/" + id));
                index++;
            }
            return hits;
        }
        catch { return null; }
    }

    public static (List<CodeSession> Sessions, string? Next)? ParseCodeSessions(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o["data"] is not JsonArray rows) return null;
            var list = new List<CodeSession>();
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (Str(row["id"]) is not { } id) continue;
                var title = Str(row["title"]);
                list.Add(new CodeSession(id, string.IsNullOrEmpty(title) ? S.T("Untitled") : title,
                    Str(row["status"]) == "archived", Str(row["environment_kind"]) == "anthropic_cloud",
                    Date(row["last_event_at"]) ?? Date(row["updated_at"]),
                    Str((row["config"]?["sources"] as JsonArray)?.FirstOrDefault()?["url"])));
            }
            var next = Str(o["next_cursor"]);
            return (list, string.IsNullOrEmpty(next) ? null : next);
        }
        catch { return null; }
    }

    /// The Code tab's search: every word of the query somewhere in the
    /// title or the repository, matched in the title for highlighting.
    public static bool CodeMatches(string query, CodeSession s)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var haystack = (s.Title + "\n" + s.Repository).ToLowerInvariant();
        return words.All(haystack.Contains);
    }
}

/// `artifact-accounts.json` and `artifact-groups.json`.
public sealed class WebStore
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    static string AccountsFile => Path.Combine(Paths.GraftHome, "artifact-accounts.json");
    static string GroupsFile => Path.Combine(Paths.GraftHome, "artifact-groups.json");
    public static string ProfilesDir => Path.Combine(Paths.GraftHome, "web");
    public static string ProfileOf(Guid id) => Path.Combine(ProfilesDir, id.ToString("N"));

    public static List<WebAccount> LoadAccounts() => Load<List<WebAccount>>(AccountsFile) ?? [];
    public static void SaveAccounts(List<WebAccount> accounts) => Save(AccountsFile, accounts);
    public static List<ArtifactGroup> LoadGroups() => Load<List<ArtifactGroup>>(GroupsFile) ?? [];
    public static void SaveGroups(List<ArtifactGroup> groups) => Save(GroupsFile, groups);

    static T? Load<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; }
        catch (Exception e) { Diagnostics.Note("web.store-unreadable", $"{Path.GetFileName(path)} {e.Message}"); return default; }
    }

    static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Paths.GraftHome);
        Fs.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    }
}
