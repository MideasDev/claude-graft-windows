namespace ClaudeGraft.Core;

/// How hard a refresh was asked for. A tick nobody asked for may reuse a
/// figure for minutes; somebody opening the menu wants one under a minute
/// old; somebody pressing Refresh wants a new one.
public enum Strength { Tick, Look, Press }

/// One account, one figure: every view reads `Entry`, which is live where
/// live is available and the profile's own history underneath.
public sealed class UsageMonitor
{
    sealed class LiveState
    {
        public UsageFigure? Figure;
        public DateTime Asked = DateTime.MinValue;
        public int Failures;
        public DateTime NotBefore = DateTime.MinValue;
        public bool HardWait;
        public string? Note;
    }

    readonly Dictionary<string, LiveState> live = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim pass = new(1, 1);
    readonly object gate = new();

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    public Func<string, CancellationToken, Task<UsageFigure>> Fetch { get; set; } = UsageApi.FetchAsync;
    public Func<string, DateTime, (AccessToken? Token, Credentials.Failure Why)> Token { get; set; } = Credentials.UsageToken;

    public static TimeSpan CacheWindow(Strength strength, bool running) => strength switch
    {
        Strength.Press => TimeSpan.FromSeconds(2),   // wide enough to swallow a double-click
        Strength.Look => TimeSpan.FromMinutes(1),
        _ => running ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(5),
    };

    static readonly int[] BackoffSeconds = [60, 120, 300, 900, 1800];

    public static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(BackoffSeconds[Math.Clamp(failures - 1, 0, BackoffSeconds.Length - 1)]);

    static string Key(string profile) => Paths.Normalize(profile);

    /// A sample the profile wrote after the last live answer is the better
    /// figure and wins.
    public UsageFigure? Entry(string? profile)
    {
        if (profile == null) return null;
        var history = UsageHistory.Read(profile);
        UsageFigure? fromService;
        lock (gate) fromService = live.TryGetValue(Key(profile), out var state) ? state.Figure : null;
        if (fromService == null) return history;
        if (history == null) return fromService;
        return history.Taken > fromService.Taken ? history : fromService;
    }

    public string? Note(string? profile)
    {
        if (profile == null) return null;
        lock (gate) return live.TryGetValue(Key(profile), out var state) ? state.Note : null;
    }

    /// A press that lands while a pass runs is queued, never dropped; a tick
    /// that lands then has nothing to add and is.
    public async Task RefreshAsync(IEnumerable<string> profiles, Strength strength, Func<string, bool> running)
    {
        if (strength == Strength.Tick)
        {
            if (!await pass.WaitAsync(0).ConfigureAwait(false)) return;
        }
        else await pass.WaitAsync().ConfigureAwait(false);

        try
        {
            foreach (var profile in profiles.Distinct(StringComparer.OrdinalIgnoreCase))
                await RefreshOneAsync(profile, strength, running(profile)).ConfigureAwait(false);
        }
        finally { pass.Release(); }
    }

    async Task RefreshOneAsync(string profile, Strength strength, bool isRunning)
    {
        var now = Clock();
        LiveState state;
        lock (gate)
        {
            if (!live.TryGetValue(Key(profile), out state!)) live[Key(profile)] = state = new LiveState();
            // A failing endpoint would otherwise be asked on every open of
            // the menu. A press may skip the backoff, never a Retry-After.
            if (now < state.NotBefore && (strength != Strength.Press || state.HardWait)) return;
            if (now - state.Asked < CacheWindow(strength, isRunning)) return;
            state.Asked = now;
        }

        var (token, why) = Token(profile, now);
        if (token == null)
        {
            lock (gate) state.Note = why switch
            {
                Credentials.Failure.NotSignedIn => S.T("Not signed in yet."),
                Credentials.Failure.Expired => S.T("The saved login has expired. Open this Claude once so it can renew it."),
                _ => S.T("The saved login could not be read."),
            };
            return;
        }

        try
        {
            var figure = await Fetch(token.Value, CancellationToken.None).ConfigureAwait(false);
            if (figure.Plan == null && token.Plan != null) figure = figure with { Plan = token.Plan };
            lock (gate)
            {
                state.Figure = figure;
                state.Failures = 0;
                state.NotBefore = DateTime.MinValue;
                state.HardWait = false;
                state.Note = null;
            }
        }
        catch (Exception e)
        {
            lock (gate)
            {
                state.Failures++;
                var retryAfter = (e as UsageApi.Refused)?.RetryAfter;
                state.HardWait = retryAfter != null;
                state.NotBefore = now + (retryAfter ?? Backoff(state.Failures));
                state.Note = e is HttpRequestException or TaskCanceledException
                    ? S.T("The usage service could not be reached.") : e.Message;
            }
            Diagnostics.Note("usage.live-failed", $"{Path.GetFileName(profile)} {e.GetType().Name}");
        }
    }
}
