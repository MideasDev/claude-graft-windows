using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core.Sidebar;

/// Pinned Code chats, their order and the Code sort, kept in step between
/// Claudes that share a history. Ported from the macOS original; its
/// notes/sidebar-and-settings.md says why each rule is there.
///
/// Pins live in Claude's browser storage (IndexedDB `keyval-store` and the
/// `dframe-store` local-storage record), not in the session records, whose
/// `isStarred` is only a reflection of them. Claude Graft never writes those
/// databases' files: a private copy of Claude's own runtime opens them with the
/// browser's own storage APIs, so the database format is always exactly the
/// one Claude reads.
public static partial class SidebarSync
{
    public sealed record Snapshot(List<string> Pins, List<string> Order, string Sort, double OrderTime = 0,
                                  string? Scope = null, string Fingerprint = "")
    {
        public Snapshot Restricted(ISet<string> shared)
        {
            var members = Pins.Where(shared.Contains).ToHashSet();
            var seen = new HashSet<string>();
            return new([.. members.Order(StringComparer.Ordinal)],
                       [.. Order.Concat(Pins).Where(id => members.Contains(id) && seen.Add(id))], Sort, OrderTime);
        }

        public Snapshot Replacing(ISet<string> shared, Snapshot other) => this with
        {
            Pins = [.. Pins.Where(p => !shared.Contains(p)).Concat(other.Pins).Distinct().Order(StringComparer.Ordinal)],
            Order = ReplaceShared(Order, shared, other.Order),
            Sort = other.Sort,
            OrderTime = Math.Max(OrderTime, other.OrderTime),
        };

        public bool SameChoices(Snapshot other) =>
            Pins.ToHashSet().SetEquals(other.Pins) && Order.SequenceEqual(other.Order) && Sort == other.Sort;
    }

    /// The shared items take their new order in the places shared items held;
    /// everything else stays exactly where it was.
    public static List<string> ReplaceShared(IEnumerable<string> existing, ISet<string> shared, IReadOnlyList<string> wanted)
    {
        var index = 0;
        var result = new List<string>();
        foreach (var item in existing)
        {
            if (!shared.Contains(item)) result.Add(item);
            else if (index < wanted.Count) result.Add(wanted[index++]);
        }
        result.AddRange(wanted.Skip(index));
        var seen = new HashSet<string>();
        return [.. result.Where(seen.Add)];
    }

    /// The last agreement is what tells an unpin from a pin that never
    /// arrived: a side that still matches it did not move, so the other
    /// side's choice wins; with no agreement yet, a pin on either side stays.
    public static Snapshot Merge(Snapshot borrower, Snapshot source, Snapshot? baseline)
    {
        var left = borrower.Pins.ToHashSet();
        var right = source.Pins.ToHashSet();
        var old = baseline?.Pins.ToHashSet() ?? [];
        var pins = new HashSet<string>();
        foreach (var id in left.Union(right).Union(old))
        {
            bool a = left.Contains(id), b = right.Contains(id);
            var pinned = baseline != null ? (a == b ? a : a == old.Contains(id) ? b : a) : a || b;
            if (pinned) pins.Add(id);
        }
        var preferred = baseline != null && borrower.Order.SequenceEqual(baseline.Order) ? source
                      : baseline != null && source.Order.SequenceEqual(baseline.Order) ? borrower
                      : borrower.OrderTime > source.OrderTime ? borrower : source;
        var seen = new HashSet<string>();
        var order = preferred.Order.Concat(source.Order).Concat(borrower.Order).Concat(pins.Order(StringComparer.Ordinal))
            .Where(id => pins.Contains(id) && seen.Add(id)).ToList();
        var sort = baseline != null && source.Sort == baseline.Sort ? borrower.Sort : source.Sort;
        return new([.. pins.Order(StringComparer.Ordinal)], order, sort, Math.Max(borrower.OrderTime, source.OrderTime));
    }

