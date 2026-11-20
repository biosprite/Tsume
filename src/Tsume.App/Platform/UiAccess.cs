using System.Runtime.InteropServices;

namespace Tsume.App.Platform;

/// <summary>
/// Whether this copy of the panel runs with UIAccess — the right the on-screen keyboard uses to place its window
/// in the band above the taskbar, Start, Search, and elevated windows. The helper grants it when it starts
/// the panel; without it the panel cannot show over those. Read once at start-up.
/// </summary>
internal static class UiAccess
{
    public static bool IsGranted { get; } = Read();

    private static bool Read()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return false;

        try
        {
            return GetTokenInformation(token, TokenUIAccess, out var granted, sizeof(int), out _) && granted != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const int TokenUIAccess = 26;        // TOKEN_INFORMATION_CLASS.TokenUIAccess
    private const uint TokenQuery = 0x0008;

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint token, int infoClass, out int info, int length, out int returnLength);
}
