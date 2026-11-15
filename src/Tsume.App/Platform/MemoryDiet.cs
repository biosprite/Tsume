using System.Runtime.InteropServices;

namespace Tsume.App.Platform;

/// <summary>Hands unused memory back to Windows once the panel has been closed for a while.</summary>
public static partial class MemoryDiet
{
    public static void Trim()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        // (-1, -1) asks Windows to page out everything that is not actively in use.
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);
}
