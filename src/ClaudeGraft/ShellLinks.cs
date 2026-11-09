using ClaudeGraft.Core;
using Microsoft.Win32;

namespace ClaudeGraft;

/// Desktop and Start shortcuts. A shortcut is this executable with
/// `--open <folder>`, so the shortcut behaves like the Claude Graft that is
/// installed now, whatever version made it. Only shortcuts recorded in a
/// Claude's `Links` are ever rewritten or deleted: never one this app did
/// not make.
static class ShellLinks
{
    public static string Executable => Environment.ProcessPath ?? Application.ExecutablePath;

    public static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string StartDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Claude Graft");

    public static string IconFor(Shortcut s) => Path.Combine(Paths.IconsDir, s.Folder + ".ico");

    static string FileNameFor(string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim();
        return (clean.Length == 0 ? "Claude" : clean) + ".lnk";
    }

    public static string Create(Shortcut s, string directory)
    {
        Directory.CreateDirectory(directory);
        IconMaker.WriteIco(IconFor(s), IconMaker.Parse(s.Color), IconMaker.Mark(s.Name));
        var path = Path.Combine(directory, FileNameFor(s.Name));
        Write(path, s);
        if (!s.Links.Any(l => Paths.Same(l, path))) s.Links.Add(path);
        return path;
    }

    static void Write(string path, Shortcut s)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = Executable;
        link.Arguments = $"--open \"{s.Folder}\"";
        link.WorkingDirectory = Path.GetDirectoryName(Executable);
        link.IconLocation = IconFor(s) + ",0";
        link.Description = $"{s.Name} — Claude Graft";
        link.Save();
    }

    static string? TargetOf(string path)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(path);
            return (string)link.TargetPath;
        }
        catch { return null; }
    }

    public static void Delete(Shortcut s)
    {
        foreach (var link in s.Links.ToList())
        {
            try { if (File.Exists(link) && link.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) File.Delete(link); }
            catch (Exception e) { Diagnostics.Note("links.delete-failed", $"{link} {e.Message}"); }
        }
        s.Links.Clear();
        try { File.Delete(IconFor(s)); } catch { }
        try { if (Directory.Exists(StartDir) && !Directory.EnumerateFileSystemEntries(StartDir).Any()) Directory.Delete(StartDir); } catch { }
    }

    /// After a rename the old shortcut files still carry the old name.
    /// They are replaced where they were, under the new one.
    public static void Rename(Shortcut s)
    {
        var dirs = s.Links.Select(Path.GetDirectoryName).Where(d => d != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Delete(s);
        foreach (var dir in dirs) Create(s, dir!);
    }

    /// Run at every start. A shortcut that points at an older copy of this
    /// program (moved, or updated into a new folder) is pointed at this one;
    /// one the person deleted is forgotten, never put back. Returns whether
    /// anything changed, so the list is saved only then.
    public static bool Refresh(Store store)
    {
        var changed = false;
        foreach (var s in store.Data.Shortcuts)
        {
            foreach (var link in s.Links.ToList())
            {
                if (!File.Exists(link)) { s.Links.Remove(link); changed = true; continue; }
                var target = TargetOf(link);
                if (target != null && !Paths.Same(target, Executable))
                {
                    try
                    {
                        if (!File.Exists(IconFor(s)))
                            IconMaker.WriteIco(IconFor(s), IconMaker.Parse(s.Color), IconMaker.Mark(s.Name));
                        Write(link, s);
                        Diagnostics.Note("links.refreshed", s.Folder);
                    }
                    catch (Exception e) { Diagnostics.Note("links.refresh-failed", $"{s.Folder} {e.Message}"); }
                }
            }
        }
        return changed;
    }
}

/// Starting with Windows is a value under the user's own Run key, and only
/// while the person has the option ticked.
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "ClaudeGraft";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, $"\"{ShellLinks.Executable}\" --tray");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// Keeps the value pointing at this copy, if the person turned it on.
    public static void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is string current && !current.Contains(ShellLinks.Executable, StringComparison.OrdinalIgnoreCase))
                Enabled = true;
        }
        catch { }
    }
}
