using System.Runtime.InteropServices;
using System.Windows.Threading;
using Tsume.App.Platform.Interop;
using Tsume.Core.Devices;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Input;

/// <summary>
/// Intercepts firmware keyboard triggers for the Game Bar / Quick Settings button (e.g. Win+G or F15/F16).
/// Hook is only installed while enabled and at least one trigger is configured.
/// </summary>
public sealed class GameBarButtonHook : IDisposable
{
    private const nint MaskExtraInfo = 0x5453_554D;   // "TSUM"
    public const nint PassThroughExtraInfo = 0x5453_5550;   // "TSUP"

    private static readonly Journal Errors = new("errors");
    private static readonly Journal Log = new("buttons");

    private readonly Dispatcher _dispatcher;
    private readonly User32.LowLevelHookProc _proc;

    private KeyTrigger[] _triggers = [];
    private nint _hook;
    private bool _enabled;
    private bool _lWinDown;
    private bool _rWinDown;
    private uint? _swallowingVk;
    private bool _disposed;

    public event Action? Pressed;

    public GameBarButtonHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = OnKeyboardHook;
    }

    public void SetTriggers(IReadOnlyList<KeyTrigger> triggers)
    {
        if (_disposed) return;

        var copy = triggers.Count == 0 ? [] : triggers.ToArray();
        if (_dispatcher.CheckAccess()) ApplyTriggers(copy);
        else _dispatcher.BeginInvoke(() => ApplyTriggers(copy));
    }

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_disposed || _enabled == value) return;
            _enabled = value;
            if (_dispatcher.CheckAccess()) ReconcileHook();
            else _dispatcher.BeginInvoke(ReconcileHook);
        }
    }

    private void ApplyTriggers(KeyTrigger[] triggers)
    {
        if (_disposed) return;
        _triggers = triggers;
        _swallowingVk = null;
        ReconcileHook();
    }

    private void ReconcileHook()
    {
        if (_disposed) return;
        if (_enabled && _triggers.Length > 0) Install();
        else Uninstall();
    }

    private void Install()
    {
        if (_hook != 0 || _disposed) return;

        var module = Kernel32.GetModuleHandle(null);
        _hook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _proc, module, 0);
        if (_hook == 0)
        {
            var err = Marshal.GetLastWin32Error();
            Errors.Write($"GameBarButtonHook could not install keyboard hook (err={err}).");
            Log.Write($"gamebar hook install FAILED err={err}");
            _enabled = false;
        }
        else
        {
            Log.Write($"gamebar hook installed (0x{_hook:X}, triggers={_triggers.Length})");
        }
    }

    private void Uninstall()
    {
        _lWinDown = false;
        _rWinDown = false;
        _swallowingVk = null;
        var hook = Interlocked.Exchange(ref _hook, 0);
        if (hook != 0)
        {
            User32.UnhookWindowsHookEx(hook);
            Log.Write("gamebar hook uninstalled");
        }
    }

    private nint OnKeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code < User32.HC_ACTION || !_enabled || _triggers.Length == 0)
            return User32.CallNextHookEx(_hook, code, wParam, lParam);

        var info = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);

        // Ignore our own mask key and intentional pass-through Win+G — do not skip LLKHF_INJECTED
        // generally, because OEM/HID button drivers can emit button chords with LLKHF_INJECTED set.
        if (info.ExtraInfo is (nuint)MaskExtraInfo or (nuint)PassThroughExtraInfo || info.VkCode == User32.VK_MENU_MASK)
            return User32.CallNextHookEx(_hook, code, wParam, lParam);

        var message = (uint)wParam;
        var isDown = message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
        var isUp = message is User32.WM_KEYUP or User32.WM_SYSKEYUP;

        // Track Win keys directly: firmware sends LWin and G back-to-back before GetAsyncKeyState updates.
        if (info.VkCode == User32.VK_LWIN)
        {
            if (isDown) _lWinDown = true;
            else if (isUp) _lWinDown = false;
            LogAsync($"ll-kbd LWin {(isDown ? "down" : "up")} scan=0x{info.ScanCode:X2} flags=0x{info.Flags:X2}");
            return User32.CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (info.VkCode == User32.VK_RWIN)
        {
            if (isDown) _rWinDown = true;
            else if (isUp) _rWinDown = false;
            LogAsync($"ll-kbd RWin {(isDown ? "down" : "up")} scan=0x{info.ScanCode:X2} flags=0x{info.Flags:X2}");
            return User32.CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (isUp && _swallowingVk == info.VkCode)
        {
            _swallowingVk = null;
            return 1;
        }

        if (isDown)
        {
            var winDown = _lWinDown || _rWinDown
                || User32.GetAsyncKeyState(User32.VK_LWIN) < 0
                || User32.GetAsyncKeyState(User32.VK_RWIN) < 0;
            var altDown = (info.Flags & User32.LLKHF_ALTDOWN) != 0;
            var ctrlDown = User32.GetAsyncKeyState(User32.VK_CONTROL) < 0;
            var shiftDown = User32.GetAsyncKeyState(User32.VK_SHIFT) < 0;

            foreach (var trigger in _triggers)
            {
                if (info.VkCode != trigger.VirtualKey) continue;
                if (winDown != trigger.Win || altDown != trigger.Alt || ctrlDown != trigger.Ctrl || shiftDown != trigger.Shift)
                    continue;

                if (_swallowingVk != info.VkCode)
                {
                    _swallowingVk = info.VkCode;
                    LogAsync($"ll-kbd trigger vk=0x{info.VkCode:X2} win={winDown} SWALLOWED scan=0x{info.ScanCode:X2} flags=0x{info.Flags:X2}");

                    // Mask LWin so releasing it after we swallow a Win+<key> chord does not pop open Start.
                    if (trigger.Win) User32.SendChord(MaskExtraInfo, User32.VK_MENU_MASK);

                    _dispatcher.BeginInvoke(DispatcherPriority.Send, () => Pressed?.Invoke());
                }

                return 1;
            }

            // Log Win+<other> and F13-F24 presses so firmware chords on any BIOS can be identified.
            if (winDown || info.VkCode is >= 0x7C and <= 0x87)
                LogAsync($"ll-kbd vk=0x{info.VkCode:X2} down scan=0x{info.ScanCode:X2} flags=0x{info.Flags:X2} win={winDown}");
        }

        return User32.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static void LogAsync(string message) => ThreadPool.QueueUserWorkItem(_ => Log.Write(message));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        Uninstall();
    }
}
