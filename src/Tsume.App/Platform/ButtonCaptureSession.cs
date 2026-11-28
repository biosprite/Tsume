using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Tsume.App.Platform.Interop;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Platform;

/// <summary>
/// Passive, read-only multi-source input timeline recorder for discovering dedicated handheld buttons.
/// Simultaneously watches:
///   1. Vendor WMI event classes (e.g. MSI_Event, AsusAtkWmiEvent, plus any discovered WMI event classes)
///   2. Low-level keyboard hook (WH_KEYBOARD_LL — never swallows keys)
///   3. Raw Input HID reports (Consumer Control, Gamepad, and discovered vendor usage pages >= 0xFF00)
///   4. XInput button bitmask transitions (slots 0..3)
/// All events share one monotonic millisecond clock.
/// </summary>
public sealed class ButtonCaptureSession : IDisposable
{
    private static readonly string[] DefaultEventClasses =
    [
        "MSI_Event",
        "AsusAtkWmiEvent",
        "LENOVO_GAMEZONE_THERMAL_MODE_EVENT",
        "LENOVO_GAMEZONE_SMART_FAN_MODE_EVENT",
    ];

    private readonly Dispatcher _dispatcher;
    private readonly HwndSource? _hwndSource;
    private readonly nint _hwnd;
    private readonly List<ManagementEventWatcher> _wmiWatchers = [];
    private readonly List<User32.RAWINPUTDEVICE> _registeredRawDevices = [];
    private readonly Dictionary<nint, string> _rawDevicePaths = [];
    private readonly Dictionary<nint, string> _lastRawHidPayload = [];
    private readonly Dictionary<nint, (long WindowStartMs, int WindowCount, long LastEmitMs)> _rawRate = [];
    private readonly User32.LowLevelHookProc _kbdProc;
    private readonly DispatcherTimer _xinputTimer;
    private readonly ushort[] _lastXInputButtons = new ushort[XInput.MaxControllers];
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    private nint _kbdHook;
    private bool _disposed;
    private volatile string _activePromptId = "watch";

    /// <summary>Raised when a capture session finishes disposing and releases its Raw Input registrations.</summary>
    public static event Action? SessionClosed;

    public event Action<CapturedButtonEvent>? EventCaptured;

    public ButtonCaptureSession(
        Dispatcher dispatcher,
        nint windowHandle,
        IEnumerable<(string Namespace, string ClassName)>? discoveredWmiEventClasses = null,
        IEnumerable<ushort>? discoveredVendorUsagePages = null)
    {
        _dispatcher = dispatcher;
        _hwnd = windowHandle;
        _kbdProc = OnKeyboardHook;

        // 1. WMI event watchers (best effort; non-existent classes are silently skipped).
        var seenWmi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eventTargets = new List<(string Namespace, string ClassName)>();

        foreach (var cls in DefaultEventClasses)
        {
            if (seenWmi.Add($@"root\WMI|{cls}"))
                eventTargets.Add((@"root\WMI", cls));
        }

        if (discoveredWmiEventClasses is not null)
        {
            foreach (var (ns, cls) in discoveredWmiEventClasses)
            {
                if (string.IsNullOrWhiteSpace(cls)) continue;
                var effectiveNs = string.IsNullOrWhiteSpace(ns) ? @"root\WMI" : ns;
                if (seenWmi.Add($"{effectiveNs}|{cls}"))
                    eventTargets.Add((effectiveNs, cls));
            }
        }

        foreach (var (ns, cls) in eventTargets)
            TryWatchWmiEvent(ns, cls);

        // 2. Passive low-level keyboard hook.
        var module = Kernel32.GetModuleHandle(null);
        _kbdHook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _kbdProc, module, 0);

