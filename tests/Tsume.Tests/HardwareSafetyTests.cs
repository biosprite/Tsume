using Tsume.Core.Performance;
using Tsume.Hardware;
using Tsume.Hardware.Diagnostics;
using Tsume.Hardware.Msi;

namespace Tsume.Tests;

public sealed class HardwareSafetyTests
{
    public static TheoryData<MsiBoard> AllDefinedBoards => new()
    {
        MsiBoards.Claw8,
        MsiBoards.Claw8ArcG3,
        MsiBoards.Claw8AiPlus,
        MsiBoards.Claw7AiPlus,
        MsiBoards.ClawA1M,
        MsiBoards.ClawA8,
    };

    [Theory]
    [MemberData(nameof(AllDefinedBoards))]
    public void MsiBoards_AllDefinedBoardsHaveCompleteConfiguration(MsiBoard board)
    {
        Assert.False(string.IsNullOrWhiteSpace(board.Model));
        Assert.NotNull(board.Registers);
        Assert.NotNull(board.Shift);
        Assert.NotNull(board.Shift.Codes);
        Assert.True(board.Shift.Codes.ContainsKey(PerformanceMode.Endurance));
        Assert.True(board.Shift.Codes.ContainsKey(PerformanceMode.Balanced));
        Assert.True(board.Shift.Codes.ContainsKey(PerformanceMode.Performance));
        Assert.True(board.Shift.Codes.ContainsKey(PerformanceMode.Manual));
        Assert.True(board.PowerFloor > 0);
        Assert.NotNull(board.FanTemperatures);
        Assert.Equal(6, board.FanTemperatures.Points.Length);

        Assert.NotNull(board.Profile);
        Assert.False(string.IsNullOrWhiteSpace(board.Profile.Name));
        Assert.NotEmpty(board.Profile.Modes);
        Assert.NotNull(board.Profile.Controls);
        Assert.NotNull(board.Profile.FanCurve);
        Assert.NotNull(board.Profile.FanCurve.CustomTemperatures);
        Assert.Equal(6, board.Profile.FanCurve.CustomTemperatures.Count);
    }

    [Fact]
    public void MsiBoards_Claw7AiPlusMatchesClaw8AiPlusExceptModelAndName()
    {
        var claw7 = MsiBoards.Claw7AiPlus;
        var claw8 = MsiBoards.Claw8AiPlus;

        Assert.Equal("MS-1T42", claw7.Model);
        Assert.Equal("MS-1T52", claw8.Model);
        Assert.NotEqual(claw8.Profile.Name, claw7.Profile.Name);
        Assert.Contains("Claw 7 AI+", claw7.Profile.Name, StringComparison.Ordinal);

        // Normalizing only Model and Profile.Name makes the two board records identical.
        var normalizedClaw7 = claw7 with
        {
            Model = claw8.Model,
            Profile = claw7.Profile with { Name = claw8.Profile.Name },
        };

        Assert.Equal(claw8, normalizedClaw7);
    }

    [Fact]
    public void MsiBoards_FindResolvesKnownModelsAndArcG3Variant()
    {
        Assert.Same(MsiBoards.Claw8, MsiBoards.Find(new Board("MSI", "MS-1T91"), "Intel(R) Arc(TM) G3 Extreme"));
        Assert.Same(MsiBoards.Claw8ArcG3, MsiBoards.Find(new Board("MSI", "MS-1T91"), "Intel(R) Arc(TM) G3"));
        Assert.Same(MsiBoards.Claw8AiPlus, MsiBoards.Find(new Board("MSI", "MS-1T52"), ""));
        Assert.Same(MsiBoards.Claw7AiPlus, MsiBoards.Find(new Board("MSI", "MS-1T42"), ""));
        Assert.Same(MsiBoards.ClawA1M, MsiBoards.Find(new Board("MSI", "MS-1T41"), ""));
        Assert.Same(MsiBoards.ClawA8, MsiBoards.Find(new Board("MSI", "MS-1T8K"), ""));
        Assert.Null(MsiBoards.Find(new Board("MSI", "MS-UNKNOWN"), ""));
    }

    [Fact]
    public void PackagePower_WattsComputesExpectedAverageForPositiveDelta()
    {
        // 10_000_000_000 pWh * 3600e-12 J/pWh = 36 J; over 2000 ms (2 s) = 18.0 W.
        var from = new PackagePower.Reading(PicowattHours: 5_000_000_000UL, Milliseconds: 1_000UL);
        var to = new PackagePower.Reading(PicowattHours: 15_000_000_000UL, Milliseconds: 3_000UL);

        var watts = PackagePower.Watts(from, to);

        Assert.NotNull(watts);
        Assert.Equal(18.0, watts.Value, precision: 6);
    }

    [Fact]
    public void PackagePower_WattsReturnsZeroForUnchangedEnergy()
    {
        var from = new PackagePower.Reading(PicowattHours: 100UL, Milliseconds: 1_000UL);
        var to = new PackagePower.Reading(PicowattHours: 100UL, Milliseconds: 2_000UL);

        var watts = PackagePower.Watts(from, to);

        Assert.Equal(0.0, watts);
    }

    [Fact]
    public void PackagePower_WattsReturnsNullOnCounterWraparoundOrReset()
    {
        var from = new PackagePower.Reading(PicowattHours: 50_000_000_000UL, Milliseconds: 1_000UL);
        var to = new PackagePower.Reading(PicowattHours: 1_000_000UL, Milliseconds: 2_000UL);

        Assert.Null(PackagePower.Watts(from, to));
    }

    [Fact]
    public void PackagePower_WattsReturnsNullOnNonPositiveElapsedTimeOrNullReadings()
    {
        var reading = new PackagePower.Reading(PicowattHours: 10_000_000_000UL, Milliseconds: 5_000UL);
        var earlier = new PackagePower.Reading(PicowattHours: 20_000_000_000UL, Milliseconds: 4_000UL);
        var sameTime = new PackagePower.Reading(PicowattHours: 20_000_000_000UL, Milliseconds: 5_000UL);

        Assert.Null(PackagePower.Watts(null, reading));
        Assert.Null(PackagePower.Watts(reading, null));
        Assert.Null(PackagePower.Watts(reading, earlier));
        Assert.Null(PackagePower.Watts(reading, sameTime));
    }
}
