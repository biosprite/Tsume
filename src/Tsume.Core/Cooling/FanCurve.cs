using System.Text.Json.Serialization;

namespace Tsume.Core.Cooling;

/// <summary>From <paramref name="FromCelsius"/> up, a curve point may not be slower than <paramref name="MinSpeed"/>.</summary>
public sealed record FanFloor(int FromCelsius, int MinSpeed);

/// <summary>
/// What a machine's custom fan curve may hold. Part of the <see cref="Devices.DeviceProfile"/>; the helper clamps
/// every curve to it, and the panel uses the same rules so what it draws is what gets written.
/// </summary>
public sealed record FanCurveLimits
{
    /// <summary>The highest speed a point may hold, in the firmware's own units (not percent on every board): the top of the graph.</summary>
    public required int MaxSpeed { get; init; }

    /// <summary>Roughly the fan's RPM at <see cref="MaxSpeed"/>, for labeling the graph. An estimate, not a reading.</summary>
    public required int MaxRpm { get; init; }

    /// <summary>The slowest speeds allowed as it gets hotter, coolest first. Empty: any speed at any temperature.</summary>
    public IReadOnlyList<FanFloor> Floors { get; init; } = [];

    /// <summary>Custom curve temperatures (°C, coolest first) written for this chipset, or null to keep firmware defaults.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? CustomTemperatures { get; init; }

    /// <summary>The slowest speed allowed at a temperature.</summary>
    public int FloorAt(int celsius)
    {
        var floor = 0;
        foreach (var step in Floors)
            if (celsius >= step.FromCelsius) floor = Math.Max(floor, step.MinSpeed);
        return Math.Min(floor, MaxSpeed);
    }

    /// <summary>
    /// <paramref name="speeds"/> made legal: each within its floor and <see cref="MaxSpeed"/>, and never slower
    /// than the point before it (a hotter point is raised, never a cooler one lowered).
    /// </summary>
    /// <param name="stock">
    /// The firmware's own speeds, if known. A floor never asks for more than they give at that point, so the
    /// stock curve is always legal.
    /// </param>
    /// <exception cref="ArgumentException">The lists differ in length.</exception>
    public IReadOnlyList<int> Clamp(IReadOnlyList<int> temperatures, IReadOnlyList<int> speeds, IReadOnlyList<int>? stock = null)
    {
        if (temperatures.Count != speeds.Count)
            throw new ArgumentException($"A curve needs {temperatures.Count} speeds, one per temperature; got {speeds.Count}.", nameof(speeds));

        var legal = new int[speeds.Count];
        var slowest = 0;
        for (var point = 0; point < legal.Length; point++)
        {
            slowest = Math.Max(slowest, MinSpeedAt(point, temperatures, stock));
            legal[point] = slowest = Math.Clamp(speeds[point], slowest, MaxSpeed);
        }
        return legal;
    }

    /// <summary>
    /// The slowest speed one point may hold on its own account: its temperature's floor, lowered to the firmware's
    /// own speed there when that is slower. <see cref="Clamp"/> also keeps it from dropping below the point before it.
    /// </summary>
    public int MinSpeedAt(int point, IReadOnlyList<int> temperatures, IReadOnlyList<int>? stock = null)
    {
        var floor = FloorAt(temperatures[point]);
        return stock is not null && point < stock.Count ? Math.Min(floor, Math.Max(0, stock[point])) : floor;
    }

    /// <summary>Linearly resamples a curve onto <paramref name="toTemperatures"/>, clamping outside the source range.</summary>
    public static int[] Sample(IReadOnlyList<int> fromTemperatures, IReadOnlyList<int> fromSpeeds, IReadOnlyList<int> toTemperatures)
    {
        var count = toTemperatures.Count;
        var result = new int[count];
        if (fromTemperatures.Count == 0 || fromSpeeds.Count != fromTemperatures.Count) return result;

        for (var i = 0; i < count; i++)
        {
            var target = toTemperatures[i];
            if (target <= fromTemperatures[0])
            {
                result[i] = fromSpeeds[0];
                continue;
            }
            if (target >= fromTemperatures[^1])
            {
                result[i] = fromSpeeds[^1];
                continue;
            }

            var speed = fromSpeeds[^1];
            for (var seg = 0; seg < fromTemperatures.Count - 1; seg++)
            {
                var low = fromTemperatures[seg];
                var high = fromTemperatures[seg + 1];
                if (target > high) continue;
                if (high <= low)
                {
                    speed = fromSpeeds[seg + 1];
                    break;
                }

                var fraction = (double)(target - low) / (high - low);
                speed = (int)Math.Round(fromSpeeds[seg] + fraction * (fromSpeeds[seg + 1] - fromSpeeds[seg]));
                break;
            }
            result[i] = speed;
        }
        return result;
    }

    /// <summary>A speed as a share of <see cref="MaxSpeed"/>, 0–100.</summary>
    public int PercentAt(int speed) => MaxSpeed <= 0 ? 0 : (int)Math.Round(100.0 * Math.Clamp(speed, 0, MaxSpeed) / MaxSpeed);

    /// <summary>Roughly the RPM a speed gives, assuming RPM rises evenly up to <see cref="MaxRpm"/>.</summary>
    public int RpmAt(int speed) => MaxSpeed <= 0 ? 0 : (int)Math.Round((double)MaxRpm * Math.Clamp(speed, 0, MaxSpeed) / MaxSpeed);

    /// <summary>Content equality; record equality compares the list by reference.</summary>
    public bool Matches(FanCurveLimits other) =>
        MaxSpeed == other.MaxSpeed
        && MaxRpm == other.MaxRpm
        && Floors.SequenceEqual(other.Floors)
        && (CustomTemperatures is null
            ? other.CustomTemperatures is null
            : other.CustomTemperatures is not null && CustomTemperatures.SequenceEqual(other.CustomTemperatures));
}

/// <summary>
/// The fan curve as the helper finds it: the temperatures and speeds currently in the firmware, and the firmware's
/// own stock temperatures and speeds.
/// </summary>
public sealed record FanCurveState
{
    /// <summary>The curve's temperature points in °C currently held by the firmware, coolest first.</summary>
    public required IReadOnlyList<int> Temperatures { get; init; }

    /// <summary>
    /// The speeds the firmware holds now: the custom curve while one is in place (on Custom, and under Max),
    /// otherwise the firmware's own.
    /// </summary>
    public required IReadOnlyList<int> Speeds { get; init; }

    /// <summary>The firmware's own speeds — what the fans follow on Auto, and what a reset returns to.</summary>
    public required IReadOnlyList<int> Stock { get; init; }

    /// <summary>Firmware's own temperatures (°C), or null when identical to <see cref="Temperatures"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? StockTemperatures { get; init; }

    [JsonIgnore]
    public IReadOnlyList<int> EffectiveStockTemperatures => StockTemperatures ?? Temperatures;

    /// <summary>
    /// False when the firmware's own speeds could not be told apart from a custom curve already in place:
    /// <see cref="Stock"/> then repeats <see cref="Speeds"/>, and must not be used to lower floors or to reset to.
    /// </summary>
    public bool StockKnown { get; init; } = true;
}
