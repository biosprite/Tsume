using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tsume.App.Shell;

/// <summary>
/// Keeps Tsume from being starved by the game: CPU priority High while the panel is open (Above Normal
/// while hidden), efficiency mode refused while open, GPU scheduling priority High always. Needs no
/// admin rights. TSUME_PRIORITY=off disables it, for comparing.
/// </summary>
internal static partial class RightOfWay
{
    private static readonly bool Off = Environment.GetEnvironmentVariable("TSUME_PRIORITY") == "off";
    /// <summary>Kept whole, not just its handle: the handle closes when the object is collected.</summary>
    private static readonly Process Self = Process.GetCurrentProcess();
    private static nint Tsume => Self.Handle;

    private static string _graphics = "not asked";

    /// <summary>What was asked for and what Windows said, for <see cref="OpenTrace"/>.</summary>
    public static string Standing =>
        Off ? "priority left alone" : $"priority {Now()}, graphics {_graphics}";

    private static ProcessPriorityClass Now()
    {
        Self.Refresh();
        return Self.PriorityClass;
    }

    /// <summary>Call once at start-up: how Tsume waits while hidden.</summary>
    public static void Settle()
    {
        if (Off) return;

        try
        {
            var status = D3DKMTSetProcessSchedulingPriorityClass(Tsume, GraphicsHigh);
            _graphics = status == 0 ? "High" : $"refused (0x{status:X8})";
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            _graphics = "not available";
        }

        Yield();
    }

    /// <summary>Call as the panel is about to appear: High priority, no throttling.</summary>
    public static void Claim()
    {
        if (Off) return;

        SetPriorityClass(Tsume, HIGH_PRIORITY_CLASS);
        Throttling(decideHere: true);
    }

    /// <summary>Call once the panel has gone: Above Normal priority, throttling left to Windows.</summary>
    public static void Yield()
    {
        if (Off) return;

        SetPriorityClass(Tsume, ABOVE_NORMAL_PRIORITY_CLASS);
        Throttling(decideHere: false);
    }

    /// <param name="decideHere">True: never slowed down. False: Windows decides, as it does for any program.</param>
    private static void Throttling(bool decideHere)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = decideHere ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            StateMask = 0,
        };

        SetProcessInformation(Tsume, ProcessPowerThrottling, in state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

    private const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x8000;
    private const uint HIGH_PRIORITY_CLASS = 0x0080;
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    /// <summary>D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH. The one above it, realtime, needs a privilege Tsume does not have.</summary>
    private const int GraphicsHigh = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(nint process, uint priorityClass);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(
        nint process, int informationClass, in PROCESS_POWER_THROTTLING_STATE information, uint size);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priority);
}
