namespace Tsume.App.Input;

/// <summary>
/// Turns successive pad readings into presses: the summon chord, each button as it goes down,
/// and auto-repeat on a held direction. Free of hardware and clock, so it can be unit-tested.
/// </summary>
public sealed class PadPresses
{
    /// <summary>Hold both of these to open or close the panel.</summary>
    private const PadButton SummonChord = PadButton.View | PadButton.Menu;
    private const PadButton Directions = PadButton.Up | PadButton.Down | PadButton.Left | PadButton.Right;

    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan RepeatEvery = TimeSpan.FromMilliseconds(85);

    private PadButton _before = PadButton.None;
    private DateTime _nextRepeat = DateTime.MaxValue;

    /// <param name="now">Every button held at this reading.</param>
    /// <param name="navigating">Whether the panel is open and wants presses.</param>
    /// <param name="at">When the reading was taken.</param>
    /// <returns>Whether the chord was just completed, and otherwise the buttons to report as pressed.</returns>
    public (bool Summoned, PadButton Pressed) Step(PadButton now, bool navigating, DateTime at)
    {
        var fresh = now & ~_before;
        var chordJustCompleted = (now & SummonChord) == SummonChord && (_before & SummonChord) != SummonChord;
        _before = now;

        // Reset the repeat timer whenever the panel is not navigating: a stale one would fire
        // at once if the panel opened with the stick already held.
        if (chordJustCompleted || !navigating) _nextRepeat = DateTime.MaxValue;

        if (chordJustCompleted) return (true, PadButton.None);
        if (!navigating) return (false, PadButton.None);

        var pressed = fresh & ~SummonChord;

        var held = now & Directions;
        if ((fresh & Directions) != 0)
        {
            _nextRepeat = at + RepeatDelay;
        }
        else if (held != 0 && at >= _nextRepeat)
        {
            pressed |= held;
            _nextRepeat = at + RepeatEvery;
        }

        return (false, pressed);
    }
}
