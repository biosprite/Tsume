using System.Runtime.InteropServices;

namespace Tsume.App.Platform.Interop;

/// <summary>Thread calls. Names mirror the Win32 headers.</summary>
internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();
}
