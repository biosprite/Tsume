using System.Runtime.InteropServices;

namespace Tsume.App.Platform.Interop;

/// <summary>Thread and module calls. Names mirror the Win32 headers.</summary>
internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? moduleName);
}
