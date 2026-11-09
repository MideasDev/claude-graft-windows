using System.Text.Json.Nodes;
using ClaudeGraft.Core.Updates;

/// Claude's update feed, Claude Graft's releases, and the manual update mode
/// written into a stand-in configuration library.
static class UpdateTests
{
    public static void Run(Action<bool, string> check, string sandbox)
    {
        // ---- Claude's feed ----
        var feed = ClaudeUpdates.ParseFeed("""
            {"currentRelease":"4.24000.0","releases":[{"version":"4.24000.0","updateTo":{"version":"4.24000.0",
             "url":"https://downloads.claude.ai/releases/win32/x64/4.24000.0/Claude-x.msix","sha256":"ABC123","size":296041897,"notes":"n"}}]}
            """);
        check(feed is { Version: "4.24000.0", Sha256: "abc123", Size: 296041897 }, "Claude's feed is read for its version, package and checksum");
        check(ClaudeUpdates.ParseFeed("""{"currentRelease":"1.0.0","releases":[{"version":"1.0.0","updateTo":{"version":"1.0.0","url":"https://evil.example/c.msix","sha256":"a"}}]}""") == null,
              "a package anywhere but Claude's own download host is refused");
        check(ClaudeUpdates.IsNewer("4.24000.0", "4.23118.0") && !ClaudeUpdates.IsNewer("4.23118.0", "4.23118.0") && !ClaudeUpdates.IsNewer("4.9.0", "4.23118.0"),
              "versions compare as numbers, not as text");
        check(ClaudeUpdates.FeedUrl("4.23118.0").StartsWith("https://api.anthropic.com/api/desktop/win32/") &&
              ClaudeUpdates.FeedUrl("4.23118.0").Contains("/msix/update?device_id="),
              "the feed is asked as Claude asks it, under a device identifier of Claude Graft's own");

        // ---- Claude Graft's releases ----
        const string Base = "https://github.com/MideasDev/claude-graft-windows/releases/download/v0.2.0/";
        var release = SelfUpdate.ParseRelease($$"""
            {"tag_name":"v0.2.0","draft":false,"prerelease":false,"body":"notes","assets":[
              {"name":"ClaudeGraft-0.2.0-win-x64-selfcontained.zip","browser_download_url":"{{Base}}a.zip","digest":"sha256:{{new string('b', 64)}}"},
              {"name":"ClaudeGraft-0.2.0-win-x64.zip","browser_download_url":"{{Base}}ClaudeGraft-0.2.0-win-x64.zip","size":10,"digest":"sha256:{{new string('a', 64)}}"}]}
            """);
        check(release is { Version: "0.2.0", Size: 10 } && release.Sha256 == new string('a', 64) && release.ZipUrl.EndsWith("ClaudeGraft-0.2.0-win-x64.zip"),
              "a release's framework-dependent zip is chosen, with the checksum GitHub recorded");
        var fallback = SelfUpdate.ParseRelease($$"""
            {"tag_name":"v0.3.0","assets":[{"name":"ClaudeGraft-0.3.0-win-x64.zip","browser_download_url":"{{Base}}z.zip"},
                                           {"name":"ClaudeGraft-0.3.0-win-x64.zip.sha256","browser_download_url":"{{Base}}z.sha256"}]}
            """, _ => new string('c', 64) + "  ClaudeGraft-0.3.0-win-x64.zip");
        check(fallback?.Sha256 == new string('c', 64), "without GitHub's checksum, the .sha256 beside the zip is used");
        check(SelfUpdate.ParseRelease("""{"tag_name":"v0.3.0","assets":[{"name":"ClaudeGraft-0.3.0-win-x64.zip","browser_download_url":"https://github.com/other/repo/releases/download/v/x.zip","digest":"sha256:""" + new string('a', 64) + "\"}]}") == null,
              "a download from any other repository is refused");
        check(SelfUpdate.ParseRelease("""{"tag_name":"v0.3.0","assets":[{"name":"ClaudeGraft-0.3.0-win-x64.zip","browser_download_url":"https://github.com/MideasDev/claude-graft-windows/releases/download/v/x.zip"}]}""") == null,
              "a release with no checksum at all is never installed");
        check(SelfUpdate.ParseRelease("""{"tag_name":"v9.9.9","draft":true,"assets":[]}""") == null, "a draft is not a release");

        // ---- Manual update mode ----
        var thirdParty = Path.Combine(sandbox, "Claude-3p");
        ManualUpdates.FolderOverride = () => thirdParty;
        ManualUpdates.ManagedOverride = () => false;
        check(ManualUpdates.SetEnabled(true) == null && ManualUpdates.Enabled, "manual update mode turns on");
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(thirdParty, "configLibrary", "_meta.json")))!;
        var id = meta["appliedId"]!.GetValue<string>();
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(thirdParty, "configLibrary", id + ".json")))!;
        check(config["disableAutoUpdates"]?.GetValue<bool>() == true, "it sets Claude's own disableAutoUpdates policy");
        check(ManualUpdates.SetEnabled(false) == null && !Directory.Exists(thirdParty),
              "turning it off removes everything it made, and leaves no folder Claude did not have");

        Directory.CreateDirectory(Path.Combine(thirdParty, "configLibrary"));
        var existing = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(thirdParty, "configLibrary", "_meta.json"), new JsonObject { ["appliedId"] = existing, ["entries"] = new JsonArray() }.ToJsonString());
        File.WriteAllText(Path.Combine(thirdParty, "configLibrary", existing + ".json"),
            new JsonObject { ["disableAutoUpdates"] = false, ["inferenceProvider"] = "mine" }.ToJsonString());
        ManualUpdates.SetEnabled(true);
        var mixed = JsonNode.Parse(File.ReadAllText(Path.Combine(thirdParty, "configLibrary", existing + ".json")))!;
        check(mixed["disableAutoUpdates"]?.GetValue<bool>() == true && mixed["inferenceProvider"]?.GetValue<string>() == "mine",
              "in an existing configuration only the update key changes");
        ManualUpdates.SetEnabled(false);
        var back = JsonNode.Parse(File.ReadAllText(Path.Combine(thirdParty, "configLibrary", existing + ".json")))!;
        check(back["disableAutoUpdates"]?.GetValue<bool>() == false && back["inferenceProvider"]?.GetValue<string>() == "mine",
              "and turning it off puts back the value that was there");

        ManualUpdates.ManagedOverride = () => true;
        check(ManualUpdates.SetEnabled(true) is { } refused && refused.Length > 0 && !ManualUpdates.Enabled,
              "a Claude managed by policy is refused, since a local setting there would be a false promise");
        ManualUpdates.ManagedOverride = null;
        ManualUpdates.FolderOverride = null;
    }
}
