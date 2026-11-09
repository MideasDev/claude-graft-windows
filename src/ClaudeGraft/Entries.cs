using ClaudeGraft.Core;

namespace ClaudeGraft;

/// A row in the tray menu or the window: Claude's own profile, or one of the
/// extra Claudes. `Key` is "" for the main profile and the normalised profile
/// path otherwise, the same keys `ClaudeProcesses.ProfileOf` answers with.
sealed record ClaudeEntry(string Key, string Name, string? ProfileDir, Shortcut? Shortcut, string Color)
{
    public bool IsMain => Shortcut == null;
    public string? OpenProfile => Shortcut?.ProfileDir;
}

static class Entries
{
    public static List<ClaudeEntry> All(Store store)
    {
        var list = new List<ClaudeEntry>
        {
            new("", S.T("Claude (main)"), Paths.MainProfile, null, store.Data.MainColor),
        };
        list.AddRange(store.Data.Shortcuts.Select(s =>
            new ClaudeEntry(Paths.Normalize(s.ProfileDir), s.Name, s.ProfileDir, s, s.Color)));
        return list;
    }

    public static bool Running(ClaudeEntry e, IReadOnlyList<ClaudeProcess> snapshot) =>
        ClaudeProcesses.IsRunning(e.OpenProfile, snapshot);

    public static OpenResult Open(ClaudeEntry e, Store store) =>
        e.Shortcut == null ? Opener.OpenMain(store) : Opener.OpenFolder(store, e.Shortcut.Folder);

    public static string Short(UsageFigure? f, DateTime now)
    {
        if (f == null) return S.T("no usage yet");
        var parts = new List<string>();
        if (f.FiveHour is { } h) parts.Add(S.F("5h {0}%", h));
        parts.Add(S.F("week {0}%", f.Week));
        var text = string.Join(" · ", parts);
        return f.IsStale(now) ? $"{text} {S.T("(old figure)")}" : text;
    }

    public static string Long(UsageFigure? f, string? note, DateTime now)
    {
        if (f == null) return note ?? S.T("no usage yet");
        var lines = new List<string>();
        if (f.FiveHour is { } h)
            lines.Add(S.F("5h {0}%", h) + (Countdown.Until(f.FiveHourReset, now) is { } r ? $" — {S.F("resets in {0}", r)}" : ""));
        lines.Add(S.F("week {0}%", f.Week) + (Countdown.Until(f.WeekReset, now) is { } w ? $" — {S.F("resets in {0}", w)}" : ""));
        var age = now - f.Taken;
        var when = age.TotalMinutes < 1 ? "<1m" : Countdown.Until(now + age, now) ?? "";
        lines.Add($"{(f.Source == UsageFigure.Live ? "api.anthropic.com" : "plan-usage-history.json")} · {when}" +
                  (f.Plan != null ? $" · {f.Plan}" : ""));
        if (note != null) lines.Add(note);
        return string.Join(Environment.NewLine, lines);
    }
}
