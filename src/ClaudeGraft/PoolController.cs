using ClaudeGraft.Core;
using ClaudeGraft.Core.Pool;

namespace ClaudeGraft;

/// Keeps shared usage in step from the tray: the relay up while any Claude
/// borrows, configured with every member and the usage figures the tray
/// already has, the lines in each closed Claude, and the relay gone once no
/// profile holds them. Runs on the thirty-second tick and when the menu opens.
sealed class PoolController(TrayApp app)
{
    public SharingSettings Settings { get; private set; } = SharingSettings.Load();
    public PoolStatus? Status { get; private set; }
    public Dictionary<string, PoolEnvironment.Result> States { get; } = [];
    int busy;

    public bool AnyBorrowing => Settings.Lending.Any(l => l.Value.Count > 0);

    public void Reload() => Settings = SharingSettings.Load();

    public void Tick()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        var store = app.Store;
        var snapshot = app.Snapshot;
        Task.Run(() =>
        {
            try { Pass(store, snapshot); }
            catch (Exception e) { Diagnostics.Note("pool.tick-failed", e.Message); }
            finally { Interlocked.Exchange(ref busy, 0); app.OnUi(app.NotifyChanged); }
        });
    }

    void Pass(Store store, IReadOnlyList<ClaudeProcess> snapshot)
    {
        Reload();
        var claudes = new List<(string Id, Shortcut? Shortcut, string? Profile)> { (SharedUsage.MainId, null, Paths.MainProfile) };
        claudes.AddRange(store.Data.Shortcuts.Select(s => (s.Folder, (Shortcut?)s, (string?)s.ProfileDir)));

        var anyBorrowing = AnyBorrowing;
        var relayAnswers = Relay.Send(new PoolCommand(), timeoutMs: 1000) != null;
        if (!anyBorrowing && !relayAnswers) { Status = null; States.Clear(); return; }

        var ready = anyBorrowing ? SharedUsage.EnsureRelay() : relayAnswers;
        if (ready && anyBorrowing)
            Relay.Send(new PoolCommand { Configure = SharedUsage.Members(store, Settings, app.Monitor.Entry) });

        var anyHolding = false;
        foreach (var (id, shortcut, profile) in claudes)
        {
            if (profile == null || !Directory.Exists(profile)) continue;
            var wanted = Settings.Borrows(id);
            var closed = !ClaudeProcesses.IsRunning(shortcut?.ProfileDir, snapshot);
            var result = PoolEnvironment.Settle(profile, wanted, ready, apply: closed);
            States[id] = result;
            anyHolding |= result.HoldsLines;
        }

        // The lines come out before the relay goes.
        if (!anyBorrowing && !anyHolding && relayAnswers)
        {
            Relay.Send(new PoolCommand { Quit = true });
            Diagnostics.Note("relay.stopped", "no Claude borrows any more");
            Status = null;
            return;
        }
        Status = Relay.Send(new PoolCommand(), timeoutMs: 1000);
    }

    public void Assign(string session, string member)
    {
        Reload();
        Settings.Assignments[session] = member;
        // A choice is dropped once its conversation has been quiet for a day.
        var known = Status?.Sessions.Select(s => s.Id).ToHashSet() ?? [];
        foreach (var stale in Settings.Assignments.Keys.Where(k => !known.Contains(k) && k != session).ToList())
            Settings.Assignments.Remove(stale);
        Settings.Save();
        Tick();
    }

    public string NameOf(string? member)
    {
        if (member == null) return "—";
        if (member == SharedUsage.MainId) return S.T("Claude (main)");
        if (app.Store.Find(member) is { } s) return s.Name;
        return Settings.Accounts.FirstOrDefault(a => a.Id == member)?.Name ?? member;
    }

    /// The menu's conversations: from a Claude with something to borrow,
    /// active in the last half hour, five at most.
    public List<(PoolSession Session, string Title, List<string> Choices)> Conversations()
    {
        if (Status == null) return [];
        var now = DateTime.UtcNow;
        return Status.Sessions
            .Where(s => now - s.LastAt < TimeSpan.FromMinutes(30) && Settings.Borrows(s.From))
            .OrderByDescending(s => s.LastAt).Take(5)
            .Select(s =>
            {
                var title = SessionTitles.Find(SharedUsage.ProfileOf(s.From, app.Store), s.Id) ?? S.T("New chat");
                var choices = new List<string> { s.From };
                choices.AddRange(Settings.LendersOf(s.From));
                return (s, title, choices.Distinct().ToList());
            }).ToList();
    }

    public string StateText(string id) => States.GetValueOrDefault(id) switch
    {
        null => Settings.Borrows(id) ? S.T("waiting for the relay") : S.T("off"),
        { State: PoolEnvironment.State.On } => S.T("on"),
        { State: PoolEnvironment.State.Waiting } => S.T("on from the next time it opens"),
        { State: PoolEnvironment.State.Leaving } => S.T("off from the next time it opens"),
        { State: PoolEnvironment.State.Conflict, Conflict: var c } => S.F("left alone: {0} is set by hand in its Code environment", c),
        { State: PoolEnvironment.State.Unreadable } => S.T("its Code environment could not be read"),
        _ => S.T("off"),
    };
}
