using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeGraft.Core;

/// Bringing an existing Claude forward. Claude keeps running in the
/// notification area when its last window is closed, and that window still
/// exists, hidden, so showing it again is all the reveal needs.
public static partial class WindowTools
{
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int command);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hwnd, uint command);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    const int SwShow = 5, SwRestore = 9;
    const uint GwOwner = 4;
    const int GwlStyle = -16;
    const long WsCaption = 0x00C00000;

    /// The top-level windows of one process that look like a Claude window:
    /// Chromium's class, a caption and a title, owned by nobody.
    public static List<IntPtr> AppWindows(int pid)
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
            if (GetWindowTextLength(hwnd) == 0) return true;
            var name = new StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            if (!name.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return true;
            if ((GetWindowLongPtr(hwnd, GwlStyle).ToInt64() & WsCaption) != WsCaption) return true;
            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public static bool Reveal(int pid)
    {
        var windows = AppWindows(pid);
        var target = windows.FirstOrDefault(IsWindowVisible);
        if (target == IntPtr.Zero) target = windows.FirstOrDefault();
        if (target == IntPtr.Zero) return false;

        ShowWindow(target, IsIconic(target) ? SwRestore : SwShow);
        if (SetForegroundWindow(target)) return true;
        // Windows only hands the foreground to the process that had the last
        // input. A tap of Alt counts as input of ours, which is the documented
        // way past that rule for an app acting on a person's own click.
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        return SetForegroundWindow(target);
    }

    public static int? ForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }
}
