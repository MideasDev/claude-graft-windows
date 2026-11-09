using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core.Updates;

public sealed record GraftRelease(string Version, string ZipUrl, string Sha256, long Size, string? Notes);

/// Claude Graft's own updates, from its GitHub releases: it installs itself
/// and says nothing, unless the person holds updates. The release's zip is
/// checked against the SHA-256 GitHub records for each uploaded file (or the
/// `.sha256` file beside it), and the executable is swapped beside the one
/// running, which Windows lets be renamed but not overwritten.
public static class SelfUpdate
{
    public const string Repository = "MideasDev/claude-graft-windows";
    public static string ReleasesUrl => $"https://github.com/{Repository}/releases";
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API answers nothing to a request without one; this names
        // the app, never the person.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeGraft-Windows/" + Current);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static string Current =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    static string HoldFile => Path.Combine(Paths.GraftHome, "hold-updates");
    public static bool Held
    {
        get => File.Exists(HoldFile);
        set
        {
            try { if (value) { Directory.CreateDirectory(Paths.GraftHome); File.WriteAllText(HoldFile, ""); } else File.Delete(HoldFile); } catch { }
        }
    }

    /// The release's zip for this machine, framework-dependent, and its hash.
    public static GraftRelease? ParseRelease(string json, Func<string, string?>? fetchText = null)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            if (o["draft"]?.GetValue<bool>() == true || o["prerelease"]?.GetValue<bool>() == true) return null;
            var tag = o["tag_name"]?.GetValue<string>()?.TrimStart('v', 'V');
            if (tag == null || !Version.TryParse(tag, out _)) return null;
            var assets = (o["assets"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var zip = assets.FirstOrDefault(a => a["name"]?.GetValue<string>() is { } n &&
                                                 n.StartsWith("ClaudeGraft-", StringComparison.OrdinalIgnoreCase) &&
                                                 n.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
            if (zip == null) return null;
            var url = zip["browser_download_url"]?.GetValue<string>();
            if (url == null || !url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;
            var sha = zip["digest"]?.GetValue<string>() is { } d && d.StartsWith("sha256:") ? d[7..] : null;
            if (sha == null)
            {
                var side = assets.FirstOrDefault(a => a["name"]?.GetValue<string>() == zip["name"]!.GetValue<string>() + ".sha256");
                var sideUrl = side?["browser_download_url"]?.GetValue<string>();
                sha = sideUrl == null ? null : fetchText?.Invoke(sideUrl)?.Trim().Split(' ', '\t', '\n')[0];
            }
            if (sha == null || sha.Length != 64) return null;
            return new GraftRelease(tag, url, sha.ToLowerInvariant(), zip["size"]?.GetValue<long>() ?? 0, o["body"]?.GetValue<string>());
        }
        catch { return null; }
    }

    public static async Task<(GraftRelease? Release, string? Problem)> CheckAsync()
    {
        try
        {
            var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, null);
            if (!response.IsSuccessStatusCode) return (null, S.F("GitHub answered {0}.", (int)response.StatusCode));
            var json = await response.Content.ReadAsStringAsync();
            var release = ParseRelease(json, url => Http.GetStringAsync(url).GetAwaiter().GetResult());
            return (release != null && ClaudeUpdates.IsNewer(release.Version, Current) ? release : null, null);
        }
        catch (Exception e)
        {
            Diagnostics.Note("self-update.check-failed", e.GetType().Name);
            return (null, S.T("GitHub could not be reached."));
        }
    }

    /// Downloads, checks and puts the new executable in place, returning its
    /// path for the caller to start; the old one is kept beside it, renamed,
    /// and removed at a later start.
    public static async Task<(string? NewExe, string? Problem)> InstallAsync(GraftRelease release)
    {
        var exe = Environment.ProcessPath;
        if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return (null, S.T("Claude Graft is not running from its own executable."));
        var dir = Path.GetDirectoryName(exe)!;
        var work = Path.Combine(Path.GetTempPath(), "ClaudeGraft-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, "release.zip");
            await using (var source = await Http.GetStreamAsync(release.ZipUrl))
            await using (var target = File.Create(zip))
                await source.CopyToAsync(target);
            if (release.Size > 0 && new FileInfo(zip).Length != release.Size) return (null, S.T("The download was incomplete."));
            await using (var check = File.OpenRead(zip))
                if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(check)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (null, S.T("The download did not match the release's checksum, so it was not installed."));
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals("ClaudeGraft.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return (null, S.T("The release has no ClaudeGraft.exe in it."));
                entry.ExtractToFile(Path.Combine(work, "ClaudeGraft.exe"));
            }
            var staged = Path.Combine(dir, "ClaudeGraft.new.exe");
            File.Copy(Path.Combine(work, "ClaudeGraft.exe"), staged, overwrite: true);
            var previous = Path.Combine(dir, $"ClaudeGraft.prev-{Current}.exe");
            if (File.Exists(previous)) try { File.Delete(previous); } catch { previous = Path.Combine(dir, $"ClaudeGraft.prev-{Current}-{Guid.NewGuid():N}.exe"); }
            File.Move(exe, previous);
            try { File.Move(staged, exe); }
            catch { File.Move(previous, exe); throw; }
            Diagnostics.Note("self-update.installed", $"{Current} -> {release.Version}");
            return (exe, null);
        }
        catch (Exception e) { return (null, e.Message); }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// Copies left by earlier updates go once nothing runs them.
    public static void SweepPrevious()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return;
            foreach (var old in Directory.GetFiles(Path.GetDirectoryName(exe)!, "ClaudeGraft.prev-*.exe"))
                try { File.Delete(old); } catch { }
        }
        catch { }
    }

    public static void Relaunch(string exe)
    {
        try { Process.Start(new ProcessStartInfo(exe, "--tray") { UseShellExecute = true }); } catch { }
    }
}
