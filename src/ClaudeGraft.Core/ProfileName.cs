using System.Text;

namespace ClaudeGraft.Core;

/// A profile folder is one plain name inside the profiles root. Anything else
/// could point a delete, or a second Claude, at somebody else's data.
public static class ProfileName
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Claude", "Claude-Data", "ClaudeGraft", "AnthropicClaude",
    };

    static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// Null when the folder name is usable, otherwise the reason it is not.
    public static string? Validate(string? folder)
    {
        var name = (folder ?? "").Trim();
        if (name.Length == 0) return S.T("The profile folder needs a name.");
        if (name.IndexOfAny(['\\', '/', ':']) >= 0)
            return S.T("The profile folder must be a single folder name, not a path.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return S.F("“{0}” has characters Windows does not allow in a folder name.", name);
        if (name == "." || name == ".." || name.StartsWith('.') || name.EndsWith('.'))
            return S.F("“{0}” is not a usable folder name.", name);
        if (name.Length > 64) return S.T("The profile folder name is too long.");
        var stem = name.Split('.')[0].TrimEnd();
        if (DeviceNames.Contains(stem))
            return S.F("“{0}” is a name Windows keeps for itself.", name);
        if (Reserved.Contains(name))
            return S.T("That folder belongs to Claude or Claude Graft. Pick another name.");
        return null;
    }

    /// A folder suggested from a display name: "Claude (work) 2" → "Claude-work-2".
    /// It still goes through `Validate`, and `Unique` keeps it clear of the
    /// folders already there.
    public static string Suggest(string displayName)
    {
        var builder = new StringBuilder();
        foreach (var c in displayName.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_') builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0) slug = "Claude-profile";
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        if (Validate(slug) != null) slug += "-profile";
        return slug;
    }

    public static string Unique(string folder, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(folder) && !Directory.Exists(Paths.ProfileDir(folder))) return folder;
        for (var n = 2; ; n++)
        {
            var candidate = $"{folder}-{n}";
            if (!used.Contains(candidate) && !Directory.Exists(Paths.ProfileDir(candidate))) return candidate;
        }
    }
}
