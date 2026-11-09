namespace ClaudeGraft.Core;

/// Where Claude Desktop is. The Store build cannot be started from inside
/// `WindowsApps`, but it registers `claude-desktop.exe` as an app execution
/// alias, and an alias takes arguments the way an ordinary program does.
public static class ClaudeInstall
{
    public static Func<string?>? Override { get; set; }

    public static string? Executable
    {
        get
        {
            if (Override != null) return Override();
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates =
            [
                Path.Combine(local, "Microsoft", "WindowsApps", "claude-desktop.exe"),
                Path.Combine(local, "AnthropicClaude", "claude.exe"),
                Path.Combine(local, "Programs", "Claude", "Claude.exe"),
            ];
            return candidates.FirstOrDefault(Exists);
        }
    }

    // An execution alias is a zero-length reparse point; `File.Exists` can
    // answer either way for one, the directory listing never lies about it.
    static bool Exists(string path)
    {
        try
        {
            if (File.Exists(path)) return true;
            var dir = Path.GetDirectoryName(path)!;
            return Directory.Exists(dir) &&
                   Directory.EnumerateFiles(dir, Path.GetFileName(path)).Any();
        }
        catch { return false; }
    }
}
