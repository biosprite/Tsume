using System.Runtime.InteropServices;
using System.Windows.Threading;
using Tsume.App.Platform.Interop;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Input;

/// <summary>
/// Intercepts the Claw's Game Bar / Quick Settings button (<c>Win+G</c> sent by firmware) when
/// reassigned away from Xbox Game Bar. Only installs a low-level keyboard hook while enabled.
/// </summary>
public sealed class GameBarButtonHook : IDisposable
{
    private static readonly Journal Errors = new("errors");

    private readonly Dispatcher _dispatcher;
    private readonly User32.LowLevelHookProc _proc;

    private nint _hook;
    private bool _enabled;
    private bool _lWinDown;
    private bool _rWinDown;
    private bool _swallowingG;
    private bool _disposed;

    /// <summary>Raised on the UI thread whenever a physical Win+G chord is intercepted.</summary>
    public event Action? Pressed;

    public GameBarButtonHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = OnKeyboardHook;
    }

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_disposed || _enabled == value) return;
            _enabled = value;
            if (value) Install();
            else Uninstall();
        }
    }

    private void Install()
    {
        if (_hook != 0 || _disposed) return;

        var module = Kernel32.GetModuleHandle(null);
        _hook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _proc, module, 0);
        if (_hook == 0)
        {
            Errors.Write($"GameBarButtonHook could not install keyboard hook (err={Marshal.GetLastWin32Error()}).");
            _enabled = false;
        }
    }

    private void Uninstall()
    {
        _lWinDown = false;
        _rWinDown = false;
        _swallowingG = false;
        var hook = Interlocked.Exchange(ref _hook, 0);
        if (hook != 0) User32.UnhookWindowsHookEx(hook);
    }

    private nint OnKeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code < User32.HC_ACTION || !_enabled)
            return User32.CallNextHookEx(_hook, code, wParam, lParam);

        var info = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);

        // Ignore injected keys (including our own mask key).
        if ((info.Flags & (User32.LLKHF_INJECTED | User32.LLKHF_LOWER_IL_INJECTED)) != 0)
            return User32.CallNextHookEx(_hook, code, wParam, lParam);

        var message = (uint)wParam;
        var isDown = message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
        var isUp = message is User32.WM_KEYUP or User32.WM_SYSKEYUP;

        // Track Win keys directly: firmware sends LWin and G back-to-back before GetAsyncKeyState updates.
        if (info.VkCode == User32.VK_LWIN)
        {
            if (isDown) _lWinDown = true;
            else if (isUp) _lWinDown = false;
        }
        else if (info.VkCode == User32.VK_RWIN)
        {
            if (isDown) _rWinDown = true;
            else if (isUp) _rWinDown = false;
        }
        else if (info.VkCode == User32.VK_G)
        {
            if (isDown)
            {
                var winDown = _lWinDown || _rWinDown
                    || User32.GetAsyncKeyState(User32.VK_LWIN) < 0
                    || User32.GetAsyncKeyState(User32.VK_RWIN) < 0;
                var otherMod = (info.Flags & User32.LLKHF_ALTDOWN) != 0
                    || User32.GetAsyncKeyState(User32.VK_CONTROL) < 0
                    || User32.GetAsyncKeyState(User32.VK_SHIFT) < 0;

                if (winDown && !otherMod)
                {
                    if (!_swallowingG)
                    {
                        _swallowingG = true;
                        // Mask LWin so releasing it after we swallow G does not pop open the Start menu.
                        User32.SendChord(User32.VK_MENU_MASK);
                        _dispatcher.BeginInvoke(DispatcherPriority.Send, () => Pressed?.Invoke());
                    }
                    return 1;
                }
            }
            else if (isUp && _swallowingG)
            {
                _swallowingG = false;
                return 1;
            }
        }

        return User32.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        Uninstall();
    }
}
