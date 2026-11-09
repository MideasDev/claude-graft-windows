using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// Sharing one profile's chats with another: the graft. Ported from the
/// macOS original rule for rule, and every rule there exists because of a
/// real loss; the original's `notes/chat-sharing.md` says which, and
/// `docs/PORTING.md` what differs on Windows. Read both before changing
/// anything here.
///
/// The shape: settings that two Claudes can share are linked; chat stores
/// are never linked, because a Claude writes no session record into a folder
/// that resolves outside itself. Each profile gets its own copy of the chats
/// it borrows, and every pass carries changes both ways ("mirroring"). What
/// the profile owned before is moved to a hidden `.<name>.graft-own` sibling
/// (the stash), which is what makes going back possible.
public static class Graft
{
    /// Files a second profile can share wholesale. Everything else is login
    /// material, per-organization cache, or a store two running Claudes
    /// cannot both hold open.
    public static readonly string[] SharedItems =
    [
        "Claude Extensions",
        "Claude Extensions Settings",
        "extensions-installations.json",
        "window-state.json",
        "git-worktrees.json",
        "claude-ssh-remote",
        "ssh_configs.json",
    ];

    /// Chat history: `<store>\<accountUuid>\<orgUuid>\local_*.json`.
    public static readonly string[] ChatStores = ["claude-code-sessions", "local-agent-mode-sessions"];

    /// Sits beside the account folders in a store without being one.
    public static readonly HashSet<string> NonAccountStoreItems = new(StringComparer.OrdinalIgnoreCase) { "skills-plugin" };

    static readonly string[] AppearanceKeys = ["userThemeMode", "locale"];

    public const string StashSuffix = ".graft-own";
    const int ChatStoreDepth = 3;

    static string Join(string a, string b) => Path.Combine(a, b);
    static bool Hidden(string name) => name.StartsWith('.') || name.EndsWith(StashSuffix, StringComparison.OrdinalIgnoreCase);

    // ======================================================================
    // Stashing
    // ======================================================================

    public static string StashPath(string path)
    {
        var full = Paths.Normalize(path);
        return Join(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + StashSuffix);
    }

    /// Whether this app has put a folder away, or any folder it sits in. A
    /// cross-account graft stashes the organization folder; two profiles on
    /// one account stash the whole store above it.
    public static bool IsStashedAway(string folder)
    {
        var here = Paths.Normalize(folder);
        for (var i = 0; i < ChatStoreDepth && here != null; i++)
        {
            if (Fs.Exists(StashPath(here))) return true;
            here = Path.GetDirectoryName(here);
        }
        return false;
    }

    /// Moves a profile's own item aside rather than destroying it. A stash
    /// already there is not proof of a duplicate: Claude writes by renaming
    /// a temporary over a file, which replaces a link with a real file, so
    /// the two are folded together and the copy in use wins.
    static void Stash(string path)
    {
        if (!Fs.Exists(path) || Fs.IsLink(path)) return;
        var stashed = StashPath(path);
        if (Fs.Exists(stashed)) Absorb(stashed, path);
        if (Fs.Exists(stashed))
        {
            Diagnostics.Note("stash.blocked", $"{path} (a stash is there and could not be folded back in)");
            return;
        }
        Diagnostics.Note("stash", $"{path} held={Fs.Names(path).Length}");
        Fs.Move(path, stashed);
    }

    static void Absorb(string stashed, string live)
    {
        if (!Fs.IsDirectory(stashed) || !Fs.IsDirectory(live) || Fs.IsLink(stashed) || Fs.IsLink(live))
        {
            // Two files: the profile overwrote the stashed one itself. A
            // folder against a file is left alone rather than guessed at.
            if (!Fs.IsDirectory(stashed) && !Fs.IsDirectory(live)) Fs.Delete(stashed);
            return;
        }
        foreach (var name in Fs.Names(stashed))
        {
            var from = Join(stashed, name);
            var to = Join(live, name);
            if (Fs.Exists(to)) Absorb(from, to);
            else Fs.Move(from, to);
        }
        if (Fs.NamesOrNull(stashed) is { Length: 0 })
            try { Directory.Delete(stashed); } catch { }
    }

    /// Puts back what the profile owns, folding it into anything that has
    /// appeared where the link was rather than giving up on it.
    static void Unstash(string link)
    {
        if (Fs.IsLink(link)) Fs.RemoveLink(link);
        var stashed = StashPath(link);
        if (!Fs.Exists(stashed)) return;
        var held = Fs.Names(stashed).Length;
        if (Fs.Exists(link))
        {
            Diagnostics.Note("unstash.absorb", $"{link} returning={held} onto={Fs.Names(link).Length}");
            Absorb(stashed, link);
            return;
        }
        Diagnostics.Note("unstash", $"{link} returning={held}");
        Fs.Move(stashed, link);
    }

