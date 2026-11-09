using System.Text;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core.Pool;

/// What an account has spent, as the service's own headers say it:
/// fractions, resets in Unix seconds.
public sealed record PoolLimits(double FiveHour, DateTime? FiveHourReset, double Week, DateTime? WeekReset,
                                DateTime? RefusedUntil, DateTime Observed)
{
    const string Prefix = "anthropic-ratelimit-unified-";

    public static PoolLimits? Parse(IReadOnlyList<(string Name, string Value)> headers, DateTime now)
    {
        string? Value(string name) => headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        double? Number(string name) => double.TryParse(Value(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        DateTime? Date(string name) => Number(name) is { } s ? DateTime.UnixEpoch.AddSeconds(s) : null;

        var five = Number(Prefix + "5h-utilization");
        var week = Number(Prefix + "7d-utilization");
        var status = Value(Prefix + "status");
        if (five == null && week == null && status == null) return null;
        DateTime? refused = status == "rejected" ? Date(Prefix + "reset") ?? now.AddSeconds(60) : null;
        return new(five ?? 0, Date(Prefix + "5h-reset"), week ?? 0, Date(Prefix + "7d-reset"), refused, now);
    }

    public static PoolLimits FromUsage(UsageFigure f) =>
        new((f.FiveHour ?? 0) / 100.0, f.FiveHourReset, f.Week / 100.0, f.WeekReset, null, f.Taken);
}

/// Which account answers. Pure, so the suite can drive every branch.
public static class PoolPolicy
{
    public const double FiveHourCeiling = 0.98;
    public const double WeekCeiling = 0.99;
    public static readonly TimeSpan ShortCache = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LongCache = TimeSpan.FromHours(1);

    /// A conversation stays while its prompt cache lives: five minutes from
    /// its last turn, or an hour when it asked for one.
    public static TimeSpan CacheLifetime(byte[] body) =>
        Bytes.Contains(body, "\"ttl\":\"1h\"") ? LongCache : ShortCache;

    public sealed record Account(string Id, PoolLimits? Limits, bool Usable);

    public static DateTime? NextWeekReset(DateTime? reset, DateTime now)
    {
        if (reset is not { } r) return null;
        while (r <= now) r = r.AddDays(7);
        return r;
    }

    /// Room: not refused, under 98% of five hours and 99% of the week, a
    /// window whose reset has passed counting as empty.
    public static bool HasRoom(PoolLimits? limits, DateTime now)
    {
        if (limits == null) return true;
        if (limits.RefusedUntil is { } until && until > now) return false;
        if (limits.FiveHour >= FiveHourCeiling && (limits.FiveHourReset ?? DateTime.MaxValue) > now) return false;
        if (limits.Week >= WeekCeiling && (limits.WeekReset ?? DateTime.MaxValue) > now) return false;
        return true;
    }

    /// Soonest weekly reset first, unknown after known, a tie to the sender;
    /// anything preferred goes in front while it has room; the sender last
    /// when it has none, so the request then goes out as sent.
    public static List<string> Order(string own, IEnumerable<Account> accounts, IReadOnlyList<string>? prefer, DateTime now)
    {
        var open = accounts.Where(a => a.Usable && HasRoom(a.Limits, now)).ToList();
        open.Sort((a, b) =>
        {
            var ra = NextWeekReset(a.Limits?.WeekReset, now);
            var rb = NextWeekReset(b.Limits?.WeekReset, now);
            if (ra != null && rb != null && ra != rb) return ra.Value.CompareTo(rb.Value);
            if (ra != null && rb == null) return -1;
            if (ra == null && rb != null) return 1;
            if ((a.Id == own) != (b.Id == own)) return a.Id == own ? -1 : 1;
            return string.CompareOrdinal(a.Id, b.Id);
        });
        var ids = open.Select(a => a.Id).ToList();
        foreach (var id in (prefer ?? []).Reverse())
        {
            var index = ids.IndexOf(id);
            if (index >= 0) { ids.RemoveAt(index); ids.Insert(0, id); }
        }
        if (!ids.Contains(own)) ids.Add(own);
        return ids;
    }

    public enum OutcomeKind { Deliver, Exhausted, LoginRefused, RefusedForAccount }
    public readonly record struct Outcome(OutcomeKind Kind, DateTime? Until = null);

    /// From the status line alone. A 429 marks the account full; a 401 on a
    /// borrowed login marks that login refused; a 400, 403 or 404 on one
    /// sends the request home; anything else is delivered.
    public static Outcome Decide(int status, IReadOnlyList<(string, string)> headers, bool borrowed, DateTime now)
    {
        switch (status)
        {
            case 429:
                var limits = PoolLimits.Parse(headers, now);
                var retry = headers.FirstOrDefault(h => h.Item1.Equals("retry-after", StringComparison.OrdinalIgnoreCase)).Item2;
                var wait = double.TryParse(retry, out var s) ? s : 60;
                return new(OutcomeKind.Exhausted, limits?.RefusedUntil ?? now.AddSeconds(wait));
            case 401:
                return new(borrowed ? OutcomeKind.LoginRefused : OutcomeKind.Deliver);
            case 400 or 403 or 404:
                return new(borrowed ? OutcomeKind.RefusedForAccount : OutcomeKind.Deliver);
            default:
                return new(OutcomeKind.Deliver);
        }
    }
}

/// What a request says about itself, read from its raw bytes: keys are
/// matched with their closing quote and colon, which JSON never escapes, so
/// the same words in a prompt mean nothing.
public static class PoolRequest
{
    public static bool IsInference(string method, string target) =>
        method == "POST" && target.Split('?', 2)[0] == "/v1/messages";

    /// An uploaded file, a code execution container or a connector Anthropic
    /// runs belongs to one organization. Matched with the start of a value,
    /// since a tool schema can have a property of the same name.
    public static readonly string[] AccountBoundMarks =
        ["\"file_id\":\"", "\"container\":\"", "\"container\":{\"id\":\"", "\"mcp_servers\":[{"];

    public static bool MustStayWithSender(byte[] body, string? contentEncoding)
    {
        if (!string.IsNullOrEmpty(contentEncoding) && !contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            return true;
        return AccountBoundMarks.Any(m => Bytes.Contains(body, m));
    }

    static readonly string[] ReasoningKeys = ["\"type\":\"thinking\"", "\"type\":\"redacted_thinking\""];

    /// Models measured keeping their reasoning across accounts. Any other is
    /// taken to be bound: holding one needlessly costs a little sharing,
    /// moving one costs its reasoning.
    public static readonly string[] ReasoningTravels = ["claude-opus-5-5"];

    public static bool CarriesBoundReasoning(byte[] body)
    {
        if (!ReasoningKeys.Any(k => Bytes.Contains(body, k))) return false;
        return Model(body) is not { } model || !ReasoningTravels.Any(model.StartsWith);
    }

    public static bool ContinuesThread(byte[] body) => Bytes.Contains(body, "\"thread\":{\"type\":\"continue\"");
    public static bool CarriesThread(byte[] body) => Bytes.Contains(body, "\"thread\":{");
    public static bool StartsConversation(byte[] body) => !Bytes.Contains(body, "\"role\":\"assistant\"");
    public const string ThreadMissing = "thread_not_found";

    public static string? Model(byte[] body)
    {
        var start = Bytes.IndexOf(body, "\"model\":\"");
        if (start < 0) return null;
        start += "\"model\":\"".Length;
        var end = Array.IndexOf(body, (byte)'"', start, Math.Min(120, body.Length - start));
        return end < 0 ? null : Encoding.UTF8.GetString(body, start, end - start);
    }

    public static string? Bearer(IReadOnlyList<(string Name, string Value)> headers)
    {
        var value = headers.FirstOrDefault(h => h.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase)).Value;
        if (value == null || value.Length <= 7 || !value[..7].Equals("bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        return value[7..].Trim();
    }
}

public static class Bytes
{
    public static int IndexOf(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle));
    public static bool Contains(byte[] haystack, string needle) => IndexOf(haystack, needle) >= 0;
}

// ---- What Claude Graft and the relay say to each other ----

public sealed class PoolMember
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// The account uuid, so one account is counted once whichever way it came.
    public string? Account { get; set; }
    /// SHA-256 of every token the member's profile holds: how a request is
    /// traced to its sender.
    public List<string> Identify { get; set; } = [];
    /// The login it lends. Held in the relay's memory alone.
    public string? Token { get; set; }
    public PoolLimits? Limits { get; set; }
    public List<string>? BorrowsFrom { get; set; }
}

public sealed class PoolConfiguration
{
    public bool Enabled { get; set; }
    public List<PoolMember> Members { get; set; } = [];
    /// Conversation (session id) → the member chosen for it from the menu.
    public Dictionary<string, string>? Assignments { get; set; }
}

public sealed class PoolCommand
{
    public PoolConfiguration? Configure { get; set; }
    public bool? Quit { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PassReason>))]
public enum PassReason { Full, Login, Refused, Unreachable }

public sealed record PoolPass(string Id, PassReason Reason);

public sealed record PoolRouting(DateTime At, string From, string ServedBy, int Status, List<PoolPass> PassedOver,
                                 string? Model, string? Session);

public sealed record PoolSession(string Id, string From, string? ServedBy, DateTime LastAt, int Requests, string? Model,
                                 DateTime? CachedUntil, List<PoolPass> PassedOver, DateTime? StartedAt);

public sealed record PoolMemberStatus(string Id, PoolLimits? Limits, int Served, DateTime? LastServed,
                                      DateTime? LastSeen, bool TokenRefused);

public sealed record PoolStatus(string Binary, int Pid, DateTime Started, bool Enabled, bool Configured,
                                List<PoolMemberStatus> Members, List<PoolRouting> Recent, List<PoolSession> Sessions,
                                int OpenExchanges)
{
    public static PoolStatus Empty => new("", Environment.ProcessId, DateTime.UtcNow, false, false, [], [], [], 0);
}