    sealed record Pair(string Key, string Borrower, string Source, string BorrowerStore, string SourceStore, HashSet<string> Shared);

    sealed class State
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Snapshot> Pairs { get; set; } = [];
    }

    sealed class Failure(string reason) : Exception(reason);

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
    static string StateFile => Path.Combine(Paths.GraftHome, "sidebar-sync.json");
    static string StatusFile => Path.Combine(Paths.GraftHome, "sidebar-sync-status.json");

    /// The suite replaces the browser storage with this.
    public static Func<List<string>, Dictionary<string, Snapshot>?, Dictionary<string, HashSet<string>>, Dictionary<string, Snapshot>>? StorageOverride { get; set; }
    public static Func<List<string>, bool> AnyRunning { get; set; } =
        profiles => profiles.Any(p => ClaudeProcesses.IsRunning(Paths.Same(p, Paths.MainProfile) ? null : p));

    static HashSet<string> SessionIds(string store, string profile)
    {
        var codeStore = Path.Combine(profile, "claude-code-sessions");
        if (Fs.IsLink(store) || !Fs.ResolvesInside(store, codeStore)) throw new Failure("linked-chat-store");
        return Fs.Names(store).Where(n => n.StartsWith("local_") && n.EndsWith(".json")).Select(n => n[..^5]).ToHashSet();
    }

    /// Mirror pairs of Code chat folders, between two different profiles,
    /// each folder the one its profile's signed-in account uses, sharing at
    /// least one chat. Profiles are Claude's own and Claude Graft's.
    static List<Pair> Candidates()
    {
        string? ProfileOf(string store)
        {
            var org = Paths.Normalize(store);
            var accountDir = Path.GetDirectoryName(org);
            var code = accountDir == null ? null : Path.GetDirectoryName(accountDir);
            var profile = code == null ? null : Path.GetDirectoryName(code);
            if (profile == null || !Path.GetFileName(code)!.Equals("claude-code-sessions", StringComparison.OrdinalIgnoreCase)) return null;
            if (Graft.Account(profile) is not { } account || !Path.GetFileName(accountDir)!.Equals(account, StringComparison.OrdinalIgnoreCase)) return null;
            return Paths.Same(profile, Paths.MainProfile) || Paths.IsOwnProfile(profile) ? profile : null;
        }
        var pairs = new List<Pair>();
        foreach (var key in Graft.PairKeys())
        {
            if (Graft.PairHalves(key) is not { } halves) continue;
            if (ProfileOf(halves.Borrower) is not { } borrower || ProfileOf(halves.Source) is not { } source || Paths.Same(borrower, source)) continue;
            try
            {
                var shared = SessionIds(halves.Borrower, borrower);
                shared.IntersectWith(SessionIds(halves.Source, source));
                if (shared.Count > 0) pairs.Add(new(key, Paths.Normalize(borrower), Paths.Normalize(source), halves.Borrower, halves.Source, shared));
            }
            catch { }
        }
        return pairs;
    }

    /// Before a closed Claude opens: everything connected to it through a
    /// shared history is read, merged, written and read back, but only when
    /// every one of those Claudes is closed. Claude still opens when the
    /// sync cannot run; nothing here stops a launch.
    public static void SynchronizeBeforeOpening(string profile)
    {
        var pairs = Candidates();
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Paths.Normalize(profile) };
        for (var i = 0; i < pairs.Count; i++)
            foreach (var p in pairs.Where(p => connected.Contains(p.Borrower) || connected.Contains(p.Source)))
            { connected.Add(p.Borrower); connected.Add(p.Source); }
        pairs = pairs.Where(p => connected.Contains(p.Borrower) && connected.Contains(p.Source)).ToList();
        if (pairs.Count == 0) return;
        var profiles = connected.Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (AnyRunning(profiles)) { Record("waiting", profiles); return; }

        try
        {
            var state = File.Exists(StateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile), Json) ?? new() : new State();
            if (state.Version != 1) throw new Failure("unsupported-baseline");
            var original = Storage(profiles, null, []);

            bool Matches(Snapshot? s, string store) =>
                s?.Scope is { } scope && scope.Equals(Path.GetFileName(Path.GetDirectoryName(Paths.Normalize(store))) + "/" + Path.GetFileName(Paths.Normalize(store)), StringComparison.OrdinalIgnoreCase);
            pairs = pairs.Where(p => Matches(original.GetValueOrDefault(p.Borrower), p.BorrowerStore) &&
                                     Matches(original.GetValueOrDefault(p.Source), p.SourceStore)).ToList();
            if (pairs.Count == 0) throw new Failure("account-not-ready");

            var shared = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pairs)
            {
                (shared.TryGetValue(p.Borrower, out var b) ? b : shared[p.Borrower] = []).UnionWith(p.Shared);
                (shared.TryGetValue(p.Source, out var s) ? s : shared[p.Source] = []).UnionWith(p.Shared);
            }

            var wanted = new Dictionary<string, Snapshot>(original, StringComparer.OrdinalIgnoreCase);
            for (var round = 0; round <= pairs.Count; round++)
            {
                var changed = false;
                foreach (var p in pairs)
                {
                    if (!wanted.TryGetValue(p.Borrower, out var one) || !wanted.TryGetValue(p.Source, out var two)) throw new Failure("missing-profile");
                    var a = one.Restricted(p.Shared);
                    var b = two.Restricted(p.Shared);
                    var merged = Merge(a, b, state.Pairs.GetValueOrDefault(p.Key)?.Restricted(p.Shared));
                    changed |= !a.SameChoices(merged) || !b.SameChoices(merged);
                    wanted[p.Borrower] = one.Replacing(p.Shared, merged);
                    wanted[p.Source] = two.Replacing(p.Shared, merged);
                    state.Pairs[p.Key] = merged;
                }
                if (!changed) break;
            }

            var affected = profiles.Where(shared.ContainsKey).ToList();
            foreach (var p in pairs)
                foreach (var folder in new[] { p.BorrowerStore, p.SourceStore })
                    foreach (var id in p.Shared)
                    {
                        var file = Path.Combine(folder, id + ".json");
                        if (Fs.IsLink(file) || ReadRecord(file) == null) throw new Failure("unreadable-session");
                    }

            if (affected.Any(p => !original[p].SameChoices(wanted[p])))
            {
                if (AnyRunning(profiles)) throw new Failure("profile-running");
                var verified = Storage(affected, wanted, shared);
                foreach (var p in affected)
                    if (!verified.TryGetValue(p, out var result) || !result.Restricted(shared[p]).SameChoices(wanted[p].Restricted(shared[p])))
                        throw new Failure("verification-failed");
            }
            foreach (var p in pairs)
            {
                UpdateRecordFlags(p.BorrowerStore, p.Shared, wanted[p.Borrower].Pins.ToHashSet());
                UpdateRecordFlags(p.SourceStore, p.Shared, wanted[p.Source].Pins.ToHashSet());
            }
            // A failed write never advances the agreement: it is saved last.
            var live = Graft.PairKeys().ToHashSet();
            state.Pairs = state.Pairs.Where(kv => live.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            Directory.CreateDirectory(Paths.GraftHome);
            Fs.WriteAtomic(StateFile, JsonSerializer.SerializeToUtf8Bytes(state, Json));
            Record("synced", affected);
        }
        catch (Exception e)
        {
            Diagnostics.Note("sidebar.sync-skipped", e is Failure ? e.Message : e.GetType().Name + " " + e.Message);
            Record("retry", profiles);
        }
    }

    static JsonObject? ReadRecord(string file)
    {
        try { return Fs.ReadShared(file) is { } data ? JsonNode.Parse(data) as JsonObject : null; } catch { return null; }
    }

    static void UpdateRecordFlags(string folder, ISet<string> shared, ISet<string> pins)
    {
        foreach (var id in shared.Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(folder, id + ".json");
            if (Fs.IsLink(file) || ReadRecord(file) is not { } record) throw new Failure("unreadable-session");
            var pinned = pins.Contains(id);
            if (record["isStarred"] is JsonValue v && v.TryGetValue<bool>(out var now) && now == pinned) continue;
            record["isStarred"] = pinned;
            if (!Fs.WriteAtomic(file, Encoding.UTF8.GetBytes(record.ToJsonString()))) throw new Failure("write-failed");
        }
    }

    public static void ForgetPairs(ISet<string> keep)
    {
        try
        {
            if (!File.Exists(StateFile) || JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile), Json) is not { Version: 1 } state) return;
            var remaining = state.Pairs.Where(kv => keep.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (remaining.Count == state.Pairs.Count) return;
            state.Pairs = remaining;
            Fs.WriteAtomic(StateFile, JsonSerializer.SerializeToUtf8Bytes(state, Json));
        }
        catch { }
    }

    public static string? Status(string profile)
    {
        try
        {
            var states = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StatusFile)) ?? [];
            return states.GetValueOrDefault(Paths.Normalize(profile)) switch
            {
                "synced" => S.T("Pinned chats and sort order are synced."),
                "waiting" => S.T("Close the Claudes that share these chats, then open this one to sync its pinned chats."),
                null => null,
                _ => S.T("Pinned chats could not be synced. It will retry the next time this Claude opens."),
            };
        }
        catch { return null; }
    }

    static void Record(string status, IEnumerable<string> profiles)
    {
        try
        {
            var states = File.Exists(StatusFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StatusFile)) ?? [] : [];
            foreach (var p in profiles) states[Paths.Normalize(p)] = status;
            Directory.CreateDirectory(Paths.GraftHome);
            Fs.WriteAtomic(StatusFile, JsonSerializer.SerializeToUtf8Bytes(states));
        }
        catch { }
        Diagnostics.Note("sidebar.sync", $"{status} {string.Join(", ", profiles.Select(Path.GetFileName))}");
    }

    // ======================================================================
    // The storage helper
    // ======================================================================

    public static string Script
    {
        get
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeGraft.Core.Sidebar.sidebar-storage.js")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// Read-only: what the storage helper finds in these profiles. For
    /// `--sidebar-read`, which writes nothing anywhere but its own result.
    public static Dictionary<string, Snapshot> Read(IEnumerable<string> profiles) =>
        Storage([.. profiles.Select(Paths.Normalize)], null, []);

    sealed record Reply(bool Ok, List<ReplyProfile>? Profiles, string? Error);
    sealed record ReplyProfile(string Path, List<string> Pins, List<string> Order, string Sort, double OrderTime, string? Scope, string Fingerprint);

    /// Reads (no changes) or writes and reads back (with changes) every
    /// profile named, in one run of the helper. All are read and checked
    /// before any is written, a backup of what was there is kept, and a
    /// write that does not read back as written is put back.
    static Dictionary<string, Snapshot> Storage(List<string> profiles, Dictionary<string, Snapshot>? changes, Dictionary<string, HashSet<string>> shared)
    {
        if (StorageOverride != null) return StorageOverride(profiles, changes, shared);
        var helper = PrepareHelper();
        var scratch = Path.Combine(Paths.GraftHome, ".sidebar-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            var backups = Path.Combine(Paths.GraftHome, "sidebar-backups");
            Directory.CreateDirectory(backups);
            var output = Path.Combine(scratch, "result.json");
            var entries = new JsonArray();
            foreach (var p in profiles)
            {
                var entry = new JsonObject { ["path"] = p };
                if (changes?.GetValueOrDefault(p) is { } snapshot && shared.GetValueOrDefault(p) is { } ids)
                {
                    var scoped = snapshot.Restricted(ids);
                    entry["change"] = new JsonObject
                    {
                        ["expected"] = snapshot.Fingerprint,
                        ["shared"] = new JsonArray(ids.Order(StringComparer.Ordinal).Select(i => (JsonNode)i).ToArray()),
                        ["pins"] = new JsonArray(scoped.Pins.Select(i => (JsonNode)i).ToArray()),
                        ["order"] = new JsonArray(scoped.Order.Select(i => (JsonNode)i).ToArray()),
                        ["sort"] = scoped.Sort,
                    };
                }
                entries.Add(entry);
            }
            var request = new JsonObject
            {
                ["action"] = changes == null ? "read" : "write", ["profiles"] = entries, ["scratch"] = scratch, ["output"] = output,
                ["backup"] = Path.Combine(backups, Guid.NewGuid().ToString("N") + ".json"),
            };
            var requestFile = Path.Combine(scratch, "request.json");
            File.WriteAllText(requestFile, request.ToJsonString());

            var info = new ProcessStartInfo(helper, $"\"{requestFile}\"") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) ||
                                                                 k.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase)).ToList())
                info.Environment.Remove(key);
            using var process = Process.Start(info)!;
            if (!process.WaitForExit(25_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new Failure("storage-timeout");
            }
            if (!File.Exists(output)) throw new Failure("storage-unavailable");
            var reply = JsonSerializer.Deserialize<Reply>(File.ReadAllText(output), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (reply is not { Ok: true, Profiles: { } result } || result.Count != profiles.Count)
                throw new Failure(reply?.Error ?? "storage-unavailable");
            return result.ToDictionary(r => Paths.Normalize(r.Path),
                r => new Snapshot(r.Pins, r.Order, r.Sort, r.OrderTime, r.Scope, r.Fingerprint), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch { }
        }
    }

    public const string HelperFormat = "windows-fuse-1";
    public static Func<string?>? RuntimeOverride { get; set; }

    /// Claude's runtime folder: the one holding the executable beside
    /// `resources\app.asar`. The Store build is reached through its app
    /// execution alias, whose reparse point names the real executable.
    public static string? RuntimeFolder()
    {
        if (RuntimeOverride != null) return RuntimeOverride();
        var exe = ClaudeInstall.Executable;
        if (exe == null) return null;
        var target = AliasTarget(exe) ?? exe;
        var dir = Path.GetDirectoryName(target);
        if (dir != null && File.Exists(Path.Combine(dir, "resources", "app.asar"))) return dir;
        // The classic installer keeps a launcher at its root and the app in app-<version>.
        if (dir != null && Directory.Exists(dir))
            return Directory.GetDirectories(dir, "app-*").Where(d => File.Exists(Path.Combine(d, "resources", "app.asar")))
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)).FirstOrDefault();
        return null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(IntPtr device, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    /// An app execution alias (reparse tag APPEXECLINK) holds a version
    /// number and then NUL-separated strings: the package, the app's user
    /// model id, and the executable it runs.
    static string? AliasTarget(string alias)
    {
        const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000, FsctlGetReparsePoint = 0x000900A8;
        var handle = CreateFile(alias, 0, 7, IntPtr.Zero, 3, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;
        try
        {
            var buffer = new byte[16 * 1024];
            if (!DeviceIoControl(handle, FsctlGetReparsePoint, IntPtr.Zero, 0, buffer, buffer.Length, out var size, IntPtr.Zero)) return null;
            if (BitConverter.ToUInt32(buffer, 0) != 0x8000001B) return null;
            var length = BitConverter.ToUInt16(buffer, 4);
            var strings = Encoding.Unicode.GetString(buffer, 12, Math.Min(length - 4, size - 12)).Split('\0');
            return strings.FirstOrDefault(s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(s));
        }
        catch { return null; }
        finally { CloseHandle(handle); }
    }

    /// A private copy of Claude's runtime with this app's own script in its
    /// `app.asar`, made once per Claude version. The copy's executable is
    /// renamed, so it is never taken for a Claude, and has its embedded
    /// integrity check switched off, the one fuse that would refuse an
    /// archive Anthropic did not make. The installed Claude is never changed.
    public static string PrepareHelper()
    {
        var runtime = RuntimeFolder() ?? throw new Failure("no-runtime");
        var exe = Directory.GetFiles(runtime, "*.exe").FirstOrDefault(f =>
            Path.GetFileName(f).Equals("claude.exe", StringComparison.OrdinalIgnoreCase)) ?? throw new Failure("no-runtime");
        var version = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? new FileInfo(exe).Length.ToString();
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version + "\n" + Script + "\n" + HelperFormat)))[..20];
        var cache = Path.Combine(Paths.GraftHome, "sidebar-runtime");
        var destination = Path.Combine(cache, key);
        var helper = Path.Combine(destination, "graft-sidebar.exe");
        if (File.Exists(Path.Combine(destination, "ready"))) return helper;

        var staging = Path.Combine(cache, ".build-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in Directory.GetFiles(runtime))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(staging, name));
            }
            foreach (var dir in new[] { "locales" })
            {
                var from = Path.Combine(runtime, dir);
                if (!Directory.Exists(from)) continue;
                Directory.CreateDirectory(Path.Combine(staging, dir));
                foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(staging, dir, Path.GetFileName(file)));
            }
            var image = File.ReadAllBytes(exe);
            if (!SwitchOffIntegrityFuse(image)) throw new Failure("unsupported-runtime");
            File.WriteAllBytes(Path.Combine(staging, "graft-sidebar.exe"), image);
            Directory.CreateDirectory(Path.Combine(staging, "resources"));
            File.WriteAllBytes(Path.Combine(staging, "resources", "app.asar"), Asar(
                ("package.json", Encoding.UTF8.GetBytes("""{"name":"graft-sidebar","version":"1.0.0","main":"main.js"}""")),
                ("main.js", Encoding.UTF8.GetBytes(Script))));
            File.WriteAllText(Path.Combine(staging, "ready"), version);
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(staging, destination);
            Diagnostics.Note("sidebar.runtime-made", version);
            // Older copies, for Claudes no longer installed, go.
            foreach (var old in Directory.GetDirectories(cache).Where(d => !Paths.Same(d, destination) && !Path.GetFileName(d).StartsWith('.')))
                try { Directory.Delete(old, true); } catch { }
            return helper;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    /// Electron's fuses sit after a fixed sentinel: a version byte, a count,
    /// then one character per fuse ('0' off, '1' on). The fifth is the
    /// embedded asar integrity check.
    public static bool SwitchOffIntegrityFuse(byte[] image)
    {
        var sentinel = "dL7pKGdnNz796PbbjQWNKmHXBZaB9tsX"u8;
        var at = image.AsSpan().IndexOf(sentinel);
        if (at < 0) return false;
        var count = image[at + sentinel.Length + 1];
        const int Integrity = 4;
        if (count <= Integrity) return false;
        var slot = at + sentinel.Length + 2 + Integrity;
        if (image[slot] is not ((byte)'0' or (byte)'1' or (byte)'r')) return false;
        image[slot] = (byte)'0';
        return true;
    }

    /// The asar format: a pickled JSON header naming each file's offset and
    /// size, then the files.
    public static byte[] Asar(params (string Name, byte[] Data)[] files)
    {
        static byte[] Word(int n) => BitConverter.GetBytes((uint)n);
        var entries = new JsonObject();
        var body = new MemoryStream();
        foreach (var (name, data) in files)
        {
            entries[name] = new JsonObject { ["size"] = data.Length, ["offset"] = body.Length.ToString() };
            body.Write(data);
        }
        var header = Encoding.UTF8.GetBytes(new JsonObject { ["files"] = entries }.ToJsonString());
        var payload = new List<byte>(Word(header.Length));
        payload.AddRange(header);
        while (payload.Count % 4 != 0) payload.Add(0);
        var pickle = new List<byte>(Word(payload.Count));
        pickle.AddRange(payload);
        return [.. Word(4), .. Word(pickle.Count), .. pickle, .. body.ToArray()];
    }
}
