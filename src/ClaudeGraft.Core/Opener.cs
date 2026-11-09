using System.Diagnostics;

namespace ClaudeGraft.Core;

public enum OpenOutcome { Launched, Revealed, RunningInBackground, Cancelled, UnknownShortcut, InvalidProfile, NoClaude, Failed }

public sealed record OpenResult(OpenOutcome Outcome, string Message)
{
    public bool Ok => Outcome is OpenOutcome.Launched or OpenOutcome.Revealed or OpenOutcome.RunningInBackground or OpenOutcome.Cancelled;
}

/// Every route that opens a Claude comes through here: the tray menu, the
/// window and the shortcuts on the desktop and in Start.
public static class Opener
{
    /// Claude never takes a single-instance lock, so a second process on one
    /// `--user-data-dir` would start happily and write the same chat store as
    /// the first. Asking whether the profile is open comes before anything
    /// else, and the answer for an open one is to show it.
    public static Func<string, string, Process?> Start { get; set; } = StartProcess;
    public static Func<int, bool> RevealWindow { get; set; } = WindowTools.Reveal;

    /// Every route that opens a profile asks first when another Claude
    /// reading the same chats is open: the same conversation open in two of
    /// them at once can lose messages. The window, the menu and the launcher
    /// each say how they ask; nobody asking means yes.
    public static Func<IReadOnlyList<string>, bool> ConfirmSharers { get; set; } = _ => true;

    /// Runs the mirror passes; replaced by the tray so they run off its
    /// window's thread.
    public static Action<Action> Background { get; set; } = work => work();

    /// Shared usage's lines, settled into a closed Claude before it starts.
    public static Action<Shortcut?, Store> BeforeLaunch { get; set; } = Pool.SharedUsage.PrepareLaunch;

    /// Pinned chats kept in step with the Claudes sharing this one's history.
    public static Action<string> SyncSidebar { get; set; } = Sidebar.SidebarSync.SynchronizeBeforeOpening;

    public static string ConflictTitle => S.T("Another Claude is open on these chats");

    public static string ConflictMessage(IReadOnlyList<string> sharers)
    {
        var list = sharers.Count switch
        {
            0 => "",
            1 => sharers[0],
            _ => string.Join(", ", sharers.Take(sharers.Count - 1)) + " " + S.T("and") + " " + sharers[^1],
        };
        return S.F(sharers.Count == 1
            ? "{0} is already open on the same chats. Opening the same conversation in two Claudes at once can lose messages."
            : "{0} are already open on the same chats. Opening the same conversation in two Claudes at once can lose messages.", list);
    }

    public static OpenResult OpenMain(Store? store = null) => Open(null, S.T("Claude"), store);

    /// A folder nobody saved is never permission to open some other profile:
    /// an old desktop shortcut for a Claude that was deleted reports that,
    /// rather than quietly opening the main one in its place.
    public static OpenResult OpenFolder(Store store, string folder)
    {
        var shortcut = store.Find(folder);
        if (shortcut == null)
            return new(OpenOutcome.UnknownShortcut,
                       S.F("There is no Claude using the folder “{0}” any more.", folder));
        return Open(shortcut, store);
    }

    public static OpenResult Open(Shortcut shortcut, Store? store = null)
    {
        var problem = ProfileName.Validate(shortcut.Folder);
        if (problem != null || !Paths.IsOwnProfile(shortcut.ProfileDir))
            return new(OpenOutcome.InvalidProfile, problem ?? S.T("That profile folder is not one of Claude Graft's."));
        return Open(shortcut, shortcut.Name, store);
    }

    static OpenResult Open(Shortcut? shortcut, string name, Store? store)
    {
        var profile = shortcut?.ProfileDir;
        var running = ClaudeProcesses.BrowserFor(profile);
        if (running != null)
        {
            Diagnostics.Note("open.reveal", name);
            // A Claude already up read its store when it started; squaring
            // the pairs up now still carries its changes to the others.
            if (store != null) Background(() => Graft.MirrorKnownPairs());
            return RevealWindow(running.Pid)
                ? new(OpenOutcome.Revealed, S.F("{0} was already open.", name))
                : new(OpenOutcome.RunningInBackground,
                      S.F("{0} is already running in the background. Open it from its icon in the notification area.", name));
        }

        if (store != null)
        {
            var snapshot = ClaudeProcesses.Snapshot();
            var sharers = Graft.ChatNeighbours(shortcut, store)
                .Where(n => ClaudeProcesses.IsRunning(n.Profile, snapshot))
                .Select(n => n.Name).ToList();
            if (sharers.Count > 0 && !ConfirmSharers(sharers))
                return new(OpenOutcome.Cancelled, "");
            // Before the launch, because Claude builds its sidebar as it
            // starts: a record arriving a second later waits for the next one.
            // One launch at a time, from the window, the menu and every
            // shortcut alike: two passes over the same profiles at once would
            // each read what the other is about to change.
            using var launchLock = new Mutex(false, @"Local\ClaudeGraftWindows.Launch");
            var held = false;
            try { held = launchLock.WaitOne(TimeSpan.FromSeconds(60)); } catch (AbandonedMutexException) { held = true; }
            try
            {
                if (shortcut != null) Graft.Apply(shortcut, store);
                Graft.MirrorKnownPairs();
                if (held && (shortcut?.ProfileDir ?? Paths.MainProfile) is { } opening)
                    try { SyncSidebar(opening); } catch (Exception e) { Diagnostics.Note("sidebar.failed", e.Message); }
                BeforeLaunch(shortcut, store);
            }
            finally { if (held) launchLock.ReleaseMutex(); }
        }

        var exe = ClaudeInstall.Executable;
        if (exe == null)
            return new(OpenOutcome.NoClaude, S.T("Claude Desktop is not installed. Install it from claude.ai/download or the Microsoft Store."));

        var arguments = "";
        if (profile != null)
        {
            Directory.CreateDirectory(profile);
            arguments = $"--user-data-dir=\"{Paths.Normalize(profile)}\"";
        }
        try
        {
            var process = Start(exe, arguments);
            Diagnostics.Note("open.launch", $"{name} pid={process?.Id}");
            return new(OpenOutcome.Launched, S.F("Opening {0}…", name));
        }
        catch (Exception e)
        {
            Diagnostics.Note("open.failed", $"{name} {e.Message}");
            return new(OpenOutcome.Failed, S.F("{0} could not be opened: {1}", name, e.Message));
        }
    }

    static Process? StartProcess(string exe, string arguments)
    {
        // Claude Graft is often started from a terminal inside a Claude, and
        // the Claude it opens would inherit that session's markers and take
        // itself for a child of it. They come out of this process's own
        // environment, which is what the shell hands on.
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToList())
            if (key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable(key, null);
        // Through the shell, never CreateProcess with inherited handles: a
        // Claude holding the launcher's console or pipe keeps whoever started
        // the launcher waiting for as long as that Claude runs.
        return Process.Start(new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        });
    }
}
