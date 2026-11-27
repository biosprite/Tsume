using Tsume.Core.Cooling;
using Tsume.Core.Ipc;
using Tsume.Core.Performance;
using Tsume.Core.Telemetry;

namespace Tsume.Tests;

public sealed class HelperJsonTests
{
    public static TheoryData<HelperRequest, string> AllRequests => new()
    {
        { new GetSnapshot(), "snapshot.get" },
        { new GetReadings(), "readings.get" },
        { new SetPerformance(PerformanceMode.Manual, new PowerLimits(20, 30), new PowerLimits(15, 25, 35)), "performance.set" },
        { new SetFan(FanMode.Max), "fan.set" },
        { new GetFanCurve(), "fan.curve.get" },
        { new SetFanCurve([20, 35, 50, 75, 100]), "fan.curve.set" },
        { new SetChargeLimit(80), "charge.set" },
        { new StartPanel(Background: true, Settings: false, Lighting: true, OptiScaler: false, Stay: true, Rtss: true), "panel.start" },
        { new GetLog(), "log.get" },
        { new SetRtssProfile(FramerateLimit: 60, PositionX: 10, PositionY: 20, ZoomRatio: 2, EnableFill: 1, FillColor: 0x80000000u), "rtss.profile.set" },
        { new SetRtssExclusions(["game.exe", "launcher.exe"]), "rtss.exclusions.set" },
        { new StopRtss(), "rtss.stop" },
        { new StartRtss(), "rtss.start" },
        { new GetRtssStartup(), "rtss.startup.get" },
        { new SetRtssStartup(true), "rtss.startup.set" },
        { new SetRtssOverlay("tsume", "dark"), "rtss.overlay.set" },
        { new GetHardwareInventory(IncludeTimedLoad: false, PollOnly: true), "discovery.inventory" },
        { new GetHardwareState(PollOnly: true), "discovery.state" },
        { new RunFirmwareCheck(FirmwareCheckKind.WriteCheck, PollOnly: false), "firmware.check" },
        { new GetCpuBoost(), "cpuboost.get" },
        { new SetCpuBoost(false), "cpuboost.set" },
    };

    [Theory]
    [MemberData(nameof(AllRequests))]
    public void Request_RoundTripsWithTypeDiscriminator(HelperRequest request, string expectedOp)
    {
        var json = HelperJson.Write(request);
        Assert.Contains($"\"op\":\"{expectedOp}\"", json, StringComparison.Ordinal);

        var deserialized = HelperJson.ReadRequest(json);
        Assert.NotNull(deserialized);
        Assert.Equal(request.GetType(), deserialized.GetType());

        // Compare via re-serialization so collection properties (IReadOnlyList<T>) compare by value.
        Assert.Equal(json, HelperJson.Write(deserialized));
    }

    [Fact]
    public void Reply_SuccessSnapshotRoundTripsAndOmitsUnreachable()
    {
        var reply = HelperReply.Success(new HardwareSnapshot
        {
            Mode = PerformanceMode.Balanced,
            Limits = new PowerLimits(25, 37),
            Fan = FanMode.Auto,
            ChargeLimit = 80,
            CpuTemperature = 62,
            FanRpm = 3200,
            FanSpeed = 48,
            FirmwareVersion = "1T91EMS1.103",
        }) with { Unreachable = true };

        var json = HelperJson.Write(reply);
        Assert.DoesNotContain("unreachable", json, StringComparison.OrdinalIgnoreCase);

        var parsed = HelperJson.ReadReply(json);
        Assert.NotNull(parsed);
        Assert.True(parsed.Ok);
        Assert.False(parsed.Unreachable);
        Assert.Equal(reply.Snapshot, parsed.Snapshot);
    }

    [Fact]
    public void Reply_ReadingsFanCurveAndErrorsRoundTrip()
    {
        var readingsReply = HelperReply.Success(new LiveReadings(CpuTemperature: 58, FanRpm: 2800, FanSpeed: 40));
        var parsedReadings = HelperJson.ReadReply(HelperJson.Write(readingsReply));
        Assert.Equal(readingsReply, parsedReadings);

        var notSupported = HelperReply.NotSupported("Unknown Handheld");
        var parsedNotSupported = HelperJson.ReadReply(HelperJson.Write(notSupported));
        Assert.Equal(notSupported, parsedNotSupported);
        Assert.True(parsedNotSupported!.Unsupported);

        var curveReply = new HelperReply
        {
            Ok = true,
            FanCurve = new FanCurveState
            {
                Temperatures = [40, 60, 80],
                Speeds = [25, 50, 85],
                Stock = [20, 45, 80],
                StockTemperatures = [45, 65, 85],
                StockKnown = true,
            },
            RtssStartsWithWindows = true,
            CpuBoostEnabled = false,
            Pending = true,
        };

        var json = HelperJson.Write(curveReply);
        var parsedCurve = HelperJson.ReadReply(json);
        Assert.NotNull(parsedCurve?.FanCurve);
        Assert.Equal([40, 60, 80], parsedCurve.FanCurve.Temperatures);
        Assert.Equal([25, 50, 85], parsedCurve.FanCurve.Speeds);
        Assert.Equal([45, 65, 85], parsedCurve.FanCurve.EffectiveStockTemperatures);
        Assert.True(parsedCurve.RtssStartsWithWindows);
        Assert.False(parsedCurve.CpuBoostEnabled);
        Assert.True(parsedCurve.Pending);
    }
}
