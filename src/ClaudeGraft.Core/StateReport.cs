using System.Text;

namespace ClaudeGraft.Core;

/// `state-report.txt`: what Claude Graft sees right now, written on demand
/// with `ClaudeGraft.exe --report`. Read-only. It names folders and figures,
/// never a token: it reads the usage history files and nothing that holds a
/// login.
public static class StateReport
{
    public static string Write(Store store)
    {
        var now = DateTime.UtcNow;
        var text = new StringBuilder();
        text.AppendLine($"Claude Graft for Windows — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Claude Desktop: {ClaudeInstall.Executable ?? "(not found)"}");
        text.AppendLine($"Main profile:   {Paths.MainProfile ?? "(none)"}");
        text.AppendLine($"Profiles root:  {Paths.ProfilesRoot}");
        text.AppendLine($"List:           {Paths.StoreFile}{(store.Broken ? "  (UNREADABLE)" : "")}");
        text.AppendLine($"Live usage:     {(store.Data.LiveUsage ? "on" : "off")}");
        text.AppendLine();

        var snapshot = ClaudeProcesses.Snapshot();
        var browsers = snapshot.Where(p => p.IsBrowser).ToList();
        text.AppendLine($"Claude processes: {snapshot.Count}, of which browsers: {browsers.Count}");
        foreach (var b in browsers)
            text.AppendLine($"  pid {b.Pid}: {(b.DataDir == null ? "main profile (no --user-data-dir)" : b.DataDir)}");
        text.AppendLine();

        void Row(string name, string? openProfile, string? usageProfile)
        {
            var browser = ClaudeProcesses.BrowserFor(openProfile, snapshot);
            var figure = usageProfile == null ? null : UsageHistory.Read(usageProfile);
            var usage = figure == null ? "no usage yet"
                : $"5h {(figure.FiveHour is { } h ? h + "%" : "none")}, week {figure.Week}%, sampled {figure.Taken:u}" +
                  (figure.IsStale(now) ? " (stale)" : "");
            text.AppendLine($"{name}: {(browser != null ? $"open (pid {browser.Pid})" : "closed")}; {usage}");
        }
        Row("Claude (main)", null, Paths.MainProfile);
        foreach (var s in store.Data.Shortcuts)
            Row($"{s.Name} [{s.Folder}]", s.ProfileDir, s.ProfileDir);

        Directory.CreateDirectory(Paths.GraftHome);
        var path = Path.Combine(Paths.GraftHome, "state-report.txt");
        File.WriteAllText(path, text.ToString());
        return path;
    }
}
