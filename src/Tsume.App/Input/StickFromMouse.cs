namespace Tsume.App.Input;

/// <summary>
/// Turns relative mouse motion from the Claw's Desktop-mode left stick into a held <see cref="PadButton"/>
/// direction. Pure and clock-free, mirroring <see cref="PadPresses"/>.
/// </summary>
public sealed class StickFromMouse
{
    /// <summary>Accumulated pixels needed on the dominant axis to register a stick tilt.</summary>
    private const int ActivateThreshold = 16;

    /// <summary>Accumulated opposing or perpendicular pixels needed to switch away from a held direction.</summary>
    private const int SwitchThreshold = 16;

    /// <summary>Cap accumulated pixels so reversing direction never has to unwind a long backlog.</summary>
    private const int MaxAccum = 48;

    /// <summary>
    /// While the stick is held, the controller reports mouse deltas every ~8–16 ms.
    /// Silence beyond this window means the stick has returned to centre.
    /// </summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMilliseconds(75);

    private PadButton _active = PadButton.None;
    private int _accX;
    private int _accY;
    private DateTime _lastMotionAt = DateTime.MinValue;

    /// <summary>Feeds one relative mouse delta and returns the currently held direction.</summary>
    public PadButton Feed(int dx, int dy, DateTime at)
    {
        if (dx == 0 && dy == 0) return Current(at);

        if (_lastMotionAt != DateTime.MinValue && at - _lastMotionAt > IdleTimeout)
            Reset();

        _lastMotionAt = at;

        if (_active != PadButton.None && IsContinuingActive(dx, dy))
        {
            _accX = 0;
            _accY = 0;
            return _active;
        }

        _accX = Math.Clamp(_accX + dx, -MaxAccum, MaxAccum);
        _accY = Math.Clamp(_accY + dy, -MaxAccum, MaxAccum);

        var threshold = _active == PadButton.None ? ActivateThreshold : SwitchThreshold;
        var candidate = Dominant(_accX, _accY, threshold);
        if (candidate != PadButton.None)
        {
            _active = candidate;
            _accX = 0;
            _accY = 0;
        }

        return _active;
    }

    /// <summary>The currently held direction at <paramref name="at"/>, clearing it once motion stops.</summary>
    public PadButton Current(DateTime at)
    {
        if (_active != PadButton.None && at - _lastMotionAt > IdleTimeout)
            Reset();

        return _active;
    }

    public void Reset()
    {
        _active = PadButton.None;
        _accX = 0;
        _accY = 0;
        _lastMotionAt = DateTime.MinValue;
    }

    private bool IsContinuingActive(int dx, int dy) => _active switch
    {
        PadButton.Up    => dy < 0 && -dy >= Math.Abs(dx),
        PadButton.Down  => dy > 0 &&  dy >= Math.Abs(dx),
        PadButton.Left  => dx < 0 && -dx >= Math.Abs(dy),
        PadButton.Right => dx > 0 &&  dx >= Math.Abs(dy),
        _               => false,
    };

    private static PadButton Dominant(int x, int y, int threshold)
    {
        var absX = Math.Abs(x);
        var absY = Math.Abs(y);

        if (absX >= absY)
        {
            if (absX < threshold) return PadButton.None;
            return x < 0 ? PadButton.Left : PadButton.Right;
        }

        if (absY < threshold) return PadButton.None;
        return y < 0 ? PadButton.Up : PadButton.Down;
    }
}
