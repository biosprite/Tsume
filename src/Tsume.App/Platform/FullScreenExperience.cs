using System.Runtime.InteropServices;

namespace Tsume.App.Platform;

/// <summary>
/// Whether Windows' Xbox Full Screen Experience ("Gaming Posture") is currently active.
/// Queries <c>gamemode.dll</c>, which ships with Windows 11 24H2+; returns false on older builds.
/// </summary>
internal static class FullScreenExperience
{
    public static bool IsActive
    {
        get
        {
            try
            {
                return IsGamingFullScreenExperienceActive();
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    [DllImport("gamemode.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsGamingFullScreenExperienceActive();
}
