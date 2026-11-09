using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ClaudeGraft.Core;

public sealed record ClaudeProcess(int Pid, string CommandLine)
{
    /// Every helper Claude starts repeats the profile's `--user-data-dir`, so
    /// most processes that mention a profile are renderers. Only the browser
    /// process, the one with no `--type`, owns the windows.
    public bool IsBrowser => ClaudeProcesses.IsBrowserProcess(CommandLine);
    public string? DataDir => ClaudeProcesses.DataDir(CommandLine);
}

public static partial class ClaudeProcesses
{
    [GeneratedRegex("""--user-data-dir=(?:"([^"]*)"|(\S+))""", RegexOptions.IgnoreCase)]
    private static partial Regex DataDirPattern();

    [GeneratedRegex(@"(^|\s)--type=", RegexOptions.IgnoreCase)]
    private static partial Regex TypePattern();

    public static bool IsBrowserProcess(string commandLine) => !TypePattern().IsMatch(commandLine);

    public static string? DataDir(string commandLine)
    {
        var match = DataDirPattern().Match(commandLine);
        if (!match.Success) return null;
        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return value.Length == 0 ? null : value;
    }

    /// Does this browser process hold that profile? `null` asks about the main
    /// profile, which Claude opens with no `--user-data-dir` at all. A Claude
    /// someone opened on the main folder by path counts too.
    public static bool Holds(ClaudeProcess process, string? profile)
    {
        if (!process.IsBrowser) return false;
        var dir = process.DataDir;
        if (profile == null) return dir == null || Paths.Same(dir, Paths.MainProfile);
        if (dir == null) return Paths.Same(profile, Paths.MainProfile);
        return Paths.Same(dir, profile);
    }

    /// Tests replace this so they never depend on what happens to be running.
    public static Func<IReadOnlyList<ClaudeProcess>> Source { get; set; } = QueryProcesses;

    public static IReadOnlyList<ClaudeProcess> Snapshot() => Source();

    public static ClaudeProcess? BrowserFor(string? profile, IReadOnlyList<ClaudeProcess>? snapshot = null) =>
        (snapshot ?? Snapshot()).FirstOrDefault(p => Holds(p, profile));

    public static bool IsRunning(string? profile, IReadOnlyList<ClaudeProcess>? snapshot = null) =>
        BrowserFor(profile, snapshot) != null;

    /// Which profile, if any, owns this process id. Used to say which Claude
    /// is in front. `""` is the main profile; null is "not a Claude of ours".
    public static string? ProfileOf(int pid, IReadOnlyList<ClaudeProcess> snapshot)
    {
        var process = snapshot.FirstOrDefault(p => p.Pid == pid && p.IsBrowser);
        if (process == null) return null;
        var dir = process.DataDir;
        return dir == null || Paths.Same(dir, Paths.MainProfile) ? "" : Paths.Normalize(dir);
    }

    // A command line is only visible from outside by asking the kernel for
    // it. ProcessCommandLineInformation needs no more than the limited query
    // right, which this user has over its own processes, and no COM or WMI.
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    const int ProcessCommandLineInformation = 60;
    const uint QueryLimitedInformation = 0x1000;

    static IReadOnlyList<ClaudeProcess> QueryProcesses()
    {
        var found = new List<ClaudeProcess>();
        foreach (var process in Process.GetProcessesByName("claude"))
        {
            using (process)
            {
                if (!IsDesktopImage(ImagePathOf(process.Id))) continue;
                var command = CommandLineOf(process.Id);
                if (command != null) found.Add(new ClaudeProcess(process.Id, command));
            }
        }
        return found;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(IntPtr process, int flags, [Out] char[] name, ref int size);

    /// Claude Desktop's version, read from the executable of a Claude that is
    /// running, or of the classic installer's copy. The Store build's own
    /// folder cannot be listed, so with none running there may be no answer.
    public static string? DesktopVersion()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("claude"))
            {
                using (process)
                {
                    var image = ImagePathOf(process.Id);
                    if (IsDesktopImage(image)) return FileVersionInfo.GetVersionInfo(image!).ProductVersion;
                }
            }
            var classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude", "claude.exe");
            if (File.Exists(classic)) return FileVersionInfo.GetVersionInfo(classic).ProductVersion;
        }
        catch { }
        return null;
    }

    static string? ImagePathOf(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var name = new char[1024];
            var size = name.Length;
            return QueryFullProcessImageName(handle, 0, name, ref size) ? new string(name, 0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    static readonly Dictionary<string, bool> DesktopDirs = new(StringComparer.OrdinalIgnoreCase);

    /// Claude Code's command line is a `claude.exe` too, and the desktop app
    /// runs copies of it all day. Claude Desktop is the one that is an
    /// Electron app: its folder carries `resources\app.asar`.
    internal static bool IsDesktopImage(string? image)
    {
        var dir = image == null ? null : Path.GetDirectoryName(image);
        if (dir == null) return false;
        lock (DesktopDirs)
        {
            if (!DesktopDirs.TryGetValue(dir, out var known))
                DesktopDirs[dir] = known = File.Exists(Path.Combine(dir, "resources", "app.asar"));
            return known;
        }
    }

    internal static string? CommandLineOf(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return null;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0) return null;
                // A UNICODE_STRING: a length in bytes, then a pointer to the text.
                var bytes = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return bytes == 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception e)
        {
            Diagnostics.Note("processes.unreadable", $"{pid} {e.Message}");
            return null;
        }
        finally { CloseHandle(handle); }
    }
}
