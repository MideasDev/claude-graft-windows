using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ClaudeGraft.Core.Updates;

public sealed record ClaudeRelease(string Version, string Url, string Sha256, long Size, string? Notes);

/// Claude Desktop's own updates, as far as Claude Graft takes part in them:
/// saying when a release is out, a manual mode that keeps Claude from
/// updating itself (and closing work in progress) on its own, and installing
/// a release when asked, with every Claude closed.
public static class ClaudeUpdates
{
    public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// The feed Claude itself asks, anonymously. A device identifier of
    /// Claude Graft's own, kept here, puts it in the same rollout as Claude's
    /// own updater would be, without reading anything of Claude's.
    public static string FeedUrl(string version, string kind = "msix") =>
        $"https://api.anthropic.com/api/desktop/win32/{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}/{kind}/update" +
        $"?device_id={Uri.EscapeDataString(DeviceId())}&version={Uri.EscapeDataString(version)}&os_version={Uri.EscapeDataString(Environment.OSVersion.Version.ToString())}";

    static string DeviceId()
    {
        var file = Path.Combine(Paths.GraftHome, "claude-update-device.txt");
        try { if (File.Exists(file) && Guid.TryParse(File.ReadAllText(file).Trim(), out var g)) return g.ToString(); } catch { }
        var id = Guid.NewGuid().ToString();
        try { Directory.CreateDirectory(Paths.GraftHome); File.WriteAllText(file, id); } catch { }
        return id;
    }

    public static ClaudeRelease? ParseFeed(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var current = o["currentRelease"]?.GetValue<string>();
            var releases = o["releases"] as JsonArray ?? [];
            var to = releases.Select(r => r?["updateTo"] as JsonObject).OfType<JsonObject>()
                .FirstOrDefault(r => r["version"]?.GetValue<string>() == current) ?? releases.LastOrDefault()?["updateTo"] as JsonObject;
            if (to == null) return null;
            var url = to["url"]?.GetValue<string>();
            var sha = to["sha256"]?.GetValue<string>();
            var version = to["version"]?.GetValue<string>() ?? current;
            if (url == null || sha == null || version == null) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !u.Host.EndsWith("claude.ai", StringComparison.OrdinalIgnoreCase)) return null;
            return new ClaudeRelease(version, url, sha.ToLowerInvariant(), to["size"]?.GetValue<long>() ?? 0, to["notes"]?.GetValue<string>());
        }
        catch { return null; }
    }

    public static bool IsNewer(string candidate, string? installed) =>
        Version.TryParse(candidate, out var a) && (installed == null || !Version.TryParse(installed, out var b) || a > b);

    /// Which build is installed: the Store build from the Microsoft Store is
    /// updated by the Store, and Claude Graft leaves it to it.
    public enum Kind { Msix, StoreManaged, Classic, None }

    public static (Kind Kind, string? Version) Installed()
    {
        var folder = Sidebar.SidebarSync.RuntimeFolder();
        if (folder == null) return (Kind.None, null);
        var exe = Path.Combine(folder, "claude.exe");
        string? version = null;
        try { version = File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion : null; } catch { }
        if (folder.Contains("AnthropicPBC.Claude", StringComparison.OrdinalIgnoreCase)) return (Kind.StoreManaged, version);
        if (folder.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return (Kind.Msix, version);
        return (Kind.Classic, version);
    }

    public sealed record Check(string? Installed, ClaudeRelease? Latest, Kind Kind, string? Problem, DateTime At)
    {
        public bool Available => Latest != null && IsNewer(Latest.Version, Installed);
    }

    public static async Task<Check> CheckAsync()
    {
        var (kind, installed) = Installed();
        if (kind == Kind.None) return new(null, null, kind, S.T("Claude Desktop is not installed."), Clock());
        try
        {
            var json = await Http.GetStringAsync(FeedUrl(installed ?? "0.0.0", kind == Kind.Classic ? "squirrel" : "msix"));
            var latest = ParseFeed(json);
            return new(installed, latest, kind, latest == null ? S.T("Claude's update feed sent something unexpected.") : null, Clock());
        }
        catch (Exception e)
        {
            Diagnostics.Note("claude-update.check-failed", e.GetType().Name);
            return new(installed, null, kind, S.T("Claude's update feed could not be reached."), Clock());
        }
    }

    /// Installs a release the way Windows installs any app package: Windows
    /// checks the package's signature, and the profiles, which live outside
    /// the package, are untouched. Only with every Claude closed; nothing here
    /// ever closes one.
    public static async Task<string?> InstallAsync(ClaudeRelease release, IProgress<int>? progress, CancellationToken cancel)
    {
        var (kind, before) = Installed();
        if (kind != Kind.Msix) return S.T("This Claude is updated by the Microsoft Store or its own installer, not from here.");
        if (ClaudeProcesses.Snapshot().Any(p => p.IsBrowser)) return S.T("Close every Claude first. Claude Graft never closes one for you.");
        var file = Path.Combine(Path.GetTempPath(), "ClaudeGraft-" + release.Version + ".msix");
        try
        {
            using (var response = await Http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.Size;
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var target = File.Create(file);
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (total > 0) progress?.Report((int)(done * 100 / total));
                }
            }
            if (release.Size > 0 && new FileInfo(file).Length != release.Size) return S.T("The download was incomplete.");
            await using (var check = File.OpenRead(file))
                if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancel)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    return S.T("The download did not match Claude's published checksum, so it was not installed.");
            if (ClaudeProcesses.Snapshot().Any(p => p.IsBrowser)) return S.T("Close every Claude first. Claude Graft never closes one for you.");

            var info = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"Add-AppxPackage -Path '{file.Replace("'", "''")}'\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using var installer = Process.Start(info)!;
            var errors = await installer.StandardError.ReadToEndAsync(cancel);
            await installer.WaitForExitAsync(cancel);
            var (_, after) = Installed();
            Diagnostics.Note("claude-update.install", $"{before} -> {after} exit={installer.ExitCode}");
            // A prepared package alone is not success: the installed version must change.
            if (after == null || !IsNewer(after, before) && after != release.Version)
                return S.F("Windows did not install the update: {0}", errors.Trim().Split('\n').FirstOrDefault() ?? installer.ExitCode.ToString());
            return null;
        }
        catch (OperationCanceledException) { return S.T("The update was cancelled."); }
        catch (Exception e) { return e.Message; }
        finally { try { File.Delete(file); } catch { } }
    }
}

