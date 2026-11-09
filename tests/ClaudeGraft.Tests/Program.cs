using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;

// The suite runs entirely inside a throwaway folder: the store, the profiles
// root and the main profile are all pointed there before anything is read,
// and the process list and Claude's executable are stand-ins. Nothing here
// can open, close or read a real Claude.

var failures = 0;
var passed = 0;
void check(bool condition, string sentence)
{
    if (condition) { passed++; return; }
    failures++;
    Console.WriteLine($"FAIL: {sentence}");
}

S.Spanish = false;
var sandbox = Path.Combine(Path.GetTempPath(), "claude-graft-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(sandbox);
Paths.HomeOverride = Path.Combine(sandbox, "ClaudeGraft");
var main = Path.Combine(sandbox, "Claude-Data", "claude_test");
Directory.CreateDirectory(main);
Paths.MainProfileOverride = () => main;
var processes = new List<ClaudeProcess>();
ClaudeProcesses.Source = () => processes;
var launches = new List<(string Exe, string Args)>();
var revealed = new List<int>();
Opener.Start = (exe, args) => { launches.Add((exe, args)); return null; };
Opener.RevealWindow = pid => { revealed.Add(pid); return true; };
Opener.BeforeLaunch = (_, _) => { };
Opener.SyncSidebar = _ => { };
ClaudeInstall.Override = () => @"C:\stand-in\claude-desktop.exe";
var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
UsageHistory.Clock = () => now;

try
{
    // ---- Profile folders ----
    check(ProfileName.Validate("Claude-2") == null, "a plain folder name is usable");
    check(ProfileName.Validate("") != null, "an empty folder name is refused");
    check(ProfileName.Validate(@"..\Claude") != null, "a path is refused as a folder name");
    check(ProfileName.Validate("C:Claude") != null, "a drive-relative name is refused");
    check(ProfileName.Validate("claude") != null, "Claude's own folder is refused, whatever its case");
    check(ProfileName.Validate("ClaudeGraft") != null, "Claude Graft's own folder is refused");
    check(ProfileName.Validate("Claude-Data") != null, "the Store build's data folder is refused");
    check(ProfileName.Validate("CON") != null && ProfileName.Validate("nul.txt") != null, "a Windows device name is refused");
    check(ProfileName.Validate(".hidden") != null && ProfileName.Validate("trailing.") != null, "a dot at either end is refused");
    check(ProfileName.Validate("a<b") != null, "a character Windows forbids is refused");
    check(ProfileName.Suggest("Claude (work) 2") == "Claude-work-2", "a display name becomes a tidy folder name");
    check(ProfileName.Validate(ProfileName.Suggest("Claude")) == null, "a suggested folder is never one of the reserved ones");
    check(ProfileName.Validate(ProfileName.Suggest("CON")) == null, "a suggested folder is never a device name");

    // ---- Paths ----
    check(Paths.Same(@"C:\Users\x\Profiles\A", @"c:\users\X\profiles\a\"), "paths compare without case or trailing separator");
    check(!Paths.Same(@"C:\P\Claude 2", @"C:\P\Claude 22"), "one profile path is never taken for a longer one it prefixes");
    check(Paths.IsOwnProfile(Paths.ProfileDir("Claude-2")), "a folder directly in the profiles root is one of ours");
    check(!Paths.IsOwnProfile(main), "the main profile is never one of ours");
    check(!Paths.IsOwnProfile(Path.Combine(Paths.ProfilesRoot, "a", "b")), "a folder nested deeper is never one of ours");

    // ---- Command lines ----
    const string exe = "\"C:\\Program Files\\WindowsApps\\Claude_4_x64__pzs8sxrjxfjjc\\app\\Claude.exe\"";
    var two = Paths.ProfileDir("Claude-2");
    var twentyTwo = Paths.ProfileDir("Claude-22");
    check(ClaudeProcesses.DataDir($"{exe} --user-data-dir=\"{two}\"") == two, "a quoted --user-data-dir is read whole");
    check(ClaudeProcesses.DataDir($"{exe} --type=crashpad-handler --user-data-dir=C:\\x\\y /prefetch:4") == @"C:\x\y",
          "an unquoted --user-data-dir ends at the first space");
    check(ClaudeProcesses.DataDir($"{exe} ") == null, "a command line without --user-data-dir has none");
    check(ClaudeProcesses.IsBrowserProcess($"{exe} --user-data-dir=\"{two}\""), "the process without --type is the browser");
    check(!ClaudeProcesses.IsBrowserProcess($"{exe} --type=renderer --user-data-dir=\"{two}\""), "a renderer is not the browser");

    processes.AddRange([
        new(100, $"{exe} "),
        new(101, $"{exe} --type=gpu-process --user-data-dir=\"{main}\""),
        new(200, $"{exe} --user-data-dir=\"{twentyTwo}\""),
        new(201, $"{exe} --type=renderer --user-data-dir=\"{two}\""),
    ]);
    check(ClaudeProcesses.IsRunning(null), "the main Claude is recognised by having no --user-data-dir");
    check(ClaudeProcesses.BrowserFor(null)?.Pid == 100, "the main Claude's pid is its browser process, not a helper");
    check(!ClaudeProcesses.IsRunning(two), "a profile whose only process is a stray renderer is not running");
    check(ClaudeProcesses.IsRunning(twentyTwo) && !ClaudeProcesses.IsRunning(two),
          "a running Claude 22 does not make Claude 2 look open");
    check(ClaudeProcesses.ProfileOf(100, processes) == "", "the main Claude's window maps to the main profile");
    check(ClaudeProcesses.ProfileOf(200, processes) == Paths.Normalize(twentyTwo), "a profile's window maps to that profile");
    check(ClaudeProcesses.ProfileOf(201, processes) == null, "a helper process maps to no profile");

    // ---- The store ----
    var store = Store.Load();
    check(!store.Broken && store.Data.Shortcuts.Count == 0, "no list on disk is an empty list, not a broken one");
    var added = store.Add("Claude 2", "Claude-2");
    check(Directory.Exists(added.ProfileDir), "adding a Claude makes its profile folder");
    check(added.Color != store.Data.MainColor, "a new Claude never wears the main Claude's colour");
    var other = store.Add("Work", "Work");
    check(other.Color != added.Color, "a colour is handed out once and never twice");
    var duplicate = false;
    try { store.Add("Again", "claude-2"); } catch (ArgumentException) { duplicate = true; }
    check(duplicate, "two Claudes can never share one folder, whatever its case");
    var invalid = false;
    try { store.Add("Bad", @"..\x"); } catch (ArgumentException) { invalid = true; }
    check(invalid, "a Claude cannot be added with a path for a folder");
    store.Save();
    var reread = Store.Load();
    check(reread.Data.Shortcuts.Select(s => s.Folder).SequenceEqual(["Claude-2", "Work"]), "the list survives a save and a load");
    check(ProfileName.Unique("Claude-2", reread.Folders) == "Claude-2-2", "a suggested folder steers clear of one in use");

    File.WriteAllText(Paths.StoreFile, "{ this is not json");
    var broken = Store.Load();
    check(broken.Broken, "an unreadable list is reported as broken");
    var refused = false;
    try { broken.Save(); } catch (StoreUnreadableException) { refused = true; }
    check(refused, "an unreadable list is never saved over");
    check(File.ReadAllText(Paths.StoreFile) == "{ this is not json", "an unreadable list stays on disk exactly as it was");
    store.Save();

    // ---- Opening ----
    launches.Clear(); revealed.Clear();
    var unknown = Opener.OpenFolder(store, "Deleted-Claude");
    check(unknown.Outcome == OpenOutcome.UnknownShortcut && launches.Count == 0,
          "a shortcut for a Claude that is gone opens nothing at all, not the main Claude");
    var opened = Opener.OpenFolder(store, "Claude-2");
    check(opened.Outcome == OpenOutcome.Launched && launches.Count == 1, "a closed Claude is launched");
    check(launches[0].Args == $"--user-data-dir=\"{Paths.Normalize(two)}\"", "a Claude is launched on its own profile folder");
    processes.Add(new(300, $"{exe} --user-data-dir=\"{two}\""));
    launches.Clear();
    var again = Opener.OpenFolder(store, "claude-2");
    check(again.Outcome == OpenOutcome.Revealed && launches.Count == 0 && revealed.SequenceEqual([300]),
          "opening a Claude that is already open shows it rather than starting a second one");
    launches.Clear(); revealed.Clear();
    var mainOpen = Opener.OpenMain();
    check(mainOpen.Outcome == OpenOutcome.Revealed && revealed.SequenceEqual([100]), "the main Claude, already open, is brought forward");
    processes.RemoveAll(p => p.Pid == 100);
    launches.Clear();
    Opener.OpenMain();
    check(launches.Count == 1 && launches[0].Args == "", "the main Claude is launched with no --user-data-dir");
    var outside = new Shortcut { Folder = @"..\..\Windows", Name = "Evil" };
    launches.Clear();
    check(Opener.Open(outside).Outcome == OpenOutcome.InvalidProfile && launches.Count == 0,
          "a damaged entry pointing outside the profiles root is never opened");
    ClaudeInstall.Override = () => null;
    processes.RemoveAll(p => p.Pid == 300);
    check(Opener.OpenFolder(store, "Claude-2").Outcome == OpenOutcome.NoClaude, "without Claude installed, opening says so");
    ClaudeInstall.Override = () => @"C:\stand-in\claude-desktop.exe";

    // ---- Usage history ----
    long ms(DateTime t) => (long)(t - DateTime.UnixEpoch).TotalMilliseconds;
    string sample(DateTime t, int? fh, int sd, string org = "org-a") =>
        fh == null ? $"{{\"t\":{ms(t)},\"org\":\"{org}\",\"u\":{{\"sd\":{sd}}}}}"
                   : $"{{\"t\":{ms(t)},\"org\":\"{org}\",\"u\":{{\"fh\":{fh},\"sd\":{sd}}}}}";
    UsageFigure? parse(params string[] samples) =>
        UsageHistory.Parse(JsonDocument.Parse($"{{\"version\":2,\"samples\":[{string.Join(",", samples)}]}}").RootElement);

    var limited = parse(sample(now.AddHours(-2), 0, 40), sample(now.AddHours(-1), 20, 42), sample(now.AddMinutes(-5), 34, 44));
    check(limited?.FiveHour == 34 && limited.Week == 44, "the newest complete sample is the figure");
    check(limited?.FiveHourReset == now.AddHours(-1).AddHours(5), "the five-hour window closes five hours after it left zero");
    var gap = parse(sample(now.AddHours(-1), 20, 42), sample(now.AddMinutes(-5), null, 43));
    check(gap?.FiveHour == 20, "a single sample without the five-hour figure is a gap, not zero");
    var weeklyOnly = parse(sample(now.AddHours(-8), null, 50), sample(now.AddMinutes(-5), null, 51));
    check(weeklyOnly != null && weeklyOnly.FiveHour == null && weeklyOnly.Week == 51,
          "an account that leaves the five-hour figure out twice running has no five-hour limit");
    check(weeklyOnly!.Headline == (51, true), "an account without a five-hour limit headlines its week");
    var recent = parse(sample(now.AddHours(-3), 10, 50), sample(now.AddHours(-2), null, 51), sample(now.AddMinutes(-5), null, 52));
    check(recent?.FiveHour == 10, "an account that wrote a five-hour figure within five hours still has the limit");
    var idle = parse(sample(now.AddMinutes(-5), 0, 10));
    check(idle?.FiveHour == 0 && idle.Headline == (0, false), "an idle limited account reads zero, not no limit");
    var reset = parse(sample(now.AddDays(-9), 0, 90), sample(now.AddDays(-8), 0, 3), sample(now.AddHours(-1), 0, 20));
    check(reset?.WeekReset == now.AddDays(-8).AddDays(14), "a weekly reset seen once is rolled forward past today");
    var otherOrg = parse(sample(now.AddHours(-1), 50, 60, "org-a"), sample(now.AddMinutes(-5), null, 5, "org-b"));
    check(otherOrg == null || otherOrg.Week != 60, "an account signed in after another is not judged by the other's samples");
    check(parse() == null, "a history without samples has no figure");
    check(new UsageFigure(1, 1, null, null, now.AddHours(-6), UsageFigure.History).IsStale(now),
          "a history sample older than five hours is stale");

    var profileWithHistory = Paths.ProfileDir("Work");
    File.WriteAllText(Path.Combine(profileWithHistory, "plan-usage-history.json"),
        $"{{\"samples\":[{sample(now.AddHours(-1), 0, 30)},{sample(now.AddMinutes(-2), 12, 31)}]}}");
    check(UsageHistory.Read(profileWithHistory)?.FiveHour == 12, "a profile's history file is read from disk");

    // ---- Countdown ----
    check(Countdown.Until(now.AddMinutes(3 * 60 + 40), now) == "3h 40m", "a countdown under a day has hours and minutes");
    check(Countdown.Until(now.AddDays(2).AddHours(3), now) == "2d 3h 0m", "a countdown over a day has days");
    check(Countdown.Until(now.AddSeconds(20), now) == "1m", "a countdown never shows zero minutes");
    check(Countdown.Until(now.AddMinutes(-1), now) == null, "a moment already past has no countdown");

    // ---- The usage service's answers ----
    UsageFigure? answer(string json) => UsageApi.Reading(JsonDocument.Parse(json).RootElement, now);
    var full = answer("""{"five_hour":{"utilization":33.6,"resets_at":"2026-11-09T14:00:00.123Z"},"seven_day":{"utilization":61,"resets_at":"2026-11-12T00:00:00Z"},"subscription_type":"max"}""");
    check(full?.FiveHour == 34 && full.Week == 61 && full.Plan == "Max", "a full answer is read, rounded");
    check(full?.FiveHourReset == new DateTime(2026, 11, 9, 14, 0, 0, 123, DateTimeKind.Utc), "a reset time with a fraction is read");
    var noLimit = answer("""{"five_hour":null,"seven_day":{"utilization":94,"resets_at":null}}""");
    check(noLimit != null && noLimit.FiveHour == null && noLimit.Week == 94, "five_hour: null is an account with no five-hour limit");
    var absent = answer("""{"seven_day":{"utilization":5}}""");
    check(absent != null && absent.FiveHour == null, "a missing five_hour key is an account with no five-hour limit");
    check(answer("""{"five_hour":{"nonsense":1},"seven_day":{"utilization":5}}""") == null,
          "a five-hour window that is there but unreadable is a broken answer, refused");
    check(answer("""{"five_hour":{"utilization":0,"resets_at":null},"seven_day":{"utilization":0}}""")?.FiveHour == 0,
          "an idle limited account is sent a zero and reads as zero");
    check(answer("""{"other":1}""") == null, "an answer with neither window is refused");
    check(answer("""{"five_hour":{"utilization":1,"resets_at":1793000000},"seven_day":{"utilization":2}}""")?.FiveHourReset
          == DateTime.UnixEpoch.AddSeconds(1793000000), "a reset sent as epoch seconds is read");

    // ---- Safe storage ----
    var key = new byte[32];
    Random.Shared.NextBytes(key);
    var secret = Encoding.UTF8.GetBytes("""{"a":1}""");
    check(SafeStorage.Decrypt(SafeStorage.Encrypt(secret, key), key) is { } back && back.SequenceEqual(secret),
          "a value sealed the way Chromium seals it is opened with the same key");
    var wrong = new byte[32];
    check(SafeStorage.Decrypt(SafeStorage.Encrypt(secret, key), wrong) == null, "the wrong key opens nothing and throws nothing");
    check(SafeStorage.Decrypt([1, 2, 3], key) == null, "a value too short to be sealed is refused");

    // A whole stand-in profile, sealed with this Windows user's DPAPI the way
    // Claude seals its own: Local State holds the key, config.json the login.
    var signed = Paths.ProfileDir("Signed");
    Directory.CreateDirectory(signed);
    var sealedKey = Dpapi.Protect(key);
    File.WriteAllText(Path.Combine(signed, "Local State"),
        new JsonObject { ["os_crypt"] = new JsonObject { ["encrypted_key"] = Convert.ToBase64String([.. "DPAPI"u8, .. sealedKey]) } }.ToJsonString());
    var expires = (double)ms(now.AddHours(4));
    var cache = new JsonObject
    {
        ["client:org:https://api.anthropic.com:user:inference user:profile"] = new JsonObject
            { ["token"] = "stand-in-current", ["expiresAt"] = expires, ["subscriptionType"] = "pro" },
        ["client:org:https://api.anthropic.com:user:inference"] = new JsonObject
            { ["token"] = "stand-in-no-profile-scope", ["expiresAt"] = expires + 1000 },
        ["client:org:https://api.anthropic.com:user:profile"] = new JsonObject
            { ["token"] = "stand-in-expired", ["expiresAt"] = (double)ms(now.AddMinutes(-1)) },
    };
    var sealedCache = SafeStorage.Encrypt(Encoding.UTF8.GetBytes(cache.ToJsonString()), key);
    File.WriteAllText(Path.Combine(signed, "config.json"),
        new JsonObject { ["oauth:tokenCacheV2"] = Convert.ToBase64String(sealedCache), ["locale"] = "es-ES" }.ToJsonString());
    var (token, why) = Credentials.UsageToken(signed, now);
    check(why == Credentials.Failure.None && token?.Value == "stand-in-current",
          "the current token that covers user:profile is the one chosen");
    check(token?.Plan == "pro", "the plan is read from the token's entry");
    var (_, later) = Credentials.UsageToken(signed, now.AddHours(5));
    check(later == Credentials.Failure.Expired, "a cache whose tokens have all expired says so");
    check(Credentials.UsageToken(Paths.ProfileDir("Claude-2"), now).Why == Credentials.Failure.NotSignedIn,
          "a profile with no config.json is not signed in");
    check(Credentials.ScopesIn("c:o:https://api.anthropic.com:user:profile user:inference").SequenceEqual(["user:profile", "user:inference"]),
          "scopes are read from after the API host in the cache key");
    check(Credentials.ScopesIn("odd-key").Count == 0, "a key of another shape has no scopes, and so is not ruled out");

    // ---- The usage monitor ----
    var monitor = new UsageMonitor { Clock = () => now };
    var asked = 0;
    monitor.Token = (_, _) => (new AccessToken("t", now.AddHours(1), null, []), Credentials.Failure.None);
    monitor.Fetch = (_, _) => { asked++; return Task.FromResult(new UsageFigure(5, 6, null, null, now, UsageFigure.Live)); };
    var profiles = new[] { Paths.ProfileDir("Claude-2") };
    await monitor.RefreshAsync(profiles, Strength.Tick, _ => false);
    await monitor.RefreshAsync(profiles, Strength.Tick, _ => false);
    check(asked == 1, "a second tick inside five minutes reuses the figure");
    await monitor.RefreshAsync(profiles, Strength.Look, _ => false);
    check(asked == 1, "opening the menu inside a minute of the last answer reuses it");
    await monitor.RefreshAsync(profiles, Strength.Press, _ => false);
    check(asked == 1, "a double-click on Refresh asks only once");
    now = now.AddSeconds(3);
    await monitor.RefreshAsync(profiles, Strength.Press, _ => false);
    check(asked == 2, "a press after two seconds asks again");
    check(monitor.Entry(profiles[0])?.Source == UsageFigure.Live, "a live answer is the figure when there is no newer sample");
    check(UsageMonitor.CacheWindow(Strength.Tick, running: true) < UsageMonitor.CacheWindow(Strength.Tick, running: false),
          "an open Claude's account is read more often than a closed one's");

    monitor.Fetch = (_, _) => throw new UsageApi.Refused(System.Net.HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(10));
    now = now.AddMinutes(10);
    await monitor.RefreshAsync(profiles, Strength.Press, _ => false);
    var afterRefusal = asked;
    monitor.Fetch = (_, _) => { asked++; return Task.FromResult(new UsageFigure(7, 8, null, null, now, UsageFigure.Live)); };
    now = now.AddMinutes(2);
    await monitor.RefreshAsync(profiles, Strength.Press, _ => false);
    check(asked == afterRefusal, "a Retry-After is the one wait a press cannot skip");
    check(monitor.Entry(profiles[0])?.FiveHour == 5, "the last figure stays on screen while the service refuses");
    check(monitor.Note(profiles[0]) != null, "a refusal is explained beside the figure");
    now = now.AddMinutes(9);
    await monitor.RefreshAsync(profiles, Strength.Press, _ => false);
    check(asked == afterRefusal + 1 && monitor.Note(profiles[0]) == null, "after the wait a success clears the note");
    check(UsageMonitor.Backoff(1) == TimeSpan.FromSeconds(60) && UsageMonitor.Backoff(9) == TimeSpan.FromSeconds(1800),
          "failures back off from a minute to half an hour, and no further");

    var historyMonitor = new UsageMonitor { Clock = () => now };
    check(historyMonitor.Entry(profileWithHistory)?.Source == UsageFigure.History, "without live usage the history is the figure");

    // ---- MCP servers ----
    File.WriteAllText(Path.Combine(main, DesktopServers.FileName),
        """{"mcpServers":{"files":{"command":"a"},"search":{"command":"b"}},"other":true}""");
    var target = Paths.ProfileDir("Claude-2");
    File.WriteAllText(Path.Combine(target, DesktopServers.FileName), """{"mcpServers":{"files":{"command":"mine"}}}""");
    check(DesktopServers.CopyMissing(main, target) == 1, "only the servers a profile lacks are copied");
    var merged = JsonNode.Parse(File.ReadAllText(Path.Combine(target, DesktopServers.FileName)))!;
    check(merged["mcpServers"]!["files"]!["command"]!.GetValue<string>() == "mine", "a server a profile already has stays its own");
    check(merged["other"] == null, "nothing but servers is copied");
    check(DesktopServers.CopyMissing(main, target) == 0, "copying twice adds nothing the second time");
    check(DesktopServers.CopyMissing(main, main) == 0, "a profile is never copied into itself");
    File.WriteAllText(Path.Combine(target, DesktopServers.FileName), "{ broken");
    var keptBroken = false;
    try { DesktopServers.CopyMissing(main, target); } catch (InvalidDataException) { keptBroken = true; }
    check(keptBroken && File.ReadAllText(Path.Combine(target, DesktopServers.FileName)) == "{ broken",
          "an unreadable settings file is never written over");

    // ---- Chat sharing ----
    GraftTests.Run(check, sandbox);
    GraftTests.RunOpening(check, store, processes, launches, exe);

    // ---- The relay's TLS ----
    await TlsProbe.Run(check);
    await RelayTests.Run(check);
    PoolEnvironmentTests.Run(check, sandbox);
    PoolEnvironmentTests.RunMembers(check, store, main, now);

    // ---- Pinned chats ----
    SidebarTests.Run(check, sandbox, main);

    // ---- claude.ai: artifacts, projects, search ----
    WebTests.Run(check);

    // ---- Spanish ----
    S.Spanish = true;
    check(S.T("Claude (main)") == "Claude (principal)", "the window speaks Spanish on a Spanish Windows");
    check(S.F("{0} was already open.", "Claude 2") == "Claude 2 ya estaba abierto.", "formatted text is translated too");
    S.Spanish = false;
    check(S.T("Claude (main)") == "Claude (main)", "and English elsewhere");
}
catch (Exception e)
{
    failures++;
    Console.WriteLine($"FAIL: the suite stopped on {e}");
}
finally
{
    try { Directory.Delete(sandbox, recursive: true); } catch { }
}

Console.WriteLine(failures == 0 ? $"All {passed} checks passed." : $"{failures} failed, {passed} passed.");
return failures == 0 ? 0 : 1;
