using Tsume.Core.Cooling;

namespace Tsume.Tests;

public sealed class FanCurveLimitsTests
{
    private static readonly FanCurveLimits SampleLimits = new()
    {
        MaxSpeed = 100,
        Floors =
        [
            new FanFloor(FromCelsius: 70, MinSpeed: 40),
            new FanFloor(FromCelsius: 80, MinSpeed: 65),
            new FanFloor(FromCelsius: 90, MinSpeed: 90),
        ],
    };

    [Theory]
    [InlineData(50, 0)]
    [InlineData(69, 0)]
    [InlineData(70, 40)]
    [InlineData(79, 40)]
    [InlineData(80, 65)]
    [InlineData(95, 90)]
    public void FloorAt_ReturnsHighestMatchingFloor(int celsius, int expected)
    {
        Assert.Equal(expected, SampleLimits.FloorAt(celsius));
    }

    [Fact]
    public void FloorAt_CapsAtMaxSpeed()
    {
        var limits = new FanCurveLimits
        {
            MaxSpeed = 50,
            Floors = [new FanFloor(60, 80)],
        };

        Assert.Equal(50, limits.FloorAt(70));
    }

    [Fact]
    public void Clamp_EnforcesFloorsMaxSpeedAndMonotonicity()
    {
        int[] temps = [40, 55, 70, 80, 90];
        int[] requested = [50, 30, 20, 60, 120];

        var clamped = SampleLimits.Clamp(temps, requested);

        // Point 0: 50 (legal).
        // Point 1: requested 30, raised to previous (50).
        // Point 2: requested 20, floor 40, raised to previous (50).
        // Point 3: requested 60, floor 65 -> 65.
        // Point 4: requested 120, capped to MaxSpeed (100).
        Assert.Equal([50, 50, 50, 65, 100], clamped);
    }

    [Fact]
    public void Clamp_LowersFloorToStockWhenStockIsSlower()
    {
        int[] temps = [60, 70, 80];
        int[] stock = [20, 30, 55];

        var clamped = SampleLimits.Clamp(temps, [20, 30, 55], stock);

        // Stock speeds are below floors (40 at 70°C, 65 at 80°C), so the stock curve remains legal.
        Assert.Equal(stock, clamped);
    }

    [Fact]
    public void Clamp_ThrowsOnMismatchedLengths()
    {
        Assert.Throws<ArgumentException>(() => SampleLimits.Clamp([50, 60], [20]));
    }

    [Fact]
    public void Sample_InterpolatesAndClampsOutOfRange()
    {
        int[] fromTemps = [40, 60, 80];
        int[] fromSpeeds = [20, 40, 80];
        int[] targetTemps = [30, 40, 50, 70, 90];

        var sampled = FanCurveLimits.Sample(fromTemps, fromSpeeds, targetTemps);

        Assert.Equal([20, 20, 30, 60, 80], sampled);
    }

    [Fact]
    public void Sample_ReturnsZeroesWhenSourceEmpty()
    {
        Assert.Equal([0, 0], FanCurveLimits.Sample([], [], [50, 70]));
    }

    [Fact]
    public void PercentAt_AndMatches_BehaveAsExpected()
    {
        var limits = new FanCurveLimits
        {
            MaxSpeed = 150,
            Floors = [new FanFloor(70, 60)],
            CustomTemperatures = [40, 55, 70, 85],
        };
        var equivalent = new FanCurveLimits
        {
            MaxSpeed = 150,
            Floors = [new FanFloor(70, 60)],
            CustomTemperatures = [40, 55, 70, 85],
        };
        var different = limits with { CustomTemperatures = [40, 60, 70, 85] };

        Assert.Equal(0, limits.PercentAt(-10));
        Assert.Equal(50, limits.PercentAt(75));
        Assert.Equal(100, limits.PercentAt(200));
        Assert.True(limits.Matches(equivalent));
        Assert.False(limits.Matches(different));
    }
}
