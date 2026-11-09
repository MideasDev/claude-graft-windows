using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeGraft.Core;

/// Filesystem questions asked the way the graft needs them answered on
/// Windows. A link is a junction (a folder) or a symbolic link (a file):
/// junctions need no privilege, file links need Developer Mode or an
/// administrator, and a file that cannot be linked is simply left the
/// profile's own.
public static partial class Fs
{
    /// The entry's own attributes, never its target's: a link answers with
    /// ReparsePoint, and Directory only when it is a folder link. Asking a
    /// DirectoryInfo about a file link answers as if it were a folder, which
    /// sent file links to `Directory.Delete` ("the directory name is
    /// invalid", measured on a machine with Developer Mode on).
    static FileAttributes? OwnAttributes(string path)
    {
        try { return File.GetAttributes(path); } catch { return null; }
    }

    static bool IsFolderEntry(FileAttributes attributes) => attributes.HasFlag(FileAttributes.Directory);

    public static bool IsLink(string path) =>
        OwnAttributes(path) is { } a && a.HasFlag(FileAttributes.ReparsePoint) && LinkTarget(path) != null;

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || IsLink(path);

    /// Follows links, as the macOS original's `isDirectory` does.
    public static bool IsDirectory(string path) => Directory.Exists(path);

    public static string? LinkTarget(string path)
    {
        try
        {
            if (OwnAttributes(path) is not { } a) return null;
            FileSystemInfo info = IsFolderEntry(a) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget;
        }
        catch { return null; }
    }

    /// Removes the link itself, never what it points at. `Directory.Delete`
    /// without recursion on a junction takes the junction alone.
    public static bool RemoveLink(string path)
    {
        try
        {
            if (OwnAttributes(path) is { } a && IsFolderEntry(a)) Directory.Delete(path, recursive: false);
            else File.Delete(path);
            return true;
        }
        catch (Exception e)
        {
            Diagnostics.Note("link.remove-failed", $"{path} {e.Message}");
            return false;
        }
    }

    /// Junctions are made by `mklink /J`, the one route Windows offers to
    /// any user; the API behind it is an undocumented reparse buffer.
    public static Func<string, string, bool> MakeJunction { get; set; } = (link, target) =>
    {
        var info = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    };

    public static bool CreateLink(string link, string target)
    {
        try
        {
            if (Directory.Exists(target)) return MakeJunction(link, target);
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e)
        {
            Diagnostics.Note("link.unsupported", $"{Path.GetFileName(link)} {e.GetType().Name}");
            return false;
        }
    }

    // ---- Real paths ----

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(IntPtr handle, [Out] char[] path, uint length, uint flags);

    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    const uint ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;

    /// Where a path really is once every junction and link on the way has
    /// been followed. A path that does not exist yet resolves as its nearest
    /// existing parent does, with the rest appended.
    public static string RealPath(string path)
    {
        var full = Paths.Normalize(path);
        var resolved = Final(full);
        if (resolved != null) return resolved;
        var parent = Path.GetDirectoryName(full);
        return parent == null || parent == full ? full : Path.Combine(RealPath(parent), Path.GetFileName(full));
    }

    static string? Final(string path)
    {
        var handle = CreateFile(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;
        try
        {
            var buffer = new char[1024];
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length) return null;
            var text = new string(buffer, 0, (int)length);
            if (text.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) text = @"\\" + text[8..];
            else if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) text = text[4..];
            return text.TrimEnd('\\');
        }
        finally { CloseHandle(handle); }
    }

    public static bool SameReal(string a, string b) =>
        string.Equals(RealPath(a), RealPath(b), StringComparison.OrdinalIgnoreCase);

    /// Whether `path`, followed through every link, lands inside `root`.
    public static bool ResolvesInside(string path, string root) =>
        RealPath(path).StartsWith(RealPath(root) + "\\", StringComparison.OrdinalIgnoreCase);

    public static string[] Names(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)!).ToArray(); }
        catch { return []; }
    }

    /// Null when the folder could not be listed, which is not the same as
    /// an empty one.
    public static string[]? NamesOrNull(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)!).ToArray(); }
        catch { return null; }
    }

    public static DateTime Modified(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    /// Reads a file Claude may be writing at the same moment. Null only when
    /// it could not be read; the caller asks `Exists` to tell "gone" from
    /// "busy", because on Windows a file mid-write can refuse to be read.
    public static byte[]? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch { return null; }
    }

    /// Written beside and renamed over, which is how Claude writes its own
    /// records, so a reader never sees half a file.
    public static bool WriteAtomic(string path, byte[] data)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".graft-tmp");
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            return false;
        }
    }

    public static bool Copy(string from, string to)
    {
        var data = ReadShared(from);
        return data != null && !Exists(to) && WriteAtomic(to, data);
    }

    public static bool Move(string from, string to)
    {
        try
        {
            if (Directory.Exists(from) && !IsLink(from)) Directory.Move(from, to);
            else File.Move(from, to);
            return true;
        }
        catch (Exception e)
        {
            Diagnostics.Note("move.failed", $"{from} -> {Path.GetFileName(to)} {e.Message}");
            return false;
        }
    }

    public static bool Delete(string path)
    {
        try
        {
            if (IsLink(path)) return RemoveLink(path);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    public static string Digest(byte[] data)
    {
        // FNV-1a, as the macOS original keeps it: the state file written by
        // one is then readable by the other's rules.
        ulong hash = 0xcbf29ce484222325;
        foreach (var b in data) { hash ^= b; hash *= 0x100000001b3; }
        return hash.ToString("x");
    }
}
