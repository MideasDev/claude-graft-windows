using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using ClaudeGraft.Core.Sidebar;
using Snap = ClaudeGraft.Core.Sidebar.SidebarSync.Snapshot;

/// Pinned chats between Claudes sharing a history: the merge, the pass over
/// real profile folders with browser storage stood in for, and the storage
/// script itself under Node.
static class SidebarTests
{
    static Snap S(string[] pins, string[]? order = null, string sort = "recency", double time = 0, string? scope = null) =>
        new([.. pins], [.. order ?? pins], sort, time, scope);

    public static void Run(Action<bool, string> check, string sandbox, string main)
    {
        // ---- The merge ----
        var none = SidebarSync.Merge(S(["a"]), S(["b"]), null);
        check(none.Pins.SequenceEqual(["a", "b"]), "with no agreement yet, a pin on either side stays");
        var unpinned = SidebarSync.Merge(S([]), S(["a"]), baseline: S(["a"]));
        check(unpinned.Pins.Count == 0, "a chat unpinned on one side since the last agreement is unpinned on both");
        var pinned = SidebarSync.Merge(S(["a", "b"]), S(["a"]), baseline: S(["a"]));
        check(pinned.Pins.SequenceEqual(["a", "b"]), "a chat pinned on one side since the last agreement is pinned on both");
        var moved = SidebarSync.Merge(S(["a", "b"], ["a", "b"]), S(["a", "b"], ["b", "a"]), baseline: S(["a", "b"], ["a", "b"]));
        check(moved.Order.SequenceEqual(["b", "a"]), "the side that reordered since the agreement gives the order");
        var sort = SidebarSync.Merge(S([], sort: "alpha"), S([], sort: "recency"), baseline: S([], sort: "recency"));
        check(sort.Sort == "alpha", "the side that changed the sort gives the sort");
        check(SidebarSync.ReplaceShared(["x", "a", "y", "b"], new HashSet<string> { "a", "b" }, ["b", "a", "c"]).SequenceEqual(["x", "b", "y", "a", "c"]),
              "shared items take their new order in the places shared items held, others stay put");
        var outside = S(["mine", "a"]).Replacing(new HashSet<string> { "a", "b" }, S(["b"]));
        check(outside.Pins.SequenceEqual(["b", "mine"]), "a pin outside the shared chats is never touched");

        // ---- A pass over real folders ----
        const string Account = "eeeeeeee-5555-4555-8555-555555555555";
        const string Org = "ffffffff-6666-4666-8666-666666666666";
        File.WriteAllText(Path.Combine(main, "config.json"), new JsonObject { ["lastKnownAccountUuid"] = Account }.ToJsonString());
        var mainOrg = Path.Combine(main, "claude-code-sessions", Account, Org);
        Directory.CreateDirectory(mainOrg);
        foreach (var id in new[] { "local_s1", "local_s2" })
            File.WriteAllText(Path.Combine(mainOrg, id + ".json"), new JsonObject { ["title"] = id, ["isStarred"] = false }.ToJsonString());
        var borrower = Paths.ProfileDir("Pins");
        Directory.CreateDirectory(borrower);
        File.WriteAllText(Path.Combine(borrower, "config.json"), new JsonObject { ["lastKnownAccountUuid"] = Account }.ToJsonString());
        Graft.GraftFrom(main, borrower);
        var borrowerOrg = Path.Combine(borrower, "claude-code-sessions", Account, Org);
        check(File.Exists(Path.Combine(borrowerOrg, "local_s1.json")), "the stand-in profiles share their Code chats");

        var scope = Account + "/" + Org;
        var stored = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase)
        {
            [Paths.Normalize(main)] = S(["local_s1"], scope: scope) with { Fingerprint = "m" },
            [Paths.Normalize(borrower)] = S([], scope: scope) with { Fingerprint = "b" },
        };
        var writes = 0;
        SidebarSync.StorageOverride = (profiles, changes, shared) =>
        {
            if (changes != null)
            {
                writes++;
                foreach (var p in profiles) stored[p] = changes[p];
            }
            return profiles.ToDictionary(p => p, p => stored[p], StringComparer.OrdinalIgnoreCase);
        };
        SidebarSync.AnyRunning = _ => false;
        SidebarSync.SynchronizeBeforeOpening(borrower);
        check(writes == 1 && stored[Paths.Normalize(borrower)].Pins.SequenceEqual(["local_s1"]),
              "a chat pinned in the source is pinned in the borrower");
        var record = JsonNode.Parse(File.ReadAllText(Path.Combine(borrowerOrg, "local_s1.json")))!;
        check(record["isStarred"]?.GetValue<bool>() == true, "and its record says so too");
        check(SidebarSync.Status(borrower)?.Contains("synced") == true || SidebarSync.Status(borrower) == ClaudeGraft.Core.S.T("Pinned chats and sort order are synced."),
              "the window can say the pins are synced");

        stored[Paths.Normalize(borrower)] = S([], scope: scope) with { Fingerprint = "b2" };
        SidebarSync.SynchronizeBeforeOpening(borrower);
        check(stored[Paths.Normalize(main)].Pins.Count == 0, "a chat unpinned in the borrower is unpinned in the source");