    /// Points `link` at `target`, keeping whatever was there.
    public static bool Relink(string target, string link)
    {
        if (!Fs.Exists(target)) return false;
        if (Fs.SameReal(target, link) && !Fs.IsLink(link)) return false;
        if (Fs.IsLink(link))
        {
            if (Fs.LinkTarget(link) is { } current && Paths.Same(current, target)) return true;
            Fs.RemoveLink(link);
        }
        else if (Fs.Exists(link)) Stash(link);
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.Normalize(link))!);
        return Fs.CreateLink(link, target);
    }

    /// The most recently touched child folder: the active organization. A
    /// folder, because Claude writes `<org>.profile-origin.json` beside them.
    public static string? NewestChild(string dir) =>
        Fs.Names(dir)
            .Where(n => !Hidden(n) && Fs.IsDirectory(Join(dir, n)))
            .OrderByDescending(n => Fs.Modified(Join(dir, n)))
            .FirstOrDefault();

    /// The organization a profile is using under one account. The newest
    /// folder is not it: Claude touches an organization folder it is not
    /// showing (a `scheduled-tasks.json` is enough), and on a real machine
    /// that sent a borrowed history into an empty organization the sidebar
    /// never looked at. The organization in use is where the profile's own
    /// records are written, so the one holding the newest record of its own
    /// wins, a record being its own when it is not a byte-for-byte copy of
    /// the source's (copies are this app's doing, and the newest of them all).
    /// With nothing of its own anywhere, the folder already paired, then the
    /// newest folder, as before.
    public static string? ActiveOrganization(string accountDir, string? sourceOrg = null)
    {
        var orgs = Fs.Names(accountDir).Where(n => !Hidden(n) && Fs.IsDirectory(Join(accountDir, n))).ToList();
        if (orgs.Count <= 1) return orgs.FirstOrDefault();

        DateTime NewestOwn(string org)
        {
            var dir = Join(accountDir, org);
            var newest = DateTime.MinValue;
            foreach (var name in Fs.Names(dir).Where(IsMirrored))
            {
                var path = Join(dir, name);
                if (sourceOrg != null && Fs.ReadShared(Join(sourceOrg, name)) is { } theirs &&
                    Fs.ReadShared(path) is { } mine && mine.AsSpan().SequenceEqual(theirs)) continue;
                var at = Fs.Modified(path);
                if (at > newest) newest = at;
            }
            return newest;
        }

        var byRecord = orgs.Select(o => (Org: o, At: NewestOwn(o))).Where(x => x.At > DateTime.MinValue)
            .OrderByDescending(x => x.At).FirstOrDefault();
        if (byRecord.Org != null) return byRecord.Org;
        var paired = orgs.FirstOrDefault(o => MirrorPairsBorrowedBy(Join(accountDir, o)).Count > 0);
        return paired ?? NewestChild(accountDir);
    }

    /// The folder `parent` keeps `name` in: exactly that, or the one folder
    /// there whose name is a prefix of it (or it of them), at least eight
    /// characters, and only where there is exactly one. A profile in local
    /// mode was measured spelling account and organization by eight.
    public static string? CounterpartDirectory(string parent, string name)
    {
        if (Fs.IsDirectory(Join(parent, name))) return name;
        var candidates = Fs.Names(parent)
            .Where(n => !Hidden(n))
            .Where(n => Math.Min(n.Length, name.Length) >= 8 &&
                        (n.StartsWith(name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
            .Where(n => Fs.IsDirectory(Join(parent, n)))
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    // ======================================================================
    // Profile identity
    // ======================================================================

    /// An unreadable `config.json` is not an empty one. No file at all is a
    /// profile never signed in (an empty object); a file that will not parse
    /// is one caught mid-rename (null), and nothing may decide where chats
    /// go, or write that file, on the strength of it.
    public static JsonObject? ReadableConfig(string profile)
    {
        var path = Join(profile, "config.json");
        if (!File.Exists(path)) return Fs.Exists(path) ? null : new JsonObject();
        var data = Fs.ReadShared(path);
        if (data == null) return null;
        try { return JsonNode.Parse(data) as JsonObject; } catch { return null; }
    }

    public static string? Account(string profile) =>
        ReadableConfig(profile)?["lastKnownAccountUuid"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ======================================================================
    // The mirror state
    // ======================================================================

    sealed class MirrorState
    {
        public Dictionary<string, Dictionary<string, string>> Pairs { get; set; } = [];
    }

    static string StateFile => Join(Paths.GraftHome, "mirrored-chats.json");
    static readonly object StateGate = new();

    static MirrorState LoadState()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize<MirrorState>(File.ReadAllText(StateFile),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception e) { Diagnostics.Note("mirror.state-unreadable", e.Message); }
        return new();
    }

    static void SaveState(MirrorState state)
    {
        Directory.CreateDirectory(Paths.GraftHome);
        if (Fs.WriteAtomic(StateFile, JsonSerializer.SerializeToUtf8Bytes(state,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })))
            // A pair no longer mirrored takes its sidebar agreement with it.
            Sidebar.SidebarSync.ForgetPairs(state.Pairs.Keys.ToHashSet());
    }

    /// A character no path holds, so the halves always come back apart.
    const char PairSeparator = '\0';

    /// Always written borrower first: after a merge both folders hold the
    /// same bytes, and the order is the only record of which side is which.
    public static string PairKey(string one, string other) => Fs.RealPath(one) + PairSeparator + Fs.RealPath(other);

    public static (string Borrower, string Source)? PairHalves(string key)
    {
        var halves = key.Split(PairSeparator);
        if (halves.Length != 2 || !halves.All(Path.IsPathRooted)) return null;
        return (halves[0], halves[1]);
    }

    public static List<string> PairKeys() { lock (StateGate) return [.. LoadState().Pairs.Keys.Order(StringComparer.Ordinal)]; }

    static bool AtOrInside(string side, string path) =>
        side.Equals(path, StringComparison.OrdinalIgnoreCase) ||
        side.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase);

    /// Every pair with either half at or in this folder: for a profile or a
    /// Claude that is gone, where both roles are finished.
    public static List<string> MirrorPairsUnder(string folder) => MirrorPairs(folder, borrowingHalfOnly: false);

    /// Every pair this folder borrows through, never one it lends. Also the
    /// answer to "has mirroring been set up here before", which is asked of
    /// the state file and never of the stash.
    public static List<string> MirrorPairsBorrowedBy(string folder) => MirrorPairs(folder, borrowingHalfOnly: true);

    static List<string> MirrorPairs(string folder, bool borrowingHalfOnly)
    {
        var path = Fs.RealPath(folder);
        return PairKeys().Where(key =>
        {
            if (PairHalves(key) is not { } roles) return false;
            return AtOrInside(roles.Borrower, path) || (!borrowingHalfOnly && AtOrInside(roles.Source, path));
        }).ToList();
    }

    public enum MirrorAction { Nothing, CopyToOther, CopyToOne, RemoveFromOne, RemoveFromOther, Conflict }

    /// Which way one record moves. The baseline is what makes an absence
    /// mean something: a name on one side only is a new chat or a deleted
    /// one, and the two look the same on disk.
    public static MirrorAction MirrorDecision(string? one, string? other, string? baseline)
    {
        if (one == other) return MirrorAction.Nothing;
        if (one != null && other == null)
            return baseline == null ? MirrorAction.CopyToOther : baseline == one ? MirrorAction.RemoveFromOne : MirrorAction.Conflict;
        if (other != null && one == null)
            return baseline == null ? MirrorAction.CopyToOne : baseline == other ? MirrorAction.RemoveFromOther : MirrorAction.Conflict;
        if (one == baseline) return MirrorAction.CopyToOne;
        if (other == baseline) return MirrorAction.CopyToOther;
        return MirrorAction.Conflict;
    }

    /// Brings every pair ever mirrored back into line. A launcher squares up
    /// its own pair on the way past; this is what reaches the source.
    public static int MirrorKnownPairs()
    {
        var moved = 0;
        foreach (var key in PairKeys())
            if (PairHalves(key) is { } pair) moved += MirrorChatFolders(pair.Borrower, pair.Source);
        return moved;
    }

    /// The names a mirror pass is responsible for: the records and the
    /// markers saying which were deleted. Everything else in an organization
    /// folder belongs to the profile that wrote it.
    public static bool IsMirrored(string name) =>
        (name.StartsWith("local_", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ||
        name.StartsWith("deleted_", StringComparison.Ordinal);

    static double RecordMoment(string path)
    {
        try
        {
            if (Fs.ReadShared(path) is { } data && JsonNode.Parse(data)?["lastActivityAt"] is JsonValue v && v.TryGetValue<double>(out var at))
                return at;
        }
        catch { }
        return (Fs.Modified(path) - DateTime.UnixEpoch).TotalMilliseconds;
    }

    /// Brings two organization folders into line and writes down what they
    /// agreed on. A folder that cannot be read is not an empty one, and a
    /// record that exists but cannot be read right now (Windows lets a file
    /// being written refuse readers) is skipped this pass: read as absent it
    /// would be carried across as a deletion.
    public static int MirrorChatFolders(string one, string other)
    {
        if (Fs.SameReal(one, other)) return 0;
        foreach (var folder in new[] { one, other })
        {
            if (Fs.Exists(folder)) continue;
            // Only the folder, never the path to it: a profile someone
            // deleted must not be built back up around a pass.
            if (!Fs.IsDirectory(Path.GetDirectoryName(Paths.Normalize(folder))!)) return 0;
            try { Directory.CreateDirectory(folder); } catch { return 0; }
        }
        var oneNames = Fs.NamesOrNull(one);
        var otherNames = Fs.NamesOrNull(other);
        if (oneNames == null || otherNames == null)
        {
            Diagnostics.Note("mirror.unread", $"{one} | {other}");
            return 0;
        }

        lock (StateGate)
        {
            var state = LoadState();
            var key = PairKey(one, other);
            var baseline = state.Pairs.TryGetValue(key, out var known) ? new Dictionary<string, string>(known) : [];
            var moved = 0;

            // A side holding none of the names the baseline describes, while
            // the other still holds them, was stashed or replaced wholesale,
            // not cleared one chat at a time. Carried across as deletions it
            // once took 150 chats from the only profile that had them.
            if (baseline.Count > 0)
            {
                var described = baseline.Keys.ToHashSet();
                var hereHasNone = !oneNames.Where(IsMirrored).Any(described.Contains);
                var thereHasNone = !otherNames.Where(IsMirrored).Any(described.Contains);
                if (hereHasNone != thereHasNone)
                {
                    Diagnostics.Note("mirror.baseline-forgotten", $"{one} (one side holds none of what it held)");
                    baseline = [];
                }
            }

            (byte[] Data, string Digest)? Read(string folder, string name)
            {
                var data = Fs.ReadShared(Join(folder, name));
                return data == null ? null : (data, Fs.Digest(data));
            }

            var names = oneNames.Where(IsMirrored).Union(otherNames.Where(IsMirrored)).Order(StringComparer.Ordinal).ToList();
            foreach (var name in names)
            {
                var here = Read(one, name);
                var there = Read(other, name);
                if ((here == null && File.Exists(Join(one, name))) || (there == null && File.Exists(Join(other, name))))
                {
                    Diagnostics.Note("mirror.busy", name);
                    continue;
                }
                var action = MirrorDecision(here?.Digest, there?.Digest, baseline.GetValueOrDefault(name));
                // Both moved. A record that still exists beats one that was
                // deleted: a chat put back is a nuisance, one taken away is not.
                if (action == MirrorAction.Conflict)
                {
                    action = (here, there) switch
                    {
                        (null, not null) => MirrorAction.CopyToOne,
                        (not null, null) => MirrorAction.CopyToOther,
                        (not null, not null) => RecordMoment(Join(one, name)) >= RecordMoment(Join(other, name))
                            ? MirrorAction.CopyToOther : MirrorAction.CopyToOne,
                        _ => MirrorAction.Nothing,
                    };
                }
                switch (action)
                {
                    case MirrorAction.Nothing:
                        if (here != null) baseline[name] = here.Value.Digest;
                        break;
                    case MirrorAction.CopyToOther:
                    case MirrorAction.CopyToOne:
                        var source = action == MirrorAction.CopyToOther ? here : there;
                        var destination = action == MirrorAction.CopyToOther ? other : one;
                        // The baseline says what the filesystem did, never
                        // what the pass meant to do.
                        if (source == null || !Fs.WriteAtomic(Join(destination, name), source.Value.Data)) break;
                        baseline[name] = source.Value.Digest;
                        moved++;
                        break;
                    case MirrorAction.RemoveFromOne:
                    case MirrorAction.RemoveFromOther:
                        var target = Join(action == MirrorAction.RemoveFromOne ? one : other, name);
                        try { File.Delete(target); } catch { break; }
                        if (File.Exists(target)) break;
                        baseline.Remove(name);
                        moved++;
                        break;
                }
            }

            // A name gone from both sides is never visited above, and left in
            // the baseline it would one day read as a deletion to carry over.
            var present = names.ToHashSet();
            state.Pairs[key] = baseline.Where(p => present.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            SaveState(state);
            if (moved > 0) Diagnostics.Note("mirror", $"{Path.GetFileName(one)} moved={moved}");
            return moved;
        }
    }

    // ======================================================================
    // Grafting
    // ======================================================================

    /// Shares the safe files, then mirrors the chats.
    public static void GraftFrom(string source, string profile)
    {
        if (Fs.SameReal(source, profile)) return;
        Directory.CreateDirectory(profile);
        foreach (var item in SharedItems) Relink(Join(source, item), Join(profile, item));
        try { DesktopServers.CopyMissing(source, profile); }
        catch (Exception e) { Diagnostics.Note("settings.unread", $"{Path.GetFileName(profile)} {e.Message}"); }
        MirrorChatStores(source, profile);
        CopyAppearance(source, profile);
    }

    /// A folder ready to mirror into: a link left by an older shape is
    /// removed, and on the first pass the profile's own copy is stashed.
    static void OpenForMirror(string folder, bool firstPass)
    {
        if (Fs.IsLink(folder)) Fs.RemoveLink(folder);
        else if (firstPass) Stash(folder);
        Directory.CreateDirectory(folder);
    }

    /// A first pass that finds a stash already there is a second one. What
    /// the source holds byte for byte, and the stash does not name, was
    /// borrowed, and comes back on the next pass anyway. Only on a folder
    /// that resolves inside the profile: through a link it *is* the source.
    static int DropBorrowedCopies(string folder, string source, string store)
    {
        var profile = Path.GetDirectoryName(Paths.Normalize(store))!;
        if (!Fs.ResolvesInside(folder, profile)) return 0;
        if (StashedCounterpart(folder, store) is not { } own) return 0;
        var dropped = 0;
        foreach (var name in Fs.Names(folder).Where(IsMirrored))
        {
            if (Fs.Exists(Join(own, name))) continue;
            var mine = Fs.ReadShared(Join(folder, name));
            var theirs = Fs.ReadShared(Join(source, name));
            if (mine == null || theirs == null || !mine.AsSpan().SequenceEqual(theirs)) continue;
            try { File.Delete(Join(folder, name)); dropped++; } catch { }
        }
        if (dropped > 0) Diagnostics.Note("mirror.reborrowed", $"{folder} dropped={dropped}");
        return dropped;
    }

    /// The profile's own chats go into the shared set too, so both sidebars
    /// hold the union; the stash keeps the copy saying which were its own.
    static int SeedOwnChats(string folder, string store)
    {
        if (StashedCounterpart(folder, store) is not { } own) return 0;
        var seeded = 0;
        foreach (var name in Fs.Names(own).Where(IsMirrored))
            if (!Fs.Exists(Join(folder, name)) && Fs.Copy(Join(own, name), Join(folder, name))) seeded++;
        Diagnostics.Note("mirror.seed", $"{folder} own={seeded}");
        return seeded;
    }

    /// Where a folder's own copy went: its sibling for a cross-account
    /// graft, or one level down inside a stashed store for one account.
    static string? StashedCounterpart(string folder, string store)
    {
        var sibling = StashPath(folder);
        if (Fs.IsDirectory(sibling)) return sibling;
        var full = Paths.Normalize(folder);
        var account = Path.GetFileName(Path.GetDirectoryName(full)!);
        var inside = Join(Join(StashPath(store), account), Path.GetFileName(full));
        return Fs.IsDirectory(inside) ? inside : null;
    }

    public static void MirrorChatStores(string source, string profile)
    {
        if (Account(source) is not { } sourceAccount) return;
        if (ReadableConfig(profile) is not { } own) return;
        var ownAccount = own["lastKnownAccountUuid"] is JsonValue v && v.TryGetValue<string>(out var a) ? a : null;

        foreach (var store in ChatStores)
        {
            var src = Join(source, store);
            var dst = Join(profile, store);
            if (!Fs.IsDirectory(src)) continue;
            if (ownAccount != null && !ownAccount.Equals(sourceAccount, StringComparison.OrdinalIgnoreCase))
                MirrorAcrossAccounts(src, sourceAccount, dst, ownAccount);
            else
                MirrorWithinOneAccount(src, dst, ownAccount ?? sourceAccount);

            // Keyed by organization and not a sidebar, so it stays a link;
            // after both branches, since one account stashes the whole store.
            var skills = Join(src, "skills-plugin");
            if (Fs.IsDirectory(skills)) Relink(skills, Join(dst, "skills-plugin"));
        }
    }

    static void MirrorWithinOneAccount(string src, string dst, string account)
    {
        // The source is read before anything of the profile's is moved: a
        // source with nothing under this account once put a whole history
        // away and left an empty folder in its place, every launch.
        if (CounterpartDirectory(src, account) is not { } sourceAccountDir)
        {
            Diagnostics.Note("mirror.nothingToShare", $"{src} (no folder for this account)");
            return;
        }
        var theirAccount = Join(src, sourceAccountDir);
        var orgs = Fs.Names(theirAccount).Where(n => !Hidden(n) && Fs.IsDirectory(Join(theirAccount, n)))
            .Order(StringComparer.Ordinal).ToList();
        if (orgs.Count == 0)
        {
            Diagnostics.Note("mirror.nothingToShare", $"{theirAccount} (no organization folder)");
            return;
        }

        var ownAccountDir = CounterpartDirectory(dst, account) ?? account;
        var mine = orgs.Select(org => Join(Join(dst, ownAccountDir),
            CounterpartDirectory(Join(dst, ownAccountDir), org) ?? org)).ToList();
        var theirs = orgs.Select(org => Join(theirAccount, org)).ToList();
        ForgetStalePairs(dst, mine.Zip(theirs).ToList());

        // Read once, before the store is opened, and held for every
        // organization: the first pass stashes the store as a whole.
        var firstPass = MirrorPairsBorrowedBy(dst).Count == 0;
        if (firstPass)
            for (var i = 0; i < mine.Count; i++) DropBorrowedCopies(mine[i], theirs[i], dst);
        OpenForMirror(dst, firstPass);
        for (var i = 0; i < mine.Count; i++)
        {
            OpenForMirror(mine[i], firstPass: false);
            if (firstPass) SeedOwnChats(mine[i], dst);
            MirrorChatFolders(mine[i], theirs[i]);
        }
    }

    static void MirrorAcrossAccounts(string src, string sourceAccount, string dst, string ownAccount)
    {
        if (CounterpartDirectory(src, sourceAccount) is not { } sourceAccountDir ||
            ActiveOrganization(Join(src, sourceAccountDir)) is not { } sourceOrg)
        {
            Diagnostics.Note("mirror.nothingToShare", $"{src} (no organization folder for the source's account)");
            return;
        }
        var theirOrg = Join(Join(src, sourceAccountDir), sourceOrg);
        var ownAccountDir = CounterpartDirectory(dst, ownAccount) ?? ownAccount;
        var orgHeldBySource = CounterpartDirectory(src, ownAccount) is { } held ? ActiveOrganization(Join(src, held)) : null;
        if ((ActiveOrganization(Join(dst, ownAccountDir), theirOrg) ?? orgHeldBySource) is not { } ownOrg) return;
        Diagnostics.Note("mirror.organizations", $"{ownAccountDir}\\{ownOrg} <- {sourceAccountDir}\\{sourceOrg}");

        if (Fs.IsLink(dst)) OpenForMirror(dst, firstPass: false);
        var mine = Join(Join(dst, ownAccountDir), ownOrg);
        var theirs = Join(Join(src, sourceAccountDir), sourceOrg);
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        ForgetStalePairs(dst, [(mine, theirs)]);
        var firstPass = MirrorPairsBorrowedBy(mine).Count == 0;
        if (firstPass) DropBorrowedCopies(mine, theirs, dst);
        OpenForMirror(mine, firstPass);
        if (firstPass) SeedOwnChats(mine, dst);
        MirrorChatFolders(mine, theirs);
    }

    /// Theme and language, the only keys taken from the source's
    /// `config.json`; the rest of that file is the profile's own login.
    static void CopyAppearance(string source, string profile)
    {
        var from = ReadableConfig(source);
        if (from == null || from.Count == 0) return;
        if (ReadableConfig(profile) is not { } into) return;
        var wanted = AppearanceKeys.Where(from.ContainsKey).ToList();
        if (!wanted.Any(k => !JsonNode.DeepEquals(into[k], from[k]))) return;
        foreach (var k in wanted) into[k] = from[k]?.DeepClone();
        Fs.WriteAtomic(Join(profile, "config.json"), Encoding.UTF8.GetBytes(into.ToJsonString()));
    }

    public static void ForgetMirrors(string profile)
    {
        var stale = MirrorPairsUnder(profile).ToHashSet();
        if (stale.Count == 0) return;
        Diagnostics.Note("mirror.forget", $"{Path.GetFileName(profile)} pairs={stale.Count}");
        DropPairs(stale);
    }

    static void DropPairs(HashSet<string> stale)
    {
        lock (StateGate)
        {
            var state = LoadState();
            foreach (var key in stale) state.Pairs.Remove(key);
            SaveState(state);
        }
    }

    /// A pair naming where a profile's chats used to live (before a new
    /// sign-in moved them) is dropped before anything squares it up.
    static void ForgetStalePairs(string store, List<(string Mine, string Theirs)> live)
    {
        var kept = live.Select(p => PairKey(p.Mine, p.Theirs)).ToHashSet();
        var stale = MirrorPairsBorrowedBy(store).Where(k => !kept.Contains(k)).ToHashSet();
        if (stale.Count == 0) return;
        Diagnostics.Note("mirror.stale", $"{store} dropped={stale.Count}");
        DropPairs(stale);
    }

    /// Going back is a handover, not a delete: one last pass carries what
    /// the profile did over to the source, then a copy goes only where the
    /// other side holds the same bytes, and never one the stash says the
    /// profile brought itself.
    public static int UnmirrorChatStores(string profile)
    {
        var keys = MirrorPairsBorrowedBy(profile);
        if (keys.Count == 0) return 0;
        var removed = 0;
        foreach (var key in keys)
        {
            if (PairHalves(key) is not { } pair) continue;
            var (mine, theirs) = pair;
            MirrorChatFolders(mine, theirs);
            var store = Path.GetDirectoryName(Path.GetDirectoryName(mine)!)!;
            var own = StashedCounterpart(mine, store);
            foreach (var name in Fs.Names(mine).Where(IsMirrored))
            {
                if (own != null && Fs.Exists(Join(own, name))) continue;
                var here = Fs.ReadShared(Join(mine, name));
                var there = Fs.ReadShared(Join(theirs, name));
                if (here == null || there == null || !here.AsSpan().SequenceEqual(there)) continue;
                try { File.Delete(Join(mine, name)); removed++; } catch { }
            }
        }
        DropPairs(keys.ToHashSet());
        Diagnostics.Note("unmirror", $"{Path.GetFileName(profile)} pairs={keys.Count} copiesRemoved={removed}");
        return removed;
    }

    public static void Ungraft(string profile)
    {
        var borrowing = MirrorPairsBorrowedBy(profile).Count > 0;
        var anyStash = SharedItems.Append(DesktopServers.FileName).Any(i => Fs.Exists(StashPath(Join(profile, i))) || Fs.IsLink(Join(profile, i)))
                       || ChatStores.Any(s => Fs.Exists(Join(profile, s)) && (Fs.IsLink(Join(profile, s)) || HasStashInside(Join(profile, s))));
        if (!borrowing && !anyStash) return;

        Diagnostics.Note("ungraft.begin", Path.GetFileName(profile));
        UnmirrorChatStores(profile);
        foreach (var item in SharedItems) Unstash(Join(profile, item));
        Unstash(Join(profile, DesktopServers.FileName));
        foreach (var store in ChatStores)
        {
            var dst = Join(profile, store);
            if (Fs.IsLink(dst)) { Unstash(dst); continue; }
            foreach (var account in Fs.Names(dst).Where(n => !n.EndsWith(StashSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                var accountDir = Join(dst, account);
                if (Fs.IsLink(accountDir)) { Unstash(accountDir); continue; }
                if (!Fs.IsDirectory(accountDir)) continue;
                foreach (var org in Fs.Names(accountDir).Where(n => !n.EndsWith(StashSuffix, StringComparison.OrdinalIgnoreCase)))
                    Unstash(Join(accountDir, org));
            }
            // One account stashes the whole store; it comes back whether a
            // link or a mirrored folder stands in its place.
            Unstash(dst);
            Unstash(Join(dst, "skills-plugin"));
        }
        Diagnostics.Note("ungraft.end", Path.GetFileName(profile));
    }

    static bool HasStashInside(string store)
    {
        if (Fs.Exists(StashPath(store))) return true;
        foreach (var account in Fs.Names(store))
        {
            if (account.EndsWith(StashSuffix, StringComparison.OrdinalIgnoreCase)) return true;
            var dir = Join(store, account);
            if (Fs.IsLink(dir)) return true;
            if (Fs.IsDirectory(dir) && Fs.Names(dir).Any(n => n.EndsWith(StashSuffix, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    // ======================================================================
    // Which profile a Claude reads, and opening it
    // ======================================================================

    public const string MainSource = "main";

    /// The folder a shortcut borrows chats from, or null for its own.
    public static string? SourceDir(Shortcut s, Store store)
    {
        if (string.IsNullOrEmpty(s.Source)) return null;
        if (s.Source.Equals(MainSource, StringComparison.OrdinalIgnoreCase)) return Paths.MainProfile;
        var other = store.Find(s.Source);
        return other == null || other == s ? null : other.ProfileDir;
    }

    /// Brings a closed profile's storage in line with what its entry says.
    public static void Apply(Shortcut s, Store store)
    {
        if (!Paths.IsOwnProfile(s.ProfileDir)) return;
        Directory.CreateDirectory(s.ProfileDir);
        try
        {
            if (SourceDir(s, store) is { } source && Directory.Exists(source)) GraftFrom(source, s.ProfileDir);
            else Ungraft(s.ProfileDir);
        }
        catch (Exception e) { Diagnostics.Note("apply.failed", $"{s.Folder} {e.Message}"); }
    }

    /// The Claudes reading the same chats as this one: the one it borrows
    /// from, those borrowing from it, and those borrowing from the same one.
    /// `null` is the main profile.
    public static List<(string Name, string? Profile)> ChatNeighbours(Shortcut? of, Store store)
    {
        string Key(Shortcut? s) => s == null ? MainSource : s.Folder;
        string? SourceKey(Shortcut? s) => s == null || string.IsNullOrEmpty(s.Source) || SourceDir(s, store) == null ? null
            : s.Source.Equals(MainSource, StringComparison.OrdinalIgnoreCase) ? MainSource : store.Find(s.Source)!.Folder;

        var all = new List<Shortcut?> { null };
        all.AddRange(store.Data.Shortcuts);
        var mine = Key(of);
        var mySource = SourceKey(of);
        return all.Where(x => !Key(x).Equals(mine, StringComparison.OrdinalIgnoreCase))
            .Where(x =>
            {
                var theirs = SourceKey(x);
                return (mySource != null && Key(x).Equals(mySource, StringComparison.OrdinalIgnoreCase)) ||
                       (theirs != null && theirs.Equals(mine, StringComparison.OrdinalIgnoreCase)) ||
                       (mySource != null && theirs != null && theirs.Equals(mySource, StringComparison.OrdinalIgnoreCase));
            })
            .Select(x => (x?.Name ?? S.T("Claude (main)"), x?.ProfileDir))
            .ToList();
    }

    // ======================================================================
    // Chats an account switch left in another profile
    // ======================================================================

    public sealed record Chat(string Title, DateTime LastActive);
    public sealed record ChatsElsewhereInfo(string Profile, string Account, int Count, bool Merging, List<Chat> Chats);

    /// Signing out of one account and into another leaves the first one's
    /// history where it was written. This names the profile holding chats of
    /// this profile's account that this profile has not got.
    public static ChatsElsewhereInfo? ChatsElsewhere(string profile, IEnumerable<string> others)
    {
        if (ReadableConfig(profile)?["lastKnownAccountUuid"] is not JsonValue v || !v.TryGetValue<string>(out var account)) return null;
        foreach (var store in ChatStores)
        {
            var dir = Join(profile, store);
            if (Fs.IsLink(dir) || IsStashedAway(dir)) return null;
        }
        if (MirrorPairsBorrowedBy(profile).Count > 0) return null;
        var own = RecordFiles(profile, account);
        var here = own.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ChatsElsewhereInfo? best = null;
        foreach (var other in others.Where(o => !Fs.SameReal(o, profile)))
        {
            var missing = RecordFiles(other, account).Where(r => !here.Contains(r.Key)).ToList();
            if (missing.Count == 0 || missing.Count <= (best?.Count ?? 0)) continue;
            best = new(other, account, missing.Count, own.Count > 0, NewestChats(missing.Select(m => m.Path)));
        }
        return best;
    }

    static List<(string Key, string Path)> RecordFiles(string profile, string account)
    {
        var found = new List<(string, string)>();
        foreach (var store in ChatStores)
        {
            var dir = Join(profile, store);
            if (CounterpartDirectory(dir, account) is not { } accountDir) continue;
            var under = Join(dir, accountDir);
            foreach (var org in Fs.Names(under).Where(n => !Hidden(n)))
            {
                var orgDir = Join(under, org);
                if (!Fs.IsDirectory(orgDir)) continue;
                foreach (var name in Fs.Names(orgDir).Where(n => n.StartsWith("local_", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                    found.Add((store + "/" + name, Join(orgDir, name)));
            }
        }
        return found;
    }

    static List<Chat> NewestChats(IEnumerable<string> files, int limit = 5) =>
        files.Select(f => (Path: f, When: Fs.Modified(f)))
            .OrderByDescending(e => e.When)
            .Take(limit * 4)
            .Select(e =>
            {
                try
                {
                    if (Fs.ReadShared(e.Path) is not { } data || JsonNode.Parse(data) is not JsonObject record) return null;
                    var title = record["title"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(title)) return null;
                    var active = record["lastActivityAt"] is JsonValue a && a.TryGetValue<double>(out var ms)
                        ? DateTime.UnixEpoch.AddMilliseconds(ms) : e.When;
                    return new Chat(title, active);
                }
                catch { return null; }
            })
            .OfType<Chat>()
            .OrderByDescending(c => c.LastActive)
            .Take(limit)
            .ToList();

    public sealed record Adoption(int Copied, List<string> Running);

    /// Tests replace this: nothing in a temporary folder can be made to run.
    public static Func<IEnumerable<string>, List<string>> RunningAmong { get; set; } =
        profiles => profiles.Where(p => ClaudeProcesses.IsRunning(Paths.Same(p, Paths.MainProfile) ? null : p)).ToList();

    /// Copied, never moved, and nothing moves while any Claude is up: an
    /// instance builds its sidebar as it starts and rewrites records as it
    /// runs, and any of them may be sharing these folders.
    public static Adoption AdoptChats(string source, string profile, string account, IEnumerable<string> allProfiles)
    {
        if (Fs.SameReal(source, profile)) return new(0, []);
        var running = RunningAmong(allProfiles);
        if (running.Count > 0)
        {
            Diagnostics.Note("adopt.blocked", $"{Path.GetFileName(profile)} running={running.Count}");
            return new(0, running);
        }
        int copied = 0, markers = 0;
        foreach (var store in ChatStores)
        {
            var src = Join(source, store);
            var dst = Join(profile, store);
            if (CounterpartDirectory(src, account) is not { } sourceAccountDir) continue;
            var from = Join(src, sourceAccountDir);
            var into = Join(dst, CounterpartDirectory(dst, account) ?? account);
            if (IsStashedAway(into) || !Fs.ResolvesInside(into, profile))
            {
                Diagnostics.Note("adopt.skipped", $"{profile} {store} (this profile's folder for the account is not its own)");
                continue;
            }
            foreach (var org in Fs.Names(from).Where(n => !Hidden(n)))
            {
                var orgFrom = Join(from, org);
                if (!Fs.IsDirectory(orgFrom)) continue;
                var orgInto = Join(into, CounterpartDirectory(into, org) ?? org);
                Directory.CreateDirectory(orgInto);
                foreach (var name in Fs.Names(orgFrom).Where(IsMirrored))
                {
                    var to = Join(orgInto, name);
                    if (Fs.Exists(to) || !Fs.Copy(Join(orgFrom, name), to)) continue;
                    if (name.StartsWith("local_", StringComparison.Ordinal)) copied++; else markers++;
                }
            }
        }
        Diagnostics.Note("adopt", $"{Path.GetFileName(source)} -> {Path.GetFileName(profile)} copied={copied} markers={markers}");
        return new(copied, []);
    }
}
