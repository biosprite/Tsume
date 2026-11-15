using System.Windows.Input;
using System.Windows.Interop;
using Tsume.App.Platform.Interop;

namespace Tsume.App.Input;

/// <summary>The system-wide keyboard shortcut Win + Alt + Q.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int Id = 0x7A10;
    private const Key Letter = Key.Q;
    private const uint Modifiers = User32.MOD_WIN | User32.MOD_ALT | User32.MOD_NOREPEAT;

    /// <summary>A message-only window (parent HWND_MESSAGE, -3) that receives WM_HOTKEY.</summary>
    private readonly HwndSource _sink = new(new HwndSourceParameters("Tsume hotkey") { ParentWindow = new nint(-3) });

    public event Action? Pressed;

    /// <summary>False when another program already owns the shortcut.</summary>
    public bool IsRegistered { get; }

    public GlobalHotkey()
    {
        _sink.AddHook(OnMessage);
        IsRegistered = User32.RegisterHotKey(_sink.Handle, Id, Modifiers, (uint)KeyInterop.VirtualKeyFromKey(Letter));
    }

    private nint OnMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == User32.WM_HOTKEY && wParam == Id)
        {
            Pressed?.Invoke();
            handled = true;
        }

        return 0;
    }

    public void Dispose()
    {
        if (IsRegistered) User32.UnregisterHotKey(_sink.Handle, Id);
        _sink.Dispose();
    }
}
