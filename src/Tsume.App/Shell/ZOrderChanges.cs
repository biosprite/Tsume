using Tsume.App.Platform.Interop;

namespace Tsume.App.Shell;

/// <summary>
/// Tells the UI thread whenever the stacking order of top-level windows changes, in any program.
/// Start it on the UI thread: Windows delivers each change there as the thread pumps messages.
/// </summary>
internal sealed class ZOrderChanges
{
    /// <summary>Kept in a field: Windows calls it for as long as the hook lives.</summary>
    private readonly User32.WinEventProc _callback;
    private nint _hook;

    public event Action? Changed;

    public ZOrderChanges() => _callback = (_, _, _, _, _, _, _) => Changed?.Invoke();

    public void Start()
    {
        if (_hook == 0)
            _hook = User32.SetWinEventHook(
                User32.EVENT_OBJECT_REORDER, User32.EVENT_OBJECT_REORDER, 0, _callback, 0, 0, User32.WINEVENT_OUTOFCONTEXT);
    }

    public void Stop()
    {
        if (_hook != 0) User32.UnhookWinEvent(_hook);
        _hook = 0;
    }
}
