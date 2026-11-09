using System.Text.Json;

namespace ClaudeGraft.Core;

/// One account's usage. `FiveHour` is null on an account with no five-hour
/// limit, never zero: zero is a window with nothing spent in it yet, which a
/// limited account reads all through an idle day.
public sealed record UsageFigure(
    int? FiveHour,
    int Week,
    DateTime? FiveHourReset,
    DateTime? WeekReset,
    DateTime Taken,
    string Source,
    string? Plan = null)
{
    public const string Live = "live";
    public const string History = "history";

    /// The figure the tray gives this account: the five-hour window where
    /// there is one, otherwise the week (shown with a W).
    public (int Percent, bool Weekly) Headline => FiveHour is { } h ? (h, false) : (Week, true);

    /// Claude only writes its history while it runs, so a sample this old
    /// says nothing about a five-hour window that has since rolled over.
    public bool IsStale(DateTime now) => Source == History && now - Taken > TimeSpan.FromHours(5);
}

public static class Countdown
{
    /// "2d 3h 40m", "3h 40m", "12m"; days only when there is at least one.
    public static string? Until(DateTime? when, DateTime now)
    {
        if (when is not { } date) return null;
        var left = (int)(date - now).TotalSeconds;
        if (left <= 0) return null;
        int days = left / 86_400, hours = left % 86_400 / 3_600, minutes = left % 3_600 / 60;
        if (days > 0) return $"{days}d {hours}h {minutes}m";
        if (hours > 0) return $"{hours}h {minutes}m";
        return $"{Math.Max(minutes, 1)}m";
    }
}

/// `plan-usage-history.json`: samples of `{t, org, u:{fh, sd}}` that Claude
/// appends while it runs. It grows into the hundreds of kilobytes, so it is
/// parsed once per change and cached against its time and size.
public static class UsageHistory
{
    static readonly Dictionary<string, (DateTime Stamp, long Size, UsageFigure? Figure)> Cache = new();
    static readonly object Gate = new();

    public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public static UsageFigure? Read(string profileDir)
    {
        var path = Path.Combine(profileDir, "plan-usage-history.json");
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var hit) && hit.Stamp == info.LastWriteTimeUtc && hit.Size == info.Length)
                return hit.Figure;
        }
        UsageFigure? figure = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            figure = Parse(JsonDocument.Parse(stream).RootElement);
        }
        catch (Exception e) { Diagnostics.Note("usage.history-unreadable", $"{Path.GetFileName(profileDir)} {e.Message}"); }
        lock (Gate) Cache[path] = (info.LastWriteTimeUtc, info.Length, figure);
        return figure;
    }

    internal readonly record struct Sample(DateTime Time, int? FiveHour, int Week, string? Org);

    static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    static readonly TimeSpan OneWeek = TimeSpan.FromDays(7);

    public static UsageFigure? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("samples", out var raw) || raw.ValueKind != JsonValueKind.Array) return null;
        var samples = new List<Sample>();
        foreach (var item in raw.EnumerateArray())
        {
            if (!item.TryGetProperty("t", out var t) || !t.TryGetDouble(out var ms)) continue;
            if (!item.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Object) continue;
            if (!u.TryGetProperty("sd", out var sd) || !sd.TryGetInt32(out var week)) continue;
            int? fiveHour = u.TryGetProperty("fh", out var fh) && fh.TryGetInt32(out var f) ? f : null;
            var org = item.TryGetProperty("org", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            samples.Add(new(DateTime.UnixEpoch.AddMilliseconds(ms), fiveHour, week, org));
        }
        if (samples.Count == 0) return null;
        var newest = samples[^1];

        if (HasNoFiveHourLimit(samples))
            return new UsageFigure(null, newest.Week, null, WeekReset(samples), newest.Time, UsageFigure.History);

        // A limited account's sample without the figure is a gap, not an
        // empty window. Read as zero it dropped the bar to nothing, and the
        // zero then passed for the window reopening, moving its reset.
        var complete = samples.Where(s => s.FiveHour != null).ToList();
        var latest = complete.LastOrDefault(s => s.Org == newest.Org);
        if (latest == default) return null;
        return new UsageFigure(latest.FiveHour, latest.Week, FiveHourReset(complete), WeekReset(complete),
                               latest.Time, UsageFigure.History);
    }

    /// One sample without `fh` is not enough: a limited account can leave it
    /// out once between complete ones. The account must have left it out
    /// twice running and written none in the five hours before.
    internal static bool HasNoFiveHourLimit(List<Sample> samples)
    {
        var newest = samples[^1];
        if (newest.FiveHour != null) return false;
        var own = samples.Where(s => s.Org == newest.Org).ToList();
        if (own.Count > 1 && own[^2].FiveHour != null) return false;
        return !own.Any(s => s.FiveHour != null && newest.Time - s.Time < FiveHours);
    }

    /// Claude does not record when a window closes, but one opening shows up
    /// as the figure leaving zero; it closes five hours after that.
    static DateTime? FiveHourReset(List<Sample> samples)
    {
        if (samples.Count == 0 || samples[^1].FiveHour is not > 0) return null;
        for (var i = samples.Count - 1; i > 0; i--)
        {
            if (samples[i - 1].FiveHour == 0 && samples[i].FiveHour > 0)
            {
                var reset = samples[i].Time + FiveHours;
                return reset > Clock() ? reset : null;
            }
        }
        return null;
    }

    /// Weekly resets are a cycle, so the last one seen can be rolled forward
    /// across stretches when Claude was not running to record it.
    static DateTime? WeekReset(List<Sample> samples)
    {
        DateTime? last = null;
        for (var i = 1; i < samples.Count; i++)
            if (samples[i].Week < samples[i - 1].Week - 2) last = samples[i].Time;
        if (last is not { } seen) return null;
        var reset = seen + OneWeek;
        var now = Clock();
        for (var rolls = 0; reset <= now && rolls < 520; rolls++) reset += OneWeek;
        return reset;
    }
}
