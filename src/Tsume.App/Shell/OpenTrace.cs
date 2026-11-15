using System.Diagnostics;
using System.Windows.Media;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Shell;

/// <summary>
/// Development aid. With TSUME_TRACE set, each opening of the panel logs a line to
/// %LOCALAPPDATA%\Tsume\open.log: time to first movement, frames drawn, late frames, display clock rate.
/// TSUME_TRACE=ab also leaves <see cref="DisplayClock"/> off on every other opening, for comparison.
/// </summary>
internal static class OpenTrace
{
    /// <summary>Milliseconds. A frame is due every 16.7 ms at 60 fps; a longer gap than this means one was dropped.</summary>
    private const double LateFrame = 25;

    private static readonly string? Mode = Environment.GetEnvironmentVariable("TSUME_TRACE");
    private static readonly Journal Journal = new("open");
    private static readonly TimeSpan Watched = TimeSpan.FromMilliseconds(900);

    private static Stopwatch? _clock;
    private static double _moving;
    private static long _summoned;
    private static int _openings;

    /// <summary>Whether this opening should keep the display awake (<see cref="DisplayClock"/>). Always, unless comparing.</summary>
    public static bool PaceThisTime { get; private set; } = true;

    /// <summary>Call, from any thread, the moment a hardware button asks for the panel.</summary>
    public static void Summoned() => Volatile.Write(ref _summoned, Stopwatch.GetTimestamp());

    /// <summary>Call at the very start of an opening.</summary>
    public static void Begin()
    {
        if (Mode is null) return;

        PaceThisTime = Mode != "ab" || _openings++ % 2 == 0;
        var pacing = PaceThisTime ? "on" : "off";

        // How long the request took to reach the window, when a button made it.
        var summoned = Interlocked.Exchange(ref _summoned, 0);
        var waited = summoned == 0 || Stopwatch.GetElapsedTime(summoned).TotalMilliseconds > 500
            ? ""
            : $"button to open {Stopwatch.GetElapsedTime(summoned).TotalMilliseconds:0} ms, ";

        var clock = _clock = Stopwatch.StartNew();
        var frames = new List<double>();
        _moving = 0;

        var display = Task.Run(() => WatchDisplayClock(clock));

        EventHandler? onFrame = null;
        onFrame = (_, _) =>
        {
            frames.Add(clock.Elapsed.TotalMilliseconds);
            if (clock.Elapsed < Watched) return;

            CompositionTarget.Rendering -= onFrame;

            var late = 0;
            for (var i = 1; i < frames.Count; i++)
                if (frames[i] > _moving && frames[i] - frames[i - 1] > LateFrame) late++;

            var summary = $"{waited}display kept awake: {pacing}, moving after {_moving:0} ms, {frames.Count} frames, {late} late, {RightOfWay.Standing}";
            display.ContinueWith(done => Journal.Write($"{summary}, {done.Result}"));
        };

        CompositionTarget.Rendering += onFrame;
    }

    /// <summary>Call when the entrance animations start.</summary>
    public static void Moving()
    {
        if (_clock is { } clock) _moving = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Counts the display clock's ticks during one opening, and times the first few.</summary>
    private static string WatchDisplayClock(Stopwatch clock)
    {
        var ticks = 0;
        var first = new List<string>();
        var last = clock.Elapsed.TotalMilliseconds;

        while (clock.Elapsed < Watched)
        {
            DisplayClock.WaitForTick();

            var now = clock.Elapsed.TotalMilliseconds;
            if (ticks++ < 6) first.Add($"{now - last:0}");
            last = now;
        }

        return $"display clock {ticks} ticks (first gaps, ms: {string.Join(' ', first)})";
    }
}