/// Manual update mode: Claude's own `disableAutoUpdates` policy, set in its
/// configuration library. On Windows that library is one for every Claude,
/// `%LOCALAPPDATA%\Claude-3p\configLibrary` (Claude's own code asks for it
/// there whatever `--user-data-dir` says), so one setting covers them all, at
/// each one's next launch. Only the keys changed are written down, with what
/// they were, so turning it off puts back exactly what was there.
public static class ManualUpdates
{
    public static Func<string>? FolderOverride { get; set; }
    public static Func<bool>? ManagedOverride { get; set; }

    static string ThirdParty => FolderOverride?.Invoke() ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-3p");
    static string Library => Path.Combine(ThirdParty, "configLibrary");
    static string StateFile => Path.Combine(Paths.GraftHome, "manual-updates.json");
    static readonly string[] PolicyKeys = ["disableAutoUpdates", "autoUpdate.disabled"];

    sealed class SavedKey { public string Name { get; set; } = ""; public bool? Previous { get; set; } }
    sealed class Change
    {
        public string ConfigId { get; set; } = "";
        public List<SavedKey> Keys { get; set; } = [];
        public bool CreatedConfig { get; set; }
        public bool CreatedMetadata { get; set; }
        public bool CreatedLibrary { get; set; }
        public bool CreatedFolder { get; set; }
    }
    sealed class State { public bool Enabled { get; set; } public Change? Change { get; set; } }

