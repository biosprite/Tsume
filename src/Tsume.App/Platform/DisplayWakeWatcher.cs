using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Tsume.App.Platform.Interop;

namespace Tsume.App.Platform;

/// <summary>
/// Watches <c>GUID_CONSOLE_DISPLAY_STATE</c> via a hidden message-only window so Tsume detects Modern Standby (S0ix)
/// display wakes, where Windows does not raise <c>SystemEvents.PowerModeChanged</c> (<c>PowerModes.Resume</c>).
/// </summary>
public sealed class DisplayWakeWatcher : IDisposable
{
    /// <summary>GUID_CONSOLE_DISPLAY_STATE: 0 = Off, 1 = On, 2 = Dimmed.</summary>
    public static readonly Guid ConsoleDisplayStateGuid = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    public const uint DisplayOff = 0;
    public const uint DisplayOn = 1;
    public const uint DisplayDimmed = 2;

    private readonly Dispatcher _dispatcher;
    private readonly Tracker _tracker = new();
    private HwndSource? _source;
    private nint _registration;
    private bool _disposed;

    /// <summary>Raised on the UI thread when the console display transitions from Off (0) back to On (1).</summary>
    public event Action? DisplayWoke;

    public DisplayWakeWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        if (_dispatcher.CheckAccess()) Start();
        else _dispatcher.BeginInvoke(Start);
    }

    /// <summary>Pure state tracker for <c>GUID_CONSOLE_DISPLAY_STATE</c> transitions; ignores initial On and Dimmed->On.</summary>
    public sealed class Tracker
    {
        private bool _wasOff;

        public bool Feed(uint displayState)
        {
            if (displayState == DisplayOff)
            {
                _wasOff = true;
                return false;
            }

            if (displayState == DisplayOn && _wasOff)
            {
                _wasOff = false;
                return true;
            }

            return false;
        }
    }

    private void Start()
    {
        if (_disposed || _source is not null) return;

        var parameters = new HwndSourceParameters("TsumeDisplayWake")
        {
            ParentWindow = new nint(-3),   // HWND_MESSAGE
            Width = 0,
            Height = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        var guid = ConsoleDisplayStateGuid;
        _registration = User32.RegisterPowerSettingNotification(
            _source.Handle, ref guid, User32.DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed
            || (uint)msg != User32.WM_POWERBROADCAST
            || wParam != (nint)User32.PBT_POWERSETTINGCHANGE
            || lParam == 0)
        {
            return 0;
        }

        if (TryReadDisplayState(lParam, out var state) && _tracker.Feed(state))
            DisplayWoke?.Invoke();

        return 0;
    }

    private static unsafe bool TryReadDisplayState(nint lParam, out uint state)
    {
        state = 0;
        // POWERBROADCAST_SETTING: Guid PowerSetting (0..15), uint DataLength (16..19), byte Data[1] (20..).
        var settingGuid = Marshal.PtrToStructure<Guid>(lParam);
        if (settingGuid != ConsoleDisplayStateGuid) return false;

        var dataLength = (uint)Marshal.ReadInt32(lParam, 16);
        if (dataLength < sizeof(uint)) return false;

        state = *(uint*)((byte*)lParam + 20);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        void Cleanup()
        {
            if (_registration != 0)
            {
                User32.UnregisterPowerSettingNotification(_registration);
                _registration = 0;
            }

            if (_source is not null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }

        if (_dispatcher.CheckAccess()) Cleanup();
        else _dispatcher.BeginInvoke(Cleanup);
    }
}
