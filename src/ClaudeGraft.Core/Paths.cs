namespace ClaudeGraft.Core;

/// Where everything lives. The test suite points both roots at a throwaway
/// folder, so nothing it does can reach a real profile.
public static class Paths
{
    public static string? HomeOverride { get; set; }

    /// Set by the tests; `null` inside means "there is no main profile".
    public static Func<string?>? MainProfileOverride { get; set; }

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string GraftHome => HomeOverride ?? Path.Combine(LocalAppData, "ClaudeGraft");
    public static string ProfilesRoot => Path.Combine(GraftHome, "Profiles");
    public static string IconsDir => Path.Combine(GraftHome, "Icons");
    public static string StoreFile => Path.Combine(GraftHome, "shortcuts.json");
    public static string LogFile => Path.Combine(GraftHome, "diagnostics.log");

    /// The profile Claude uses when it is opened the ordinary way. The Store
    /// (MSIX) build keeps it in `Claude-Data\claude_<publisher>`, outside the
    /// package so that it survives updates; the older installer kept it in
    /// Roaming. Neither is ever opened with `--user-data-dir`: its only mark
    /// on a running process is the absence of one.
    public static string? MainProfile
    {
        get
        {
            if (MainProfileOverride != null) return MainProfileOverride();
            var data = Path.Combine(LocalAppData, "Claude-Data");
            if (Directory.Exists(data))
            {
                var newest = new DirectoryInfo(data).GetDirectories("claude_*")
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (newest != null) return newest.FullName;
            }
            var roaming = Path.Combine(RoamingAppData, "Claude");
            return Directory.Exists(roaming) ? roaming : null;
        }
    }

    public static string ProfileDir(string folder) => Path.Combine(ProfilesRoot, folder);

    /// Windows paths compare without case and without a trailing separator.
    /// Comparing whole normalised paths, never prefixes, is what keeps
    /// `Profiles\Claude 2` from matching `Profiles\Claude 22`.
    public static bool Same(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string path)
    {
        try { path = Path.GetFullPath(path.Trim().Trim('"')); } catch { }
        return path.TrimEnd('\\', '/');
    }

    /// True only for a folder sitting directly inside the profiles root.
    /// Delete and every write go through this, so a damaged store cannot send
    /// either at somebody else's data.
    public static bool IsOwnProfile(string path)
    {
        try
        {
            var full = Normalize(path);
            var parent = Path.GetDirectoryName(full);
            return parent != null && Same(parent, ProfilesRoot) && ProfileName.Validate(Path.GetFileName(full)) == null;
        }
        catch { return false; }
    }
}