    sealed class Refused(string message) : Exception(message);

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static bool Enabled
    {
        get { try { return File.Exists(StateFile) && (JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile), Json)?.Enabled ?? false); } catch { return false; } }
    }

    /// A policy an administrator set (the registry's Policies\Claude, or a
    /// configuration supplied from elsewhere) can override the local one, so
    /// the switch would be a false promise there; it is refused instead.
    static bool Managed()
    {
        if (ManagedOverride != null) return ManagedOverride();
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Policies\Claude");
                if (key != null && (key.ValueCount > 0 || key.SubKeyCount > 0)) return true;
            }
            catch { }
        }
        return false;
    }

    static JsonObject ReadObject(string file, bool optional = false)
    {
        if (optional && !File.Exists(file)) return [];
        if (Fs.IsLink(file)) throw new Refused(S.F("Manual update mode could not read {0}. Its existing configuration was kept.", Path.GetFileName(file)));
        try { return JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidDataException(); }
        catch (Refused) { throw; }
        catch { throw new Refused(S.F("Manual update mode could not read {0}. Its existing configuration was kept.", Path.GetFileName(file))); }
    }

    static void WriteObject(string file, JsonObject o) =>
        Fs.WriteAtomic(file, System.Text.Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));

    static bool ValidId(string id) => Guid.TryParse(id, out var g) && g.ToString() == id;

    /// Null when done, otherwise why not.
    public static string? SetEnabled(bool on)
    {
        try
        {
            using var gate = new Mutex(false, @"Local\ClaudeGraftWindows.ManualUpdates");
            gate.WaitOne(TimeSpan.FromSeconds(10));
            try
            {
                var state = File.Exists(StateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile), Json) ?? new() : new State();
                if (on) Protect(state); else Restore(state);
                state.Enabled = on;
                Directory.CreateDirectory(Paths.GraftHome);
                Fs.WriteAtomic(StateFile, JsonSerializer.SerializeToUtf8Bytes(state, Json));
                Diagnostics.Note("updates.manual-mode", on.ToString());
                return null;
            }
            finally { gate.ReleaseMutex(); }
        }
        catch (Refused r) { return r.Message; }
        catch (Exception e) { return e.Message; }
    }

    static void Protect(State state)
    {
        if (Managed()) throw new Refused(S.T("Claude is managed by your organization's policy. Its administrator needs to set disableAutoUpdates; a local setting cannot guarantee protection."));
        var hadFolder = Directory.Exists(ThirdParty);
        var hadLibrary = Directory.Exists(Library);
        if (Fs.IsLink(ThirdParty) || Fs.IsLink(Library)) throw new Refused(S.T("Manual update mode refused a configuration folder that is a link."));
        var metaFile = Path.Combine(Library, "_meta.json");
        var hadMeta = File.Exists(metaFile);
        var meta = ReadObject(metaFile, optional: true);
        if (meta["hybridPointer"] != null) throw new Refused(S.T("Claude's configuration is supplied from elsewhere, so a local setting cannot guarantee protection."));
        var applied = meta["appliedId"] is JsonValue av && av.TryGetValue<string>(out var a) ? a : null;
        if (meta["appliedId"] != null && (applied == null || !ValidId(applied)))
            throw new Refused(S.F("Manual update mode could not read {0}. Its existing configuration was kept.", "_meta.json"));
        var id = applied ?? Guid.NewGuid().ToString();
        var file = Path.Combine(Library, id + ".json");
        var config = ReadObject(file, optional: applied == null);
        if (config["bootstrapUrl"] != null || config["bootstrap.url"] != null)
            throw new Refused(S.T("Claude's configuration is supplied from elsewhere, so a local setting cannot guarantee protection."));

        var change = state.Change?.ConfigId == id ? state.Change : null;
        if (change == null)
        {
            change = new Change
            {
                ConfigId = id, CreatedConfig = applied == null && !File.Exists(file), CreatedMetadata = !hadMeta,
                CreatedLibrary = !hadLibrary, CreatedFolder = !hadFolder,
            };
            foreach (var key in PolicyKeys.Where(k => config[k] != null))
            {
                if (config[key] is not JsonValue v || !v.TryGetValue<bool>(out var b))
                    throw new Refused(S.F("Manual update mode could not read {0}. Its existing configuration was kept.", key));
                change.Keys.Add(new SavedKey { Name = key, Previous = b });
            }
            if (change.Keys.Count == 0) change.Keys.Add(new SavedKey { Name = PolicyKeys[0], Previous = null });
            // Written down before anything is changed.
            state.Change = change;
            Directory.CreateDirectory(Paths.GraftHome);
            Fs.WriteAtomic(StateFile, JsonSerializer.SerializeToUtf8Bytes(state, Json));
        }
        Directory.CreateDirectory(Library);
        if (change.Keys.Any(k => !(config[k.Name] is JsonValue v && v.TryGetValue<bool>(out var b) && b)))
        {
            foreach (var k in change.Keys) config[k.Name] = true;
            WriteObject(file, config);
        }
        if (applied == null)
        {
            var entries = meta["entries"] as JsonArray ?? [];
            if (!entries.Any(e => e?["id"]?.GetValue<string>() == id)) entries.Add(new JsonObject { ["id"] = id, ["name"] = "Manual updates" });
            meta["entries"] = entries.DeepClone();
            meta["appliedId"] = id;
            WriteObject(metaFile, meta);
        }
    }

    static void Restore(State state)
    {
        if (state.Change is not { } change) return;
        if (!ValidId(change.ConfigId) || change.Keys.Any(k => !PolicyKeys.Contains(k.Name))) throw new Refused(S.T("Manual update mode refused a configuration outside Claude's own folder."));
        var file = Path.Combine(Library, change.ConfigId + ".json");
        if (File.Exists(file))
        {
            var config = ReadObject(file);
            foreach (var k in change.Keys)
            {
                // Only a value still as this mode left it is put back; one the
                // person changed meanwhile is theirs.
                if (!(config[k.Name] is JsonValue v && v.TryGetValue<bool>(out var b) && b)) continue;
                if (k.Previous is { } p) config[k.Name] = p; else config.Remove(k.Name);
            }
            if (change.CreatedConfig && config.Count == 0)
            {
                var metaFile = Path.Combine(Library, "_meta.json");
                var meta = ReadObject(metaFile, optional: true);
                if (meta["appliedId"]?.GetValue<string>() == change.ConfigId) meta.Remove("appliedId");
                if (meta["entries"] is JsonArray entries)
                    meta["entries"] = new JsonArray(entries.Where(e => e?["id"]?.GetValue<string>() != change.ConfigId).Select(e => e?.DeepClone()).ToArray());
                if (change.CreatedMetadata && meta.All(p => p.Key == "entries") && (meta["entries"] as JsonArray ?? []).Count == 0) File.Delete(metaFile);
                else if (File.Exists(metaFile)) WriteObject(metaFile, meta);
                File.Delete(file);
                if (change.CreatedLibrary) try { Directory.Delete(Library); } catch { }
                if (change.CreatedFolder) try { Directory.Delete(ThirdParty); } catch { }
            }
            else WriteObject(file, config);
        }
        state.Change = null;
    }
}
