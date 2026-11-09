using System.Runtime.InteropServices;

namespace ClaudeGraft;

/// Whether this process runs inside an app package it inherited from the
/// program that started it (Claude Desktop is one, from the Store).
static partial class PackageIdentity
{
    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref int length, IntPtr name);

    const int NoPackage = 15700;

    public static bool Inherited
    {
        get
        {
            try
            {
                var length = 0;
                return GetCurrentPackageFullName(ref length, IntPtr.Zero) != NoPackage;
            }
            catch { return false; }
        }
    }
}
