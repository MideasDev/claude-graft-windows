using System.Text;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;

/// Chat sharing, end to end on stand-in profiles inside the suite's sandbox.
/// Each case is one way the macOS original once lost chats on a real
/// machine, and the check is that it does not happen here.
static class GraftTests
{
    const string AccountA = "aaaaaaaa-1111-4111-8111-111111111111";
    const string AccountB = "bbbbbbbb-2222-4222-8222-222222222222";
    const string Org = "cccccccc-3333-4333-8333-333333333333";
    const string OrgB = "dddddddd-4444-4444-8444-444444444444";
    const string Store = "claude-code-sessions";

    static string Profile(string root, string name, string? account)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        if (account != null)
            File.WriteAllText(Path.Combine(dir, "config.json"),
                new JsonObject { ["lastKnownAccountUuid"] = account, ["userThemeMode"] = name == "main" ? "dark" : "light" }.ToJsonString());
        return dir;
    }

    static string OrgDir(string profile, string account, string org = Org)
    {
        var dir = Path.Combine(profile, Store, account, org);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void Record(string orgDir, string id, string title, double at = 1)
    {
        File.WriteAllText(Path.Combine(orgDir, $"local_{id}.json"),
            new JsonObject { ["title"] = title, ["lastActivityAt"] = at }.ToJsonString());
    }

    static HashSet<string> Records(string orgDir) =>
        Directory.Exists(orgDir)
            ? Directory.GetFiles(orgDir, "local_*.json").Select(f => Path.GetFileName(f)).ToHashSet()
            : [];

    public static void Run(Action<bool, string> check, string sandbox)
    {
        // ---- The rule, with no filesystem in it ----
        check(Graft.MirrorDecision("a", "a", null) == Graft.MirrorAction.Nothing, "two equal copies need nothing");
        check(Graft.MirrorDecision("a", null, null) == Graft.MirrorAction.CopyToOther, "a record new on one side is copied across");
        check(Graft.MirrorDecision("a", null, "a") == Graft.MirrorAction.RemoveFromOne, "a record deleted on one side is deleted on the other");
        check(Graft.MirrorDecision(null, "b", "b") == Graft.MirrorAction.RemoveFromOther, "and the same the other way round");
        check(Graft.MirrorDecision("a2", "a", "a") == Graft.MirrorAction.CopyToOther, "a record edited on one side carries its edit across");
        check(Graft.MirrorDecision("a2", "a3", "a") == Graft.MirrorAction.Conflict, "edits on both sides are a conflict to settle");
        check(Graft.MirrorDecision("a2", null, "a") == Graft.MirrorAction.Conflict, "an edit against a deletion is a conflict, never a deletion");
        check(Graft.IsMirrored("local_x.json") && Graft.IsMirrored("deleted_x") && !Graft.IsMirrored("scheduled-tasks.json"),
              "only records and deletion markers are mirrored");

        var root = Path.Combine(sandbox, "graft");

        // ---- One account, two profiles ----
        var main = Profile(root, "main", AccountA);
        var borrower = Profile(root, "borrower", AccountA);
        var mainOrg = OrgDir(main, AccountA);
        Record(mainOrg, "m1", "Main chat one");
        Record(mainOrg, "m2", "Main chat two");
        File.WriteAllText(Path.Combine(mainOrg, "scheduled-tasks.json"), "{\"main\":true}");
        var ownOrg = OrgDir(borrower, AccountA);
        Record(ownOrg, "b1", "Borrower's own chat");
        Directory.CreateDirectory(Path.Combine(main, "Claude Extensions", "one"));
        Directory.CreateDirectory(Path.Combine(borrower, "Claude Extensions", "own-extension"));

        Graft.GraftFrom(main, borrower);
        var mirrored = Path.Combine(borrower, Store, AccountA, Org);
        check(Records(mirrored).SetEquals(["local_m1.json", "local_m2.json", "local_b1.json"]),
              "a borrowing profile reads the source's chats and its own together");
        check(Records(mainOrg).Contains("local_b1.json"), "the borrower's own chats are merged into the source as well");
        check(!File.Exists(Path.Combine(mirrored, "scheduled-tasks.json")), "files that are not records stay with the profile that wrote them");
        check(!Fs.IsLink(Path.Combine(borrower, Store)) && !Fs.IsLink(mirrored),
              "a chat store is a real folder, never a link, so Claude files records into it");
        var stash = Path.Combine(borrower, "." + Store + Graft.StashSuffix);
        check(File.Exists(Path.Combine(stash, AccountA, Org, "local_b1.json")),
              "what the profile owned before the graft is kept in its stash");
        check(Graft.MirrorPairsBorrowedBy(borrower).Count == 1, "the pair is written down, borrower first");
        check(Graft.MirrorPairsBorrowedBy(main).Count == 0, "the source lends through the pair but borrows nothing");
        var extensions = Path.Combine(borrower, "Claude Extensions");
        check(Fs.IsLink(extensions) && Directory.Exists(Path.Combine(extensions, "one")), "shareable settings are linked to the source's");
        check(Directory.Exists(Path.Combine(borrower, ".Claude Extensions" + Graft.StashSuffix, "own-extension")),
              "the profile's own settings are stashed, not destroyed, when they are linked");
        check(Graft.ReadableConfig(borrower)?["userThemeMode"]?.GetValue<string>() == "dark", "the source's theme is copied");
        check(Graft.Account(borrower) == AccountA, "copying the theme leaves the profile's own login keys alone");

        // A second opening must not read as a first: it would stash the merge.
        Graft.GraftFrom(main, borrower);
        check(Records(mirrored).Count == 3, "a second pass keeps the merged chats where they are");
        check(Records(Path.Combine(stash, AccountA, Org)).SetEquals(["local_b1.json"]),
              "a second pass leaves the stash naming exactly what the profile brought");

        // Changes travel both ways.
        Record(mainOrg, "m3", "Started in main");
        File.Delete(Path.Combine(mirrored, "local_m1.json"));
        File.WriteAllText(Path.Combine(mirrored, "deleted_m1"), "");
        Record(mirrored, "m2", "Renamed in the borrower", at: 5);
        Graft.MirrorKnownPairs();
        check(Records(mirrored).Contains("local_m3.json"), "a chat started in the source reaches the borrower");
        check(!File.Exists(Path.Combine(mainOrg, "local_m1.json")), "a chat deleted in the borrower is deleted in the source");
        check(File.Exists(Path.Combine(mainOrg, "deleted_m1")), "and its deletion marker goes with it");
        check(File.ReadAllText(Path.Combine(mainOrg, "local_m2.json")).Contains("Renamed in the borrower"),
              "a chat renamed in the borrower is renamed in the source");

        // Both edited: the newer record wins.
        Record(mainOrg, "m3", "Edited in main later", at: 900);
        Record(mirrored, "m3", "Edited in borrower earlier", at: 100);
        Graft.MirrorKnownPairs();
        check(File.ReadAllText(Path.Combine(mirrored, "local_m3.json")).Contains("main later"),
              "when both sides edited a chat the one that moved last wins");

        // A record Windows will not let anyone read right now is skipped,
        // never taken for deleted.
        using (new FileStream(Path.Combine(mirrored, "local_m3.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Graft.MirrorKnownPairs();
        check(File.Exists(Path.Combine(mainOrg, "local_m3.json")), "a chat locked mid-write is not carried across as a deletion");

        // A side emptied wholesale is not a sidebar cleared one chat at a time.
        var before = Records(mainOrg).Count;
        foreach (var f in Directory.GetFiles(mirrored).Where(f => Graft.IsMirrored(Path.GetFileName(f)))) File.Delete(f);
        Graft.MirrorKnownPairs();
        check(Records(mainOrg).Count == before, "a folder that lost every chat at once never empties the other side");
        check(Records(mirrored).Count == before, "it is filled again from the side that kept them");

        // Opening the lender must not undo anybody's graft.
        Graft.Ungraft(main);
        check(Records(mainOrg).Count == before && Graft.MirrorPairsBorrowedBy(borrower).Count == 1,
              "the profile others borrow from going back to its own chats touches nobody's pair");

        // Going back.
        Record(mirrored, "late", "Written while borrowing");
        Graft.Ungraft(borrower);
        var back = Path.Combine(borrower, Store, AccountA, Org);
        check(Records(back).SetEquals(["local_b1.json"]), "going back returns the profile to exactly its own chats");
        check(Records(mainOrg).Contains("local_late.json"), "a chat written while borrowing is handed over to the source first");
        check(Records(mainOrg).Contains("local_b1.json"), "what was merged into the source stays there");
        check(!Directory.Exists(stash), "the stash is spent on the way back");
        check(!Fs.IsLink(extensions) && Directory.Exists(Path.Combine(extensions, "own-extension")),
              "linked settings are put back as the profile had them");
        check(Directory.Exists(Path.Combine(main, "Claude Extensions", "one")), "and the source's settings are untouched");
        check(Graft.MirrorPairsBorrowedBy(borrower).Count == 0, "going back drops the pair");

        // ---- Guards ----
        var unreadable = Profile(root, "unreadable", null);
        File.WriteAllText(Path.Combine(unreadable, "config.json"), "{ half-writ");
        var unreadableOrg = OrgDir(unreadable, AccountA);
        Record(unreadableOrg, "u1", "Its own");
        Graft.MirrorChatStores(main, unreadable);
        check(Records(unreadableOrg).SetEquals(["local_u1.json"]) && Graft.MirrorPairsBorrowedBy(unreadable).Count == 0,
              "a profile whose config.json cannot be read is left exactly as it was");

        var emptySource = Profile(root, "empty-source", AccountA);
        Directory.CreateDirectory(Path.Combine(emptySource, Store, "skills-plugin"));
        var keeper = Profile(root, "keeper", AccountA);
        var keeperOrg = OrgDir(keeper, AccountA);
        Record(keeperOrg, "k1", "Keeper's chat");
        Graft.MirrorChatStores(emptySource, keeper);
        check(Records(keeperOrg).Count == 1 && !Directory.Exists(Path.Combine(keeper, "." + Store + Graft.StashSuffix)),
              "a source with nothing under this account puts nothing of the borrower's away");

        Graft.GraftFrom(main, main);
        check(!Directory.Exists(Path.Combine(main, "." + Store + Graft.StashSuffix)), "a profile is never grafted from itself");

        // ---- Two accounts ----
        var other = Profile(root, "other-account", AccountB);
        var otherOrg = OrgDir(other, AccountB, OrgB);
        Record(otherOrg, "o1", "Other account's chat");
        Graft.GraftFrom(main, other);
        check(Records(otherOrg).Contains("local_m2.json") && Records(otherOrg).Contains("local_o1.json"),
              "across accounts the profile's own organization folder holds both histories");
        check(Records(mainOrg).Contains("local_o1.json"), "and the source's holds them too");
        check(File.Exists(Path.Combine(other, Store, AccountB, "." + OrgB + Graft.StashSuffix, "local_o1.json")),
              "across accounts the organization folder itself is what is stashed");
        Graft.Ungraft(other);
        check(Records(Path.Combine(other, Store, AccountB, OrgB)).SetEquals(["local_o1.json"]),
              "going back across accounts returns exactly the profile's own chats");

        // ---- Two organizations under one account (seen on a real machine) ----
        // The account's chats sit in one organization; the other is empty but
        // was touched last. The borrowed history must land where the chats are.
        var twoOrgs = Profile(root, "two-orgs", AccountB);
        var inUse = OrgDir(twoOrgs, AccountB, "11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        Record(inUse, "own1", "Chat of its own");
        File.SetLastWriteTimeUtc(Path.Combine(inUse, "local_own1.json"), DateTime.UtcNow.AddHours(-3));
        Directory.SetLastWriteTimeUtc(inUse, DateTime.UtcNow.AddHours(-3));
        var idle = OrgDir(twoOrgs, AccountB, "22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        File.WriteAllText(Path.Combine(idle, "scheduled-tasks.json"), "{}");
        check(Graft.NewestChild(Path.Combine(twoOrgs, Store, AccountB)) == Path.GetFileName(idle),
              "the newest folder is the empty organization, which is how the wrong one was picked");
        Graft.GraftFrom(main, twoOrgs);
        check(Records(inUse).Contains("local_m2.json") && Records(inUse).Contains("local_own1.json"),
              "a borrowed history lands in the organization holding the profile's own chats");
        check(Records(idle).Count == 0, "and nothing lands in an empty organization it is not using");

        // Copies of the source's chats in the idle folder, newer than anything
        // of the profile's own, never make it look like the one in use: once
        // merged, the profile's own chats are copies too, and the pair decides.
        foreach (var f in Directory.GetFiles(mainOrg, "local_*.json")) File.Copy(f, Path.Combine(idle, Path.GetFileName(f)), true);
        check(Graft.ActiveOrganization(Path.Combine(twoOrgs, Store, AccountB), mainOrg) == Path.GetFileName(inUse),
              "copies of the source's chats never make an organization look like the one in use");
        foreach (var f in Directory.GetFiles(idle, "local_*.json")) File.Delete(f);
        Graft.GraftFrom(main, twoOrgs);
        check(Records(idle).Count == 0 && Records(inUse).Contains("local_m2.json"), "and a later pass stays where it was");

        // The real machine's state: copies already sitting in the wrong folder
        // from before this rule, while the profile's own chats were never merged.
        var wrong = Profile(root, "wrong-pick", AccountB);
        var wrongInUse = OrgDir(wrong, AccountB, "11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        Record(wrongInUse, "mine", "Never merged");
        var wrongIdle = OrgDir(wrong, AccountB, "22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        foreach (var f in Directory.GetFiles(mainOrg, "local_*.json")) File.Copy(f, Path.Combine(wrongIdle, Path.GetFileName(f)), true);
        check(Graft.ActiveOrganization(Path.Combine(wrong, Store, AccountB), mainOrg) == Path.GetFileName(wrongInUse),
              "a folder full of copies left by a wrong pick loses to the one holding a chat of the profile's own");

        Graft.Ungraft(twoOrgs);
        check(Records(inUse).SetEquals(["local_own1.json"]), "going back from it returns exactly its own chats");

        // ---- File links (Developer Mode lets them be made) ----
        var linkTarget = Path.Combine(root, "link-target.json");
        File.WriteAllText(linkTarget, "{\"keep\":true}");
        var fileLink = Path.Combine(root, "file-link.json");
        if (Fs.CreateLink(fileLink, linkTarget))
        {
            check(Fs.IsLink(fileLink) && Paths.Same(Fs.LinkTarget(fileLink), linkTarget), "a file link is seen as a link to its file");
            check(Fs.RemoveLink(fileLink) && !File.Exists(fileLink) && File.ReadAllText(linkTarget) == "{\"keep\":true}",
                  "removing a file link takes the link and leaves the file it pointed at");
        }
        else Console.WriteLine("(file links are not allowed on this machine: that check is skipped)");
        var folderLink = Path.Combine(root, "folder-link");
        if (Fs.CreateLink(folderLink, mainOrg))
            check(Fs.RemoveLink(folderLink) && !Directory.Exists(folderLink) && Records(mainOrg).Count > 0,
                  "removing a junction takes the junction and leaves the folder it pointed at");

        // ---- Spellings ----
        var shortened = Path.Combine(root, "spelled");
        Directory.CreateDirectory(Path.Combine(shortened, "aaaaaaaa"));
        check(Graft.CounterpartDirectory(shortened, AccountA) == "aaaaaaaa", "an account spelled by its first eight characters is found");
        Directory.CreateDirectory(Path.Combine(shortened, "aaaaaaaa-1"));
        check(Graft.CounterpartDirectory(shortened, AccountA) == null, "two folders that could both be the account mean neither is");
        File.WriteAllText(Path.Combine(mainOrg, "..", "zzzz.profile-origin.json"), "{}");
        check(Graft.NewestChild(Path.Combine(main, Store, AccountA)) == Org, "a plain file beside the organizations is never one");

        // ---- Chats an account switch left behind ----
        var switched = Profile(root, "switched", AccountA);
        Directory.CreateDirectory(Path.Combine(switched, Store));
        var found = Graft.ChatsElsewhere(switched, [main, other]);
        check(found != null && Paths.Same(found.Profile, main) && !found.Merging && found.Count == Records(mainOrg).Count,
              "chats of this account left in another profile are found and counted");
        check(found!.Chats.Count > 0 && found.Chats.Count <= 5, "the newest few are named by their titles");
        Graft.RunningAmong = _ => [main];
        var blocked = Graft.AdoptChats(main, switched, AccountA, [main, switched]);
        check(blocked.Copied == 0 && blocked.Running.Count == 1, "nothing is copied while any Claude is open");
        Graft.RunningAmong = _ => [];
        var adopted = Graft.AdoptChats(main, switched, AccountA, [main, switched]);
        check(adopted.Copied == Records(mainOrg).Count, "they are copied in once every Claude is closed");
        check(Records(mainOrg).Count == adopted.Copied, "copied, never moved: the other profile keeps them");
        check(Graft.AdoptChats(main, switched, AccountA, [main, switched]).Copied == 0, "a second copy brings nothing twice");
        check(Graft.ChatsElsewhere(switched, [main]) == null, "once taken, the offer goes");
    }

    /// The opening flow: whose chats a Claude reads comes from the list, and
    /// another Claude open on the same chats is asked about first.
    public static void RunOpening(Action<bool, string> check, Store store, List<ClaudeProcess> processes,
                                  List<(string Exe, string Args)> launches, string exe)
    {
        var lender = store.Add("Lender", "Lender");
        var borrower = store.Add("Borrower", "Borrower");
        borrower.Source = lender.Folder;
        var third = store.Add("Third", "Third");
        third.Source = lender.Folder;
        var loner = store.Add("Loner", "Loner");

        check(Graft.SourceDir(borrower, store) == lender.ProfileDir, "a Claude borrowing from another reads that one's folder");
        check(Graft.SourceDir(loner, store) == null, "a new Claude reads its own chats");
        borrower.Source = "Gone";
        check(Graft.SourceDir(borrower, store) == null, "a source that no longer exists is no source at all");
        borrower.Source = lender.Folder;

        var neighbours = Graft.ChatNeighbours(borrower, store).Select(n => n.Name).ToHashSet();
        check(neighbours.SetEquals(["Lender", "Third"]), "a borrower shares chats with its lender and the lender's other borrowers");
        check(Graft.ChatNeighbours(loner, store).Count == 0, "a Claude on its own chats shares them with nobody");
        check(Graft.ChatNeighbours(lender, store).Select(n => n.Name).ToHashSet().SetEquals(["Borrower", "Third"]),
              "a lender shares chats with everyone borrowing from it");

        processes.Add(new(900, $"{exe} --user-data-dir=\"{lender.ProfileDir}\""));
        var asked = new List<IReadOnlyList<string>>();
        Opener.ConfirmSharers = sharers => { asked.Add(sharers); return false; };
        launches.Clear();
        var refused = Opener.Open(borrower, store);
        check(refused.Outcome == OpenOutcome.Cancelled && launches.Count == 0,
              "declining the warning opens nothing");
        check(asked.Count == 1 && asked[0].SequenceEqual(["Lender"]), "the warning names the Claude open on the same chats");
        Opener.ConfirmSharers = _ => true;
        check(Opener.Open(borrower, store).Outcome == OpenOutcome.Launched, "accepting it opens the Claude");
        asked.Clear();
        Opener.ConfirmSharers = sharers => { asked.Add(sharers); return true; };
        Opener.Open(loner, store);
        check(asked.Count == 0, "a Claude sharing nothing is opened without a question");
        processes.RemoveAll(p => p.Pid == 900);
        Opener.ConfirmSharers = _ => true;
        foreach (var s in new[] { lender, borrower, third, loner }) store.Data.Shortcuts.Remove(s);
    }
}
