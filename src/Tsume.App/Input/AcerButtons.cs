using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Tsume.App.Platform;
using Tsume.App.Platform.Interop;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Input;

/// <summary>
/// Listens for the Acer Predator Atlas 8's dedicated hardware buttons using Win32 Raw Input
/// (<c>RIDEV_INPUTSINK</c> on a hidden message-only window):
/// <list type="bullet">
///   <item>PredatorSense key (OEM button): keyboard scan code <c>E0 75</c>, or vendor HID page <c>0x88</c> usage <c>0x01</c> with bytes <c>[1]=0x81, [2]=0xFF</c>.</item>
///   <item>Widget / Quick Menu key (Game Bar button): vendor HID page <c>0xFFF1</c> usage <c>0x00F5</c>, report <c>0x9E</c> with <c>[1..2]=0x0000, [3]=0x05</c>.</item>
/// </list>
/// </summary>
public sealed class AcerButtons : IDisposable
{
    public const ushort KeyboardUsagePage = 0x01;
    public const ushort KeyboardUsage = 0x06;
    public const ushort PredatorHidUsagePage = 0x0088;
    public const ushort PredatorHidUsage = 0x0001;
    public const ushort GamepadExtendUsagePage = 0xFFF1;
    public const ushort GamepadExtendUsage = 0x00F5;

    public const ushort PredatorKeyMakeCode = 0x0075;
    public const ushort RiKeyBreak = 0x0001;
    public const ushort RiKeyE0 = 0x0002;

    public enum ButtonKind
    {
        None,
        OemButton,
        GameBarButton,
    }

    /// <summary>
    /// Tracks press/release state for the PredatorSense key (both keyboard <c>E0 75</c> and page-<c>0x88</c> HID)
    /// so holding the key does not auto-repeat toggle Tsume. Mirrors <c>AcerHardwareService</c>'s make/break latch.
    /// </summary>
    public sealed class PressTracker
    {
        private bool _oemKeyboardHeld;
        private bool _oemHidHeld;

        public void Reset()
        {
            _oemKeyboardHeld = false;
            _oemHidHeld = false;
        }

        public ButtonKind FeedRawKeyboard(ushort makeCode, ushort flags)
        {
            var isExtended = (flags & RiKeyE0) != 0;
            if (!isExtended || makeCode != PredatorKeyMakeCode)
                return ButtonKind.None;

            var isBreak = (flags & RiKeyBreak) != 0;
            if (isBreak)
            {
                _oemKeyboardHeld = false;
                return ButtonKind.None;
            }

            if (_oemKeyboardHeld) return ButtonKind.None;
            _oemKeyboardHeld = true;
            return ButtonKind.OemButton;
        }

        public ButtonKind FeedHidReport(ReadOnlySpan<byte> report)
        {
            if (IsPredatorHidRelease(report))
            {
                _oemHidHeld = false;
                return ButtonKind.None;
            }

            var kind = ParseHidReport(report);
            if (kind == ButtonKind.OemButton)
            {
                if (_oemHidHeld) return ButtonKind.None;
                _oemHidHeld = true;
            }

            return kind;
        }
    }

    private static readonly TimeSpan DuplicateGuard = TimeSpan.FromMilliseconds(150);

    private readonly Dispatcher _dispatcher;
    private readonly Journal _journal = new("buttons");
    private readonly nint _hwndMessage = new(-3);
    private readonly List<User32.RAWINPUTDEVICE> _registeredDevices = [];
    private readonly PressTracker _pressTracker = new();

    private HwndSource? _source;
    private bool _suspended;
    private bool _disposed;
    private DateTime _lastOemPress;
    private DateTime _lastGameBarPress;

    public event Action? OemButtonPressed;
    public event Action? GameBarButtonPressed;

    public AcerButtons(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        if (_dispatcher.CheckAccess()) Start();
        else _dispatcher.BeginInvoke(Start);
    }