        // 3. Raw Input for Consumer Control, Gamepad, and vendor HID pages.
        if (_hwnd != 0)
        {
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);
            RegisterRawInput(discoveredVendorUsagePages);
        }

        // 4. XInput button edge watcher (~60 Hz).
        for (var slot = 0; slot < XInput.MaxControllers; slot++)
        {
            if (XInput.XInputGetState((uint)slot, out var st) == XInput.ERROR_SUCCESS)
                _lastXInputButtons[slot] = st.Buttons;
        }

        _xinputTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _xinputTimer.Tick += (_, _) => PollXInput();
        _xinputTimer.Start();
    }

    public string ActivePromptId
    {
        get => _activePromptId;
        set => _activePromptId = string.IsNullOrWhiteSpace(value) ? "watch" : value;
    }

    private long ElapsedMs => (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;

    private void TryWatchWmiEvent(string wmiNamespace, string className)
    {
        ManagementEventWatcher? watcher = null;
        try
        {
            watcher = new ManagementEventWatcher(wmiNamespace, $"SELECT * FROM {className}");
            watcher.EventArrived += (_, e) => OnWmiEvent(wmiNamespace, className, e.NewEvent);
            watcher.Start();
            _wmiWatchers.Add(watcher);
            watcher = null;
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    private void OnWmiEvent(string wmiNamespace, string className, ManagementBaseObject ev)
    {
        using (ev)
        {
            if (_disposed) return;

            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["namespace"] = wmiNamespace,
                ["class"] = className,
            };
            var parts = new List<string>();

            foreach (PropertyData prop in ev.Properties)
            {
                if (prop.Name.StartsWith("__", StringComparison.Ordinal)) continue;
                if (prop.Name.Equals("SECURITY_DESCRIPTOR", StringComparison.OrdinalIgnoreCase)) continue;

                var valueStr = prop.Value switch
                {
                    null => "null",
                    byte b => $"0x{b:X2} ({b})",
                    ushort u16 => $"0x{u16:X4} ({u16})",
                    uint u32 => $"0x{u32:X8} (low=0x{u32 & 0xFF:X2})",
                    int i32 => $"0x{i32:X8} (low=0x{i32 & 0xFF:X2})",
                    byte[] bytes => Convert.ToHexString(bytes.AsSpan(0, Math.Min(32, bytes.Length))),
                    ushort[] u16Arr => "[" + string.Join(", ", u16Arr.Take(16).Select(v => $"0x{v:X4}")) + "]",
                    uint[] u32Arr => "[" + string.Join(", ", u32Arr.Take(16).Select(v => $"0x{v:X8}")) + "]",
                    int[] i32Arr => "[" + string.Join(", ", i32Arr.Take(16).Select(v => $"0x{v:X8}")) + "]",
                    _ => $"{prop.Value}",
                };

                fields[prop.Name] = valueStr;
                if (!prop.Name.Equals("TIME_CREATED", StringComparison.OrdinalIgnoreCase))
                    parts.Add($"{prop.Name}={valueStr}");
            }

            Emit("wmi", $"{className}: {string.Join(", ", parts)}", fields);
        }
    }

    private nint OnKeyboardHook(int code, nint wParam, nint lParam)
    {
        if (!_disposed && code >= User32.HC_ACTION)
        {
            var info = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);
            var msg = (uint)wParam;
            var isDown = msg is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
            var isUp = msg is User32.WM_KEYUP or User32.WM_SYSKEYUP;

            if (isDown || isUp)
            {
                var keyName = KeyInterop.KeyFromVirtualKey((int)info.VkCode).ToString();
                var injected = (info.Flags & User32.LLKHF_INJECTED) != 0;
                var win = User32.GetAsyncKeyState(User32.VK_LWIN) < 0 || User32.GetAsyncKeyState(User32.VK_RWIN) < 0;
                var ctrl = User32.GetAsyncKeyState(User32.VK_CONTROL) < 0;
                var alt = (info.Flags & User32.LLKHF_ALTDOWN) != 0;
                var shift = User32.GetAsyncKeyState(User32.VK_SHIFT) < 0;

                var mods = new List<string>();
                if (win) mods.Add("Win");
                if (ctrl) mods.Add("Ctrl");
                if (alt) mods.Add("Alt");
                if (shift) mods.Add("Shift");
                var modText = mods.Count > 0 ? string.Join('+', mods) + "+" : "";

                var fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["event"] = isDown ? "down" : "up",
                    ["vk"] = $"0x{info.VkCode:X2}",
                    ["key"] = keyName,
                    ["scan"] = $"0x{info.ScanCode:X2}",
                    ["flags"] = $"0x{info.Flags:X2}",
                    ["injected"] = injected ? "true" : "false",
                    ["modifiers"] = string.Join('+', mods),
                };

                Emit("keyboard_ll", $"{(isDown ? "DOWN" : "UP")} {modText}{keyName} (vk=0x{info.VkCode:X2}, scan=0x{info.ScanCode:X2}{(injected ? ", injected" : "")})", fields);
            }
        }

        return User32.CallNextHookEx(_kbdHook, code, wParam, lParam);
    }

    private void RegisterRawInput(IEnumerable<ushort>? vendorUsagePages)
    {
        var devices = new List<User32.RAWINPUTDEVICE>
        {
            new() { UsagePage = User32.HID_USAGE_PAGE_CONSUMER, Usage = User32.HID_USAGE_CONSUMER_CONTROL, Flags = User32.RIDEV_INPUTSINK, Target = _hwnd },
            new() { UsagePage = User32.HID_USAGE_PAGE_GENERIC, Usage = User32.HID_USAGE_GENERIC_GAMEPAD, Flags = User32.RIDEV_INPUTSINK, Target = _hwnd },
            new() { UsagePage = User32.HID_USAGE_PAGE_GENERIC, Usage = User32.HID_USAGE_GENERIC_JOYSTICK, Flags = User32.RIDEV_INPUTSINK, Target = _hwnd },
        };

        var seenPages = new HashSet<ushort>();
        if (vendorUsagePages is not null)
        {
            foreach (var page in vendorUsagePages)
            {
                if (page >= 0xFF00 && seenPages.Add(page))
                {
                    devices.Add(new User32.RAWINPUTDEVICE
                    {
                        UsagePage = page,
                        Usage = 0,
                        Flags = User32.RIDEV_INPUTSINK | User32.RIDEV_PAGEONLY,
                        Target = _hwnd,
                    });
                }
            }
        }

        // Register one by one so an unsupported usage page never breaks the others.
        var size = (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>();
        foreach (var dev in devices)
        {
            if (User32.RegisterRawInputDevices([dev], 1, size))
                _registeredRawDevices.Add(dev);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (!_disposed && msg == User32.WM_INPUT)
            HandleRawInput(lParam);

        return 0;
    }

    private void HandleRawInput(nint rawInputHandle)
    {
        var headerSize = (uint)Marshal.SizeOf<User32.RAWINPUTHEADER>();
        uint size = 0;
        if (User32.GetRawInputData(rawInputHandle, User32.RID_INPUT, (byte[]?)null, ref size, headerSize) != 0 || size == 0 || size > 4096)
            return;

        var buffer = new byte[size];
        if (User32.GetRawInputData(rawInputHandle, User32.RID_INPUT, buffer, ref size, headerSize) != size)
            return;

        if (buffer.Length < (int)headerSize + 8) return;

        var header = MemoryMarshal.Read<User32.RAWINPUTHEADER>(buffer);
        if (header.Type != User32.RIM_TYPEHID) return;

        // RAWHID starts immediately after RAWINPUTHEADER: dwSizeHid (4), dwCount (4), bRawData[...].
        var offset = (int)headerSize;
        var reportSize = BitConverter.ToInt32(buffer, offset);
        var count = BitConverter.ToInt32(buffer, offset + 4);
        if (reportSize <= 0 || count <= 0) return;

        var dataOffset = offset + 8;
        var totalBytes = Math.Min((long)reportSize * count, 64L);
        var avail = (int)Math.Min(buffer.Length - dataOffset, totalBytes);
        if (avail <= 0) return;

        var deviceHandle = header.Device;
        var hex = Convert.ToHexString(buffer, dataOffset, avail);

        // Suppress unchanged repeat reports (e.g. idle 125-500 Hz gamepad polling).
        if (_lastRawHidPayload.TryGetValue(deviceHandle, out var previousHex)
            && string.Equals(previousHex, hex, StringComparison.Ordinal))
            return;
        _lastRawHidPayload[deviceHandle] = hex;

        // Throttle continuously streaming devices (e.g. IMU/gyro/noisy analog axes) so they cannot starve
        // the UI or bury real button press/release transitions.
        var nowMs = ElapsedMs;
        if (_rawRate.TryGetValue(deviceHandle, out var rate))
        {
            if (nowMs - rate.WindowStartMs >= 1000)
                rate = (nowMs, 1, nowMs);
            else if (rate.WindowCount >= 10 && nowMs - rate.LastEmitMs < 100)
                return;
            else
                rate = (rate.WindowStartMs, rate.WindowCount + 1, nowMs);
        }
        else
        {
            rate = (nowMs, 1, nowMs);
        }
        _rawRate[deviceHandle] = rate;

        var devPath = GetCachedRawDevicePath(deviceHandle);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["device"] = devPath,
            ["size"] = reportSize.ToString(),
            ["count"] = count.ToString(),
            ["hex"] = hex,
        };

        Emit("raw_hid", $"HID {devPath} ({reportSize}B): {hex}", fields);
    }

    private string GetCachedRawDevicePath(nint device)
    {
        if (_rawDevicePaths.TryGetValue(device, out var cached)) return cached;

        var path = GetRawDevicePath(device);
        _rawDevicePaths[device] = path;
        return path;
    }

    private static string GetRawDevicePath(nint device)
    {
        uint chars = 0;
        if (User32.GetRawInputDeviceInfo(device, User32.RIDI_DEVICENAME, null, ref chars) != 0 || chars == 0 || chars > 1024)
            return "unknown";

        var name = new char[chars];
        if (User32.GetRawInputDeviceInfo(device, User32.RIDI_DEVICENAME, name, ref chars) == uint.MaxValue)
            return "unknown";

        return PrivacyScrubber.ScrubDevicePath(new string(name).TrimEnd('\0'));
    }

    private void PollXInput()
    {
        if (_disposed) return;

        for (var slot = 0; slot < XInput.MaxControllers; slot++)
        {
            if (XInput.XInputGetState((uint)slot, out var state) != XInput.ERROR_SUCCESS) continue;

            var now = state.Buttons;
            var prev = _lastXInputButtons[slot];
            if (now == prev) continue;

            var pressed = (ushort)(now & ~prev);
            var released = (ushort)(prev & ~now);
            _lastXInputButtons[slot] = now;

            if (pressed != 0)
            {
                Emit("xinput", $"Slot {slot} DOWN 0x{pressed:X4} ({DescribeXInputMask(pressed)})", new()
                {
                    ["slot"] = slot.ToString(),
                    ["event"] = "down",
                    ["mask"] = $"0x{pressed:X4}",
                });
            }

            if (released != 0)
            {
                Emit("xinput", $"Slot {slot} UP 0x{released:X4} ({DescribeXInputMask(released)})", new()
                {
                    ["slot"] = slot.ToString(),
                    ["event"] = "up",
                    ["mask"] = $"0x{released:X4}",
                });
            }
        }
    }

    private static string DescribeXInputMask(ushort mask)
    {
        var names = new List<string>();
        if ((mask & 0x0001) != 0) names.Add("DPadUp");
        if ((mask & 0x0002) != 0) names.Add("DPadDown");
        if ((mask & 0x0004) != 0) names.Add("DPadLeft");
        if ((mask & 0x0008) != 0) names.Add("DPadRight");
        if ((mask & 0x0010) != 0) names.Add("Start");
        if ((mask & 0x0020) != 0) names.Add("Back");
        if ((mask & 0x0040) != 0) names.Add("LS");
        if ((mask & 0x0080) != 0) names.Add("RS");
        if ((mask & 0x0100) != 0) names.Add("LB");
        if ((mask & 0x0200) != 0) names.Add("RB");
        if ((mask & 0x1000) != 0) names.Add("A");
        if ((mask & 0x2000) != 0) names.Add("B");
        if ((mask & 0x4000) != 0) names.Add("X");
        if ((mask & 0x8000) != 0) names.Add("Y");
        return names.Count > 0 ? string.Join('+', names) : $"0x{mask:X4}";
    }

    private void Emit(string source, string summary, Dictionary<string, string> fields)
    {
        var ev = new CapturedButtonEvent
        {
            PromptId = _activePromptId,
            TimestampUtc = DateTimeOffset.UtcNow,
            ElapsedMs = ElapsedMs,
            Source = source,
            Summary = summary,
            Fields = fields,
        };

        EventCaptured?.Invoke(ev);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _xinputTimer.Stop();

        var hook = Interlocked.Exchange(ref _kbdHook, 0);
        if (hook != 0) User32.UnhookWindowsHookEx(hook);

        var removedRawDevices = _registeredRawDevices.Count > 0;
        if (removedRawDevices)
        {
            var size = (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>();
            foreach (var dev in _registeredRawDevices)
            {
                var remove = dev with { Flags = User32.RIDEV_REMOVE | (dev.Flags & User32.RIDEV_PAGEONLY), Target = 0 };
                User32.RegisterRawInputDevices([remove], 1, size);
            }
            _registeredRawDevices.Clear();
        }

        _hwndSource?.RemoveHook(WndProc);

        foreach (var watcher in _wmiWatchers)
        {
            try { watcher.Stop(); } catch (Exception) { }
            watcher.Dispose();
        }
        _wmiWatchers.Clear();

        if (removedRawDevices)
            SessionClosed?.Invoke();
    }
}