        writes = 0;
        SidebarSync.AnyRunning = _ => true;
        stored[Paths.Normalize(main)] = S(["local_s2"], scope: scope);
        SidebarSync.SynchronizeBeforeOpening(borrower);
        check(writes == 0 && SidebarSync.Status(borrower) == ClaudeGraft.Core.S.T("Close the Claudes that share these chats, then open this one to sync its pinned chats."),
              "nothing is written while a Claude sharing those chats is open");

        SidebarSync.AnyRunning = _ => false;
        SidebarSync.StorageOverride = (profiles, changes, shared) =>
            changes != null ? throw new IOException("write-failed") : profiles.ToDictionary(p => p, p => stored[p], StringComparer.OrdinalIgnoreCase);
        var before = File.ReadAllText(Path.Combine(Paths.GraftHome, "sidebar-sync.json"));
        SidebarSync.SynchronizeBeforeOpening(borrower);
        check(File.ReadAllText(Path.Combine(Paths.GraftHome, "sidebar-sync.json")) == before, "a failed write never advances the agreement");

        stored[Paths.Normalize(borrower)] = S([], scope: "someone-else/" + Org);
        writes = 0;
        SidebarSync.StorageOverride = (profiles, changes, shared) =>
        {
            if (changes != null) writes++;
            return profiles.ToDictionary(p => p, p => stored[p], StringComparer.OrdinalIgnoreCase);
        };
        SidebarSync.SynchronizeBeforeOpening(borrower);
        check(writes == 0, "a profile whose sidebar belongs to another account is never written");
        SidebarSync.StorageOverride = null;

        // ---- The helper's runtime pieces ----
        var image = "xxxxdL7pKGdnNz796PbbjQWNKmHXBZaB9tsX\u0001\u0009010111011"u8.ToArray();
        check(SidebarSync.SwitchOffIntegrityFuse(image) && System.Text.Encoding.ASCII.GetString(image).EndsWith("010101011"),
              "only the embedded integrity fuse is switched off in the helper's copy");
        var asar = SidebarSync.Asar(("main.js", "x"u8.ToArray()));
        check(BitConverter.ToUInt32(asar, 0) == 4 && System.Text.Encoding.UTF8.GetString(asar).Contains("\"main.js\":{\"size\":1"),
              "the helper's archive is laid out as Electron reads one");

        // ---- The storage script, under Node ----
        RunScript(check, sandbox);
    }

    static void RunScript(Action<bool, string> check, string sandbox)
    {
        var node = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
            .Select(d => { try { return Path.Combine(d.Trim(), "node.exe"); } catch { return ""; } })
            .FirstOrDefault(File.Exists);
        if (node == null) { Console.WriteLine("(node not found: the storage script's checks are skipped)"); return; }
        var dir = Path.Combine(sandbox, "sidebar-script");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "storage.cjs"), SidebarSync.Script);
        File.WriteAllText(Path.Combine(dir, "check.cjs"), """
            // Claude's runtime has structuredClone; an older Node on PATH may not.
            globalThis.structuredClone ??= v => JSON.parse(JSON.stringify(v));
            const s = require('./storage.cjs');
            const frame = {version: 1, state: {pinnedOrder: ['code:local_a', 'chat:x', 'code:local_b'], sortByByMode: {code: 'recency'}, lastSidebarScopeKey: 'acc/org'}};
            const raw = {pin: JSON.stringify({version: 0, state: {starredIds: ['local_a', 'local_b', 'local_mine']}}),
                         local: {'dframe-store': JSON.stringify(frame), 'LSS-persisted.starred-local-code-sessions': null,
                                 'LSS-persisted.dframe-local-slice': null, 'ccd-sync-owner': null, 'ccd-sync-active': null,
                                 'ccd-sync-quarantine': null, 'ccd-sync-pending:ccd/dframe-store': null}};
            const out = [];
            const snap = s.snapshot(raw);
            out.push(snap.pins.join(',') === 'local_a,local_b,local_mine');
            out.push(snap.order.join(',') === 'local_a,local_b,local_mine');
            const next = s.patch(raw, {shared: ['local_a', 'local_b'], pins: ['local_b'], order: ['local_b'], sort: 'recency'});
            const pins = JSON.parse(next.pin).state.starredIds;
            out.push(pins.includes('local_mine') && pins.includes('local_b') && !pins.includes('local_a'));
            out.push(JSON.parse(next.local['dframe-store']).state.pinnedOrder.join(',') === 'code:local_b,chat:x');
            let refused = '';
            try { s.decode({...raw, local: {...raw.local, 'dframe-store': JSON.stringify({version: 2, state: {}})}}); } catch (e) { refused = e.message; }
            out.push(refused.startsWith('unsupported-storage'));
            out.push(s.snapshot({...raw, pin: null}).pins.length === 0);
            console.log(out.map(x => x ? '1' : '0').join(''));
            """);
        var info = new ProcessStartInfo(node, "check.cjs") { WorkingDirectory = dir, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(info)!;
        var result = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(15000);
        string[] sentences =
        [
            "the script reads the pins Claude keeps",
            "and their order from the sidebar record",
            "a change touches only the shared chats, never a pin of the profile's own",
            "and moves the shared chats in the sidebar order, leaving other entries in place",
            "a sidebar record of a version it does not know is refused",
            "a profile with no pin record has no pins, not an unreadable store",
        ];
        for (var i = 0; i < sentences.Length; i++) check(result.Length > i && result[i] == '1', sentences[i]);
    }
}