    public void Suspend()
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) StopListening();
        else _dispatcher.BeginInvoke(StopListening);
    }

    public void Resume()
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) ResumeListening();
        else _dispatcher.BeginInvoke(ResumeListening);
    }

    /// <summary>Parses a <c>RAWKEYBOARD</c> make code and flags; returns <see cref="ButtonKind.OemButton"/> on PredatorSense <c>E0 75</c> press.</summary>
    public static ButtonKind ParseRawKeyboard(ushort makeCode, ushort flags)
    {
        var isExtended = (flags & RiKeyE0) != 0;
        var isBreak = (flags & RiKeyBreak) != 0;
        return isExtended && !isBreak && makeCode == PredatorKeyMakeCode
            ? ButtonKind.OemButton
            : ButtonKind.None;
    }

    /// <summary>Parses a raw HID input report payload from usage page <c>0x88</c> or <c>0xFFF1</c>.</summary>
    public static ButtonKind ParseHidReport(ReadOnlySpan<byte> report)
    {
        // Page 0xFFF1 gamepad-extend report 0x9E: [0x9E, 0x00, 0x00, 0x05, ...] = Widget / Quick Menu key.
        if (report.Length >= 4 && report[0] == 0x9E && report[1] == 0x00 && report[2] == 0x00 && report[3] == 0x05)
            return ButtonKind.GameBarButton;

        // Page 0x88 PredatorSense key report (e.g. EC report 0x04 or gamepad MI_02 report 0x37: [id, 0x81, 0xFF]).
        // Ignore gamepad-extend report IDs (0x9E / 0x9A) so page 0xFFF1 traffic can never false-match.
        if (report.Length >= 3 && report[0] is not (0x9E or 0x9A) && report[1] == 0x81 && report[2] == 0xFF)
            return ButtonKind.OemButton;

        return ButtonKind.None;
    }

    private static bool IsPredatorHidRelease(ReadOnlySpan<byte> report) =>
        report.Length >= 3 && report[0] is not (0x9E or 0x9A) && report[1] == 0x00 && report[2] == 0x00;

    private void Start()
    {
        if (_disposed || _suspended || _source is not null) return;

        var parameters = new HwndSourceParameters("TsumeAcerButtons")
        {
            ParentWindow = _hwndMessage,
            Width = 0,
            Height = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        ButtonCaptureSession.SessionClosed += OnButtonCaptureSessionClosed;
        RegisterDevices(_source.Handle);
    }

    private void OnButtonCaptureSessionClosed()
    {
        if (_disposed || _suspended) return;

        void Reapply()
        {
            if (_disposed || _suspended || _source is null) return;
            _registeredDevices.Clear();
            RegisterDevices(_source.Handle);
        }

        if (_dispatcher.CheckAccess()) Reapply();
        else _dispatcher.BeginInvoke(Reapply);
    }

    private void StopListening()
    {
        if (_disposed || _suspended) return;
        _suspended = true;
        _pressTracker.Reset();
        UnregisterDevices();
    }

    private void ResumeListening()
    {
        if (_disposed) return;
        _suspended = false;
        _pressTracker.Reset();
        if (_source is null) Start();
        else RegisterDevices(_source.Handle);
    }

    private void RegisterDevices(nint target)
    {
        if (_registeredDevices.Count > 0 || target == 0) return;

        User32.RAWINPUTDEVICE[] devices =
        [
            new() { UsagePage = KeyboardUsagePage, Usage = KeyboardUsage, Flags = User32.RIDEV_INPUTSINK, Target = target },
            new() { UsagePage = PredatorHidUsagePage, Usage = PredatorHidUsage, Flags = User32.RIDEV_INPUTSINK, Target = target },
            new() { UsagePage = GamepadExtendUsagePage, Usage = GamepadExtendUsage, Flags = User32.RIDEV_INPUTSINK, Target = target },
        ];

        // Register one-by-one so an unsupported/unavailable usage page never blocks the others.
        var size = (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>();
        foreach (var dev in devices)
        {
            if (User32.RegisterRawInputDevices([dev], 1, size))
                _registeredDevices.Add(dev);
            else
                _journal.Write($"AcerButtons RegisterRawInputDevices failed for 0x{dev.UsagePage:X4}:0x{dev.Usage:X4} (err={Marshal.GetLastWin32Error()})");
        }
    }

    private void UnregisterDevices()
    {
        if (_registeredDevices.Count == 0) return;

        var size = (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>();
        foreach (var dev in _registeredDevices)
        {
            var remove = dev with { Flags = User32.RIDEV_REMOVE, Target = 0 };
            User32.RegisterRawInputDevices([remove], 1, size);
        }
        _registeredDevices.Clear();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg != User32.WM_INPUT || _disposed || _suspended) return 0;

        var headerSize = (uint)Marshal.SizeOf<User32.RAWINPUTHEADER>();
        uint size = 0;
        if (User32.GetRawInputData(lParam, User32.RID_INPUT, null, ref size, headerSize) != 0 || size < headerSize)
            return 0;

        var buffer = new byte[size];
        if (User32.GetRawInputData(lParam, User32.RID_INPUT, buffer, ref size, headerSize) != size)
            return 0;

        var rawType = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
        var payload = buffer.AsSpan((int)headerSize);

        if (rawType == User32.RIM_TYPEKEYBOARD && payload.Length >= 4)
        {
            var makeCode = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
            if (_pressTracker.FeedRawKeyboard(makeCode, flags) == ButtonKind.OemButton)
                RaiseOem("raw-kbd E0 75");
        }
        else if (rawType == User32.RIM_TYPEHID && payload.Length >= 8)
        {
            var sizeHid = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
            if (sizeHid <= 0 || count <= 0 || 8 + sizeHid * count > payload.Length)
                return 0;

            for (var i = 0; i < count; i++)
            {
                var report = payload.Slice(8 + i * sizeHid, sizeHid);
                var kind = _pressTracker.FeedHidReport(report);
                if (kind == ButtonKind.OemButton)
                    RaiseOem($"raw-hid {Convert.ToHexString(report[..Math.Min(report.Length, 8)])}");
                else if (kind == ButtonKind.GameBarButton)
                    RaiseGameBar($"raw-hid {Convert.ToHexString(report[..Math.Min(report.Length, 8)])}");
            }
        }

        return 0;
    }

    private void RaiseOem(string source)
    {
        var now = DateTime.UtcNow;
        if (now - _lastOemPress < DuplicateGuard) return;
        _lastOemPress = now;

        OemButtonPressed?.Invoke();
        _journal.Write($"acer-oem ({source})");
    }

    private void RaiseGameBar(string source)
    {
        var now = DateTime.UtcNow;
        if (now - _lastGameBarPress < DuplicateGuard) return;
        _lastGameBarPress = now;

        GameBarButtonPressed?.Invoke();
        _journal.Write($"acer-widget ({source})");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        void Cleanup()
        {
            ButtonCaptureSession.SessionClosed -= OnButtonCaptureSessionClosed;
            UnregisterDevices();
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
