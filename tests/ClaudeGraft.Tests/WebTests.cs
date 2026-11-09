using ClaudeGraft.Core.Web;

/// claude.ai's requests and answers, as the original read them off the page.
static class WebTests
{
    public static void Run(Action<bool, string> check)
    {
        var account = Guid.NewGuid();

        check(ArtifactApi.HeadersFor("/api/frame/artifacts?x=1").GetValueOrDefault("X-Frame-CP") == "go",
              "every artifact request carries the header without which the service answers 404");
        check(ArtifactApi.HeadersFor("/api/organizations/o/projects_v2").Count == 0,
              "the rest of claude.ai is asked without the artifact service's headers");
        check(ArtifactApi.ListPath("mine", "org-1", "tok") ==
              "/api/frame/artifacts?rel=mine&empty=exclude&limit=48&org=org-1&sort=last_viewed&page_token=tok",
              "the list is asked exactly as claude.ai's page asks it");

        var identity = ArtifactApi.ParseIdentity("""
            {"uuid":"acc","email_address":"a@b.c","memberships":[
              {"organization":{"uuid":"team","capabilities":["claude_pro"]}},
              {"organization":{"uuid":"personal","capabilities":["chat","claude_pro"]}}]}
            """);
        check(identity == new ArtifactApi.Identity("acc", "a@b.c", "personal"),
              "artifacts live under the organization that can chat, not the first one listed");
        check(ArtifactApi.ParseIdentity("""{"error":"nope"}""") == null, "an answer that is not an account names no account");

        var page = ArtifactApi.ParsePage("""
            {"artifacts":[
              {"id":"a1","title":"Plan","kind":"page","open_url":"https://claude.ai/a/x","audience":"public","rel":"mine",
               "sort_time":"2026-11-08T10:00:00Z","renamable":true,"duplicable":true,"duplicate_available":false},
              {"id":"a2","title":"","audience":"users","rel":"shared","updated_at":"2026-11-09T10:00:00Z"}],
             "next_page_token":"next"}
            """, account, "org");
        check(page?.Artifacts.Count == 2 && page.Value.Next == "next", "a page of artifacts is read with its next token");
        var first = page!.Value.Artifacts[0];
        check(first.IsOwn && first.IsPublic && first.Renamable && first.Duplicable && !first.DuplicateAvailable,
              "what claude.ai allows for an artifact is read from its row");
        check(page.Value.Artifacts[1].Title == "Untitled" && !page.Value.Artifacts[1].IsOwn, "an artifact with no title still has a name");

        var shared = page.Value.Artifacts[0] with { Rel = "shared" };
        var merged = ArtifactApi.Merge([[shared], page.Value.Artifacts]);
        check(merged.Count == 2 && merged.Single(a => a.Id == "a1").IsOwn, "an artifact listed twice is kept once, as the account's own");
        check(merged[0].Id == "a2", "and the list is newest first");

        var perms = ArtifactApi.ParsePermissions("""{"version":7,"read":{"mode":"users","users":["x@y.z"]},"commentersEnabled":true}""");
        check(perms == null ? false : perms.Version == "7" && perms.Invited.SequenceEqual(["x@y.z"]) && perms.Commenters && !perms.Editors,
              "permissions say who is invited and which roles the artifact takes");
        check(ArtifactApi.ParsePermissions("""{"writersEnabled":true}""")!.Editors, "an older answer's writers flag counts as editors");
        check(Roles.Offered(ArtifactRole.Editor, perms!) == ArtifactRole.Commenter, "an editor becomes a commenter where only comments are on");
        check(Roles.Offered(ArtifactRole.Commenter, perms! with { Commenters = false }) == ArtifactRole.Reader,
              "and a viewer where neither is: a role is lowered, never raised");
        check(ArtifactApi.ReadModeBody(false, []) == """{"read":{"mode":"owner"}}""", "closing an artifact nobody was invited to makes it the owner's alone");
        check(ArtifactApi.ReadModeBody(false, ["x@y.z"]).Contains("\"users\":[\"x@y.z\"]"), "closing it keeps those already invited");
        check(ArtifactApi.ReadModeBody(true, ["x@y.z"]) == """{"read":{"mode":"public"}}""", "opening it makes it public");
        check(ArtifactApi.InviteBody("x@y.z", ArtifactRole.Commenter) == """{"email":"x@y.z","role":"commenter"}""", "an invitation names its role as claude.ai does");
        check(ArtifactApi.Reason("""{"error":{"reason":"title_invalid"}}""") == "title_invalid", "a refusal's reason is read");

        var source = ArtifactApi.ParseSource("""{"ver":"3","assetToken":"tok","files":[{"path":"app.js"},{"path":"../x"},{"path":"index.html"}]}""");
        check(source?.Files.SequenceEqual(["index.html", "app.js"]) == true, "an artifact's files are read, never one that climbs out of its folder");
        check(!ArtifactApi.IsSafeFilePath("a/../b") && !ArtifactApi.IsSafeFilePath("C:x") && ArtifactApi.IsSafeFilePath("img/a.png"),
              "only paths that stay inside the folder are safe");
        check(ArtifactApi.FileUrl("AB-12", source!, "img/a b.png") == "https://ab-12.frame.claudeusercontent.com/_f/3/_src/img/a%20b.png?__frame_t=tok",
              "a file is read from the file host with its token in the address");

        // ---- Groups ----
        var parsed = ArtifactGroup.ParseAddresses("Ana <ana@x.com>, bob@y.org\nana@x.com; not-an-address");
        check(parsed.SequenceEqual(["ana@x.com", "bob@y.org"]), "addresses are read out of a mail header, each once");
        var group = new ArtifactGroup { Name = "Team" };
        group.Add(parsed);
        check(group.Roles == null && group.CommonRole == ArtifactRole.Reader, "a group of viewers keeps no roles at all");
        group.SetRoleForEveryone(ArtifactRole.Editor);
        group.Add(["cy@z.net"]);
        check(group.RoleOf("cy@z.net") == ArtifactRole.Editor, "someone added to a group of editors joins as an editor");
        group.SetRole("ana@x.com", ArtifactRole.Reader);
        check(group.CommonRole == null && !group.Roles!.ContainsKey("ana@x.com"), "a viewer in a mixed group is simply absent from its roles");

        // ---- Projects and search ----
        var projects = ProjectsApi.ParseProjects("""
            {"data":[{"uuid":"p1","name":"Web","description":"site","is_private":false,"is_starred":true,"updated_at":"2026-11-01T00:00:00Z","creator":{"full_name":"Alex"}},
                     {"uuid":"p2","name":"Old","archived_at":"2026-10-01T00:00:00Z"}],
             "pagination":{"has_more":true}}
            """, account);
        check(projects?.Projects.Count == 1 && projects.Value.HasMore, "an archived project is left off, as claude.ai's page leaves it off");
        check(projects!.Value.Projects[0] is { IsStarred: true, IsPrivate: false, Creator: "Alex" }, "a project's details are read");
        check(ProjectsApi.SearchBody("hola") == """{"n":25,"query":"hola","target_snippet_size":100}""", "a search is asked as the page asks it");
        var hits = ProjectsApi.ParseChats("""
            {"data":[{"conversation":{"uuid":"c1","name":"Hola mundo","is_archived":true,"project_uuid":"p1"},
                      "matched_snippet":{"text":"…hola…","matches":[{"start":1,"end":5}]},"semantic_rank":3}]}
            """, account);
        check(hits?.Single() is { IsArchived: true, Project: "p1", Rank: 3, OpenUrl: "https://claude.ai/chat/c1" },
              "a chat found is read with where it is and whether it is archived");
        check(hits!.Single().SnippetMatches.SequenceEqual([(1, 5)]), "its matched words are read for highlighting");
        var sessions = ProjectsApi.ParseCodeSessions("""
            {"data":[{"id":"cse_9","title":"Fix login","status":"archived","environment_kind":"anthropic_cloud",
                      "config":{"sources":[{"url":"https://github.com/o/r"}]}}],"next_cursor":"n2"}
            """);
        check(sessions?.Sessions.Single() is { IsArchived: true, InCloud: true, Repository: "https://github.com/o/r" } && sessions.Value.Next == "n2",
              "a Code session is read with its status, place and repository");
        check(ProjectsApi.CodeMatches("login fix", sessions!.Value.Sessions[0]) && !ProjectsApi.CodeMatches("logout", sessions.Value.Sessions[0]),
              "a Code session matches when every word is in its title or repository");
        check(ProjectsApi.CodeSessionUrl("cse_9") == "https://claude.ai/code/session_9", "a Code session is opened at its page");
        check(ProjectsApi.CodeHeaders("o").ContainsKey("anthropic-beta"), "the session list is asked with the headers without which it answers 400");
    }
}
