using System.Buffers.Binary;
using Tsume.App.Input;
using Tsume.App.Platform;
using Tsume.Core.Cooling;
using Tsume.Core.Devices;
using Tsume.Core.Ipc;
using Tsume.Core.Lighting;
using Tsume.Core.Performance;
using Tsume.Hardware;
using Tsume.Hardware.Acer;
using Tsume.Hardware.Msi;

namespace Tsume.Tests;

public sealed class AcerHardwareTests
{
    public static TheoryData<AcerBoard> AllDefinedAcerBoards => new()
    {
        AcerBoards.Atlas8,
        AcerBoards.Atlas8ArcG3,
    };

    [Theory]
    [MemberData(nameof(AllDefinedAcerBoards))]
    public void AcerBoards_AllDefinedBoardsHaveCompleteConfiguration(AcerBoard board)
    {
        Assert.False(string.IsNullOrWhiteSpace(board.Model));
        Assert.Equal(0x1025, board.EcHidVendorId);
        Assert.Equal(0x174B, board.EcHidProductId);
        Assert.Equal(0xFF05, board.EcHidUsagePage);
        Assert.Equal([2, 3], board.FanIds);

        Assert.NotNull(board.Profile);
        Assert.False(string.IsNullOrWhiteSpace(board.Profile.Name));
        Assert.Equal(4, board.Profile.Modes.Count);
        Assert.NotNull(board.Profile.ManualRange);
        Assert.NotNull(board.Profile.ManualDefault);
        Assert.Equal([FanMode.Auto, FanMode.Custom, FanMode.Max], board.Profile.FanModes);
        Assert.NotNull(board.Profile.FanCurve);
        Assert.Equal(100, board.Profile.FanCurve.MaxSpeed);
        Assert.Equal([50, 60, 70, 80, 90, 100], board.Profile.FanCurve.CustomTemperatures);
        Assert.NotNull(board.StockFanSpeeds);
        Assert.Equal(6, board.StockFanSpeeds.Count);
        Assert.Equal([80, 100], board.Profile.ChargeLimits);

        var controls = board.Profile.Controls;
        Assert.NotNull(controls);
        Assert.True(controls.HasOemButton);
        Assert.True(controls.HasGameBarButton);
        Assert.True(controls.HasAcerHidButtons);
        Assert.Equal(OemSoftwareKind.AcerPredatorSense, controls.EffectiveOemSoftware);
        Assert.Equal(ControlsProfile.AcerHidLighting, controls.LightingProtocol);
        Assert.NotNull(controls.BackButtons);
        Assert.Equal([0x0502, 0x0489], controls.BackButtons.EffectiveVendorIds);
        Assert.Equal(8, controls.BackButtons.ProductIds.Count);
        Assert.Null(controls.BackButtons.MinFirmwareVersion);
        Assert.Equal(2, controls.BackButtons.Buttons.Count);
    }

    [Fact]
    public void AcerBoards_Atlas8AdoptsExactMsiClawExTdpLimits()
    {
        // Arc G3 Extreme SKU matches MsiBoards.Claw8 TDP profile (12/15, 25/28, 32/36 W; 8–35 / 8–45 W, PL2 >= PL1 + 2).
        Assert.Equal(MsiBoards.Claw8.Profile.Modes, AcerBoards.Atlas8.Profile.Modes);
        Assert.Equal(MsiBoards.Claw8.Profile.ManualRange, AcerBoards.Atlas8.Profile.ManualRange);
        Assert.Equal(MsiBoards.Claw8.Profile.ManualDefault, AcerBoards.Atlas8.Profile.ManualDefault);
        Assert.Equal(2, AcerBoards.Atlas8.Profile.ManualRange!.Value.MinBoostHeadroom);

        // Plain Arc G3 SKU matches MsiBoards.Claw8ArcG3 TDP profile (PL1 max 30 W, PL2 >= PL1 + 2).
        Assert.Equal(MsiBoards.Claw8ArcG3.Profile.Modes, AcerBoards.Atlas8ArcG3.Profile.Modes);
        Assert.Equal(MsiBoards.Claw8ArcG3.Profile.ManualRange, AcerBoards.Atlas8ArcG3.Profile.ManualRange);
        Assert.Equal(MsiBoards.Claw8ArcG3.Profile.ManualDefault, AcerBoards.Atlas8ArcG3.Profile.ManualDefault);
        Assert.Equal(2, AcerBoards.Atlas8ArcG3.Profile.ManualRange!.Value.MinBoostHeadroom);
    }

    [Fact]
    public void AcerBoards_FindResolvesRetailBaseboardAndSystemModelFallbackAndArcG3Variant()
    {
        // Retail Atlas 8 units report Win32_BaseBoard.Manufacturer = "PTL" (not "Acer"), Product = "F900_PTG".
        Assert.Same(AcerBoards.Atlas8, AcerBoards.Find(new Board("PTL", "F900_PTG"), "Intel(R) Arc(TM) G3 Extreme"));
        Assert.Same(AcerBoards.Atlas8ArcG3, AcerBoards.Find(new Board("PTL", "F900_PTG"), "Intel(R) Arc(TM) G3"));
        Assert.True(DeviceCatalog.IsKnownBoard(new Board("PTL", "F900_PTG")));

        // Fallback via Win32_ComputerSystem Manufacturer + Model prefix.
        Assert.Same(AcerBoards.Atlas8, AcerBoards.Find(new Board("Other", "Unknown"), "Intel(R) Arc(TM) G3 Extreme", "Acer", "Predator PA08-I51"));
        Assert.Same(AcerBoards.Atlas8ArcG3, AcerBoards.Find(new Board("Other", "Unknown"), "Intel(R) Arc(TM) G3", "Acer", "Predator PA08-I51-G6NC"));

        // Non-Acer / unrelated boards never match.
        Assert.Null(AcerBoards.Find(new Board("Micro-Star International Co., Ltd.", "MS-1T91"), "Intel(R) Arc(TM) G3 Extreme", "MSI", "Claw 8"));
        Assert.Null(AcerBoards.Find(new Board("Acer", "OtherBoard"), "Intel(R) Arc(TM) G3 Extreme", "Acer", "Nitro AN515"));
        Assert.False(DeviceCatalog.IsKnownBoard(new Board("Unknown", "NotABoard")));
    }

    [Fact]
    public void AcerDevice_SetPerformance_WritesOcProfile3BeforeMode3AndPreservesOtherFields()
    {
        var ec = new FakeAcerEcHid { UsageMode = AcerDevice.ModeBalanced };
        ec.OcProfile3[2] = 0xAA;   // f2 low byte (preserved)
        ec.OcProfile3[6] = 0xBB;   // f4 low byte (preserved)
        ec.OcProfile3[18] = 0xCC;  // f11 high byte (preserved)

        var wmi = new FakeAcerWmi();
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8);

        Assert.True(device.ReapplyOnResume);

        device.SetPerformance(PerformanceMode.Performance, new PowerLimits(25, 28));

        // Must write OC Profile 3 first, then Usage Mode 3.
        Assert.Equal(["GetMode", "GetOcProfile(3)", "SetOcProfile(3)", "SetMode(3)"], ec.Log);
        Assert.Equal(AcerDevice.ModeManual, ec.UsageMode);

        // Claw EX Performance = 32 W PL1 (f1), 36 W PL2 (f3); other bytes preserved.
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(ec.OcProfile3.AsSpan(0, 2)));
        Assert.Equal(36, BinaryPrimitives.ReadUInt16LittleEndian(ec.OcProfile3.AsSpan(4, 2)));
        Assert.Equal(0xAA, ec.OcProfile3[2]);
        Assert.Equal(0xBB, ec.OcProfile3[6]);
        Assert.Equal(0xCC, ec.OcProfile3[18]);

        Assert.Equal(PerformanceMode.Manual, device.HardwareModeFor(PerformanceMode.Performance, new PowerLimits(32, 36)));
        Assert.Equal((PerformanceMode.Manual, new PowerLimits(32, 36)), device.Performance());
    }

    [Fact]
    public void AcerDevice_SetPerformance_SanitizesAllFfProfileAfterEcPowerLoss()
    {
        var ec = new FakeAcerEcHid { UsageMode = AcerDevice.ModeBalanced };
        Array.Fill(ec.OcProfile3, (byte)0xFF);
        var wmi = new FakeAcerWmi();
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8);

        device.SetPerformance(PerformanceMode.Endurance, new PowerLimits(25, 28));

        // f1 = 12 W, f3 = 15 W; all uninitialized 0xFF fields zeroed instead of left at 0xFFFF.
        Assert.Equal(12, BinaryPrimitives.ReadUInt16LittleEndian(ec.OcProfile3.AsSpan(0, 2)));
        Assert.Equal(15, BinaryPrimitives.ReadUInt16LittleEndian(ec.OcProfile3.AsSpan(4, 2)));
        Assert.Equal(0, ec.OcProfile3[2]);
        Assert.Equal(0, ec.OcProfile3[18]);
    }

    [Fact]
    public void AcerDevice_SetPerformance_ThrowsInsteadOfClobberingWhenGetOcProfileFails()
    {
        var ec = new FakeAcerEcHid { UsageMode = AcerDevice.ModeBalanced, FailGetProfile = true };
        ec.OcProfile3[2] = 0xAA;
        var wmi = new FakeAcerWmi();
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8);

        Assert.Throws<FirmwareException>(() => device.SetPerformance(PerformanceMode.Performance, new PowerLimits(25, 28)));
        Assert.DoesNotContain("SetOcProfile(3)", ec.Log);
        Assert.DoesNotContain("SetMode(3)", ec.Log);
        Assert.Equal(0xAA, ec.OcProfile3[2]);
    }

    [Fact]
    public void AcerDevice_SetPerformance_RollsBackProfileIfModeSwitchFails()
    {
        var ec = new FakeAcerEcHid { UsageMode = AcerDevice.ModeBalanced, FailSetMode = true };
        BinaryPrimitives.WriteUInt16LittleEndian(ec.OcProfile3.AsSpan(0, 2), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(ec.OcProfile3.AsSpan(4, 2), 30);
        var wmi = new FakeAcerWmi();
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8);

        Assert.Throws<FirmwareException>(() => device.SetPerformance(PerformanceMode.Performance, new PowerLimits(25, 28)));

        Assert.Equal(20, BinaryPrimitives.ReadUInt16LittleEndian(ec.OcProfile3.AsSpan(0, 2)));
        Assert.Equal(30, BinaryPrimitives.ReadUInt16LittleEndian(ec.OcProfile3.AsSpan(4, 2)));
    }

    [Fact]
    public void AcerDevice_Performance_ReportsBalancedResetAfterSleep()
    {
        var ec = new FakeAcerEcHid { UsageMode = AcerDevice.ModeBalanced };
        var wmi = new FakeAcerWmi();
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8);

        var (mode, limits) = device.Performance();

        Assert.Equal(PerformanceMode.Balanced, mode);
        Assert.Equal(new PowerLimits(30, 65), limits);
    }

    [Fact]
    public void AcerDevice_Fans_WritesVerifiedBitmasksAndReadsDualFanTelemetry()
    {
        var ec = new FakeAcerEcHid();
        var wmi = new FakeAcerWmi
        {
            CpuTempC = 64,
            Fan1Rpm = 3100,
            Fan2Rpm = 3450,
        };
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8);

        device.SetFan(FanMode.Max);
        Assert.Equal(AcerDevice.FanBehaviorMax, wmi.LastFanBehavior);
        Assert.Equal(FanMode.Max, device.Fan());

        device.SetFan(FanMode.Auto);
        Assert.Equal(AcerDevice.FanBehaviorAuto, wmi.LastFanBehavior);
        Assert.Equal(FanMode.Auto, device.Fan());

        Assert.Throws<NotSupportedException>(() => device.SetFan(FanMode.Custom));

        var readings = device.Readings();
        Assert.Equal(64, readings.CpuTemperature);
        Assert.Equal(3450, readings.FanRpm);
        Assert.Null(readings.FanSpeed);
    }

    [Fact]
    public void AcerDevice_CustomFanCurve_DrivesDirectPwmInAutoModeAndSupportsBelowAutoSpeeds()
    {
        var ec = new FakeAcerEcHid();
        var wmi = new FakeAcerWmi
        {
            CpuTempC = 55,
            Fan1Rpm = 2800,
            Fan2Rpm = 2800,
        };
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8, enableFanLoop: false);

        // Initial Auto state reports stock reference curve.
        var initial = device.FanCurve();
        Assert.NotNull(initial);
        Assert.True(initial.StockKnown);
        Assert.Equal([50, 60, 70, 80, 90, 100], initial.Temperatures);
        Assert.Equal(AcerBoards.Atlas8.StockFanSpeeds, initial.Stock);
        Assert.Equal(AcerBoards.Atlas8.StockFanSpeeds, initial.Speeds);

        // Set a quiet below-auto curve: 10% at 50-60 °C, clamped to floors above 70 °C.
        var written = device.SetFanCurve([10, 10, 15, 30, 40, 60]);
        Assert.Equal([10, 10, 20, 35, 50, 65], written);

        // Must stay in WMI Auto (0x00140006) so EC 0x7D/0xCD writes directly to hardware PWM 0..255!
        Assert.Equal(AcerDevice.FanBehaviorAuto, wmi.LastFanBehavior);
        var expectedDuty = AcerDevice.PercentToPwmDuty(10);   // 26 / 255
        Assert.Equal(26, expectedDuty);
        Assert.Equal(
            [AcerDevice.FanSpeedInput(2, expectedDuty), AcerDevice.FanSpeedInput(3, expectedDuty)],
            wmi.FanSpeedWrites);

        Assert.Equal(FanMode.Custom, device.Fan());
        Assert.True(device.HoldsFanCurve(written));
        Assert.Equal(10, device.Readings().FanSpeed);
        Assert.Equal(10, device.Snapshot().FanSpeed);

        var active = device.FanCurve();
        Assert.NotNull(active);
        Assert.Equal(written, active.Speeds);
        Assert.Equal(AcerBoards.Atlas8.StockFanSpeeds, active.Stock);

        // Returning to Auto stops the custom curve and clears reported FanSpeed.
        device.SetFan(FanMode.Auto);
        Assert.Equal(FanMode.Auto, device.Fan());
        Assert.False(device.HoldsFanCurve(written));
        Assert.Null(device.Readings().FanSpeed);
    }

    [Fact]
    public void AcerDevice_CustomFanCurve_InterpolatesRisingImmediatelyAndAppliesCoolingHysteresis()
    {
        var ec = new FakeAcerEcHid();
        var wmi = new FakeAcerWmi { CpuTempC = 65 };
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8, enableFanLoop: false);

        // 50°C->20%, 60°C->20%, 70°C->40%, 80°C->60%, 90°C->80%, 100°C->100%
        device.SetFanCurve([20, 20, 40, 60, 80, 100]);
        // At 65°C -> 30% -> duty 77
        Assert.Equal(30, device.Readings().FanSpeed);
        Assert.Equal(AcerDevice.FanSpeedInput(2, AcerDevice.PercentToPwmDuty(30)), wmi.FanSpeedWrites[^2]);

        // Rising 1°C takes effect immediately (66°C -> 32%).
        wmi.CpuTempC = 66;
        wmi.FanSpeedWrites.Clear();
        Assert.True(device.TickFanCurve());
        Assert.Equal(32, device.Readings().FanSpeed);
        Assert.Equal(AcerDevice.FanSpeedInput(2, AcerDevice.PercentToPwmDuty(32)), wmi.FanSpeedWrites[0]);

        // Falling 1°C (within 2°C hysteresis) keeps the 66°C duty (32%) to avoid hunting.
        wmi.CpuTempC = 65;
        wmi.FanSpeedWrites.Clear();
        Assert.True(device.TickFanCurve());
        Assert.Equal(32, device.Readings().FanSpeed);
        Assert.Equal(AcerDevice.FanSpeedInput(2, AcerDevice.PercentToPwmDuty(32)), wmi.FanSpeedWrites[0]);

        // Falling 2°C (66 -> 64°C) steps down smoothly to 28%.
        wmi.CpuTempC = 64;
        wmi.FanSpeedWrites.Clear();
        Assert.True(device.TickFanCurve());
        Assert.Equal(28, device.Readings().FanSpeed);
        Assert.Equal(AcerDevice.FanSpeedInput(2, AcerDevice.PercentToPwmDuty(28)), wmi.FanSpeedWrites[0]);
    }

    [Fact]
    public void AcerDevice_CustomFanCurve_SkipsRedundantSmisAtZeroRpmIdleAndDuringStandbyOrMakerTool()
    {
        var makerRunning = false;
        var ec = new FakeAcerEcHid();
        var wmi = new FakeAcerWmi { CpuTempC = 45, Fan1Rpm = 0, Fan2Rpm = 0 };
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8, enableFanLoop: false, isMakerToolRunning: () => makerRunning);

        // 0% at 50-60°C; initial SetFanCurve writes 0 once to verify WMI accepts it.
        device.SetFanCurve([0, 0, 20, 35, 50, 65]);
        Assert.Equal(2, wmi.FanSpeedWrites.Count);
        Assert.Equal(0, device.Readings().FanSpeed);

        // Subsequent tick at 0% target & 0 RPM skips SMIs completely.
        wmi.FanSpeedWrites.Clear();
        Assert.True(device.TickFanCurve());
        Assert.Empty(wmi.FanSpeedWrites);

        // If the EC spins a fan up while target is still 0%, the next tick writes duty 0 again.
        wmi.Fan1Rpm = 2800;
        Assert.True(device.TickFanCurve());
        Assert.Equal(2, wmi.FanSpeedWrites.Count);
        Assert.Equal(AcerDevice.FanSpeedInput(2, 0), wmi.FanSpeedWrites[0]);

        // During Modern Standby (EC zeroes CTMP = 0), skip SMIs completely.
        wmi.CpuTempC = 0;
        wmi.FanSpeedWrites.Clear();
        Assert.True(device.TickFanCurve());
        Assert.Empty(wmi.FanSpeedWrites);

        // Restore valid temp, then pause via OS Suspend notification.
        wmi.CpuTempC = 75;
        device.OnSystemPowerState(suspended: true);
        Assert.False(device.TickFanCurve());
        Assert.Empty(wmi.FanSpeedWrites);

        // Wake resumes and immediately writes the 75°C duty (28%).
        device.OnSystemPowerState(suspended: false);
        Assert.Equal(2, wmi.FanSpeedWrites.Count);

        // While PredatorSense is running, ticks stand back and emit zero SMIs.
        makerRunning = true;
        wmi.FanSpeedWrites.Clear();
        Assert.False(device.TickFanCurve());
        Assert.Empty(wmi.FanSpeedWrites);
    }

    [Fact]
    public void AcerDevice_CustomFanCurve_DetectsExternalOverrideAndRollsBackOnWriteFailure()
    {
        var ec = new FakeAcerEcHid();
        var wmi = new FakeAcerWmi { CpuTempC = 70 };
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8, enableFanLoop: false);

        var written = device.SetFanCurve([25, 40, 55, 70, 85, 100]);
        Assert.True(device.HoldsFanCurve(written));

        // If WMI refuses a new curve write, the previous curve stays intact.
        wmi.FailSetFanSpeed = true;
        Assert.Throws<FirmwareException>(() => device.SetFanCurve([10, 20, 30, 40, 55, 70]));
        Assert.True(device.HoldsFanCurve(written));
        wmi.FailSetFanSpeed = false;

        // If PredatorSense or BIOS switches hardware behavior to WMI Custom (mode 3) or Max (mode 2),
        // HoldsFanCurve detects the drift so FanCurveKeeper knows to restore it.
        wmi.SetGamingFanBehavior(0x003C_0006);
        Assert.False(device.HoldsFanCurve(written));

        // Re-applying restores custom mode; Max clears the active loop while keeping Max reported.
        device.SetFanCurve(written);
        device.SetFan(FanMode.Max);
        Assert.Equal(FanMode.Max, device.Fan());
        Assert.False(device.HoldsFanCurve(written));
        Assert.Equal(100, device.Snapshot().FanSpeed);
    }

    [Fact]
    public void AcerDevice_CustomFanCurve_BackgroundTimerRefreshesAndStopsCleanlyOnDispose()
    {
        var ec = new FakeAcerEcHid();
        var wmi = new FakeAcerWmi { CpuTempC = 70, Fan1Rpm = 3200, Fan2Rpm = 3200 };
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8, enableFanLoop: true, fanLoopInterval: TimeSpan.FromMilliseconds(25));

        device.SetFanCurve([25, 40, 55, 70, 85, 100]);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (wmi.FanSpeedWrites.Count < 4 && DateTime.UtcNow < deadline)
            Thread.Sleep(15);

        Assert.True(wmi.FanSpeedWrites.Count >= 4, $"Expected background loop refreshes, got {wmi.FanSpeedWrites.Count} writes.");
        device.Dispose();

        var writesAfterDispose = wmi.FanSpeedWrites.Count;
        Thread.Sleep(80);
        Assert.Equal(writesAfterDispose, wmi.FanSpeedWrites.Count);
    }

    [Fact]
    public void AcerDevice_SetPerformance_ImmediatelyReappliesActiveCustomFanDuty()
    {
        var ec = new FakeAcerEcHid { UsageMode = AcerDevice.ModeBalanced };
        var wmi = new FakeAcerWmi { CpuTempC = 60, Fan1Rpm = 3000, Fan2Rpm = 3000 };
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8, enableFanLoop: false);

        device.SetFanCurve([15, 15, 25, 40, 55, 70]);
        wmi.FanSpeedWrites.Clear();

        device.SetPerformance(PerformanceMode.Balanced, new PowerLimits(25, 28));

        var expectedDuty = AcerDevice.PercentToPwmDuty(15);
        Assert.Equal(
            [AcerDevice.FanSpeedInput(2, expectedDuty), AcerDevice.FanSpeedInput(3, expectedDuty)],
            wmi.FanSpeedWrites);
    }

    [Fact]
    public void AcerDevice_ChargeLimit_ClearsHidBoundaryBeforeWmiHealthControl()
    {
        var ec = new FakeAcerEcHid();
        var wmi = new FakeAcerWmi();
        using var device = new AcerDevice(ec, wmi, AcerBoards.Atlas8);

        device.SetChargeLimit(80);
        Assert.Contains("SetBatteryBoundary(False,80,50)", ec.Log);
        Assert.True(wmi.BatteryHealthEnabled);
        Assert.Equal(80, device.ChargeLimit());

        device.SetChargeLimit(100);
        Assert.False(wmi.BatteryHealthEnabled);
        Assert.Equal(100, device.ChargeLimit());

        Assert.Throws<ArgumentOutOfRangeException>(() => device.SetChargeLimit(90));
    }

    [Fact]
    public void AcerEcHid_BuildAndParseFrames_MatchesFirmwareSpec()
    {
        var req = AcerEcHid.BuildRequest(AcerEcHid.CmdSystemUsageMode, AcerEcHid.OpSet, [AcerDevice.ModeManual]);
        Assert.Equal(AcerEcHid.DefaultReportLength, req.Length);
        Assert.Equal([0xA0, 0x00, 0xA0, 0x01, 0x00, 0x01, 0x03], req[..7]);

        var okReply = new byte[65];
        okReply[0] = 0xA0;
        okReply[1] = 0x00;
        okReply[2] = 0xE0;
        okReply[3] = 0x02;
        okReply[4] = 0x00;
        okReply[6] = 0x03;
        BinaryPrimitives.WriteUInt16LittleEndian(okReply.AsSpan(7, 2), 25);
        BinaryPrimitives.WriteUInt16LittleEndian(okReply.AsSpan(11, 2), 28);
        Assert.True(AcerEcHid.IsValidReply(okReply, AcerEcHid.CmdOcProfile));

        var fields = AcerEcHid.ParseOcProfileReply(okReply, expectedProfile: 3);
        Assert.NotNull(fields);
        Assert.Equal(AcerEcHid.OcProfileFieldLength, fields.Length);
        Assert.Equal(25, BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(0, 2)));
        Assert.Equal(28, BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(4, 2)));

        // Rejected reply (FF FF) must fail validation.
        var errReply = (byte[])okReply.Clone();
        errReply[1] = 0xFF;
        errReply[2] = 0xFF;
        Assert.False(AcerEcHid.IsValidReply(errReply, AcerEcHid.CmdOcProfile));

        // Limit reply parsing (bytes 26=type, 27..28=value).
        var limReply = new byte[65];
        limReply[0] = 0xA0;
        limReply[1] = 0x00;
        limReply[2] = 0xE0;
        limReply[3] = 0x02;
        limReply[26] = 0x02;
        BinaryPrimitives.WriteUInt16LittleEndian(limReply.AsSpan(27, 2), 40);
        Assert.Equal((ushort)40, AcerEcHid.ParseOcProfileLimitReply(limReply, expectedType: 2));

        // 0x0B03 OpSet reply from real Atlas 8 ITE EC firmware leaves reply[6] == 0xFF (only OpGet writes fn=0x03).
        var battSetReply = new byte[65];
        Array.Fill(battSetReply, (byte)0xFF);
        battSetReply[0] = 0xA0;
        battSetReply[1] = 0x00;
        battSetReply[2] = 0xE0;
        BinaryPrimitives.WriteUInt16LittleEndian(battSetReply.AsSpan(3, 2), AcerEcHid.CmdBatteryFunction);
        Assert.True(AcerEcHid.IsValidBatteryBoundarySetReply(battSetReply));

        var battSetRejected = (byte[])battSetReply.Clone();
        battSetRejected[1] = 0xFF;
        battSetRejected[2] = 0xFF;
        Assert.False(AcerEcHid.IsValidBatteryBoundarySetReply(battSetRejected));
    }

    [Fact]
    public void AcerLightingHid_BuildZoneReport_MatchesVerifiedRetailWirePayloads()
    {
        const uint stickCaps = AcerLightingHid.CapStatic | AcerLightingHid.CapBlinking | AcerLightingHid.CapBreathing
            | AcerLightingHid.CapNeon | AcerLightingHid.CapWave | AcerLightingHid.CapRainbow;
        const uint logoCaps = 0x0000_003B;   // Verified ENE 0CF2:5130 cap mask (Static/Breathing/Neon/OpMode; no Wave/Rainbow)

        // Sticks Static Red 100%: a4 45 02 64 00 00 ff 00 00 00 00
        var stickStatic = AcerLightingHid.BuildZoneReport(
            AcerLightingHid.DeviceIdSticks, stickCaps, LightingEffect.Steady, true, 100, 5, new Rgb(0xFF, 0x00, 0x00), false);
        Assert.Equal([0xA4, 0x45, 0x02, 0x64, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00], stickStatic);

        // Sticks Wave (Spin) Blue CW (direction 0x05): a4 45 07 19 05 05 00 00 ff 00 00
        var stickSpin = AcerLightingHid.BuildZoneReport(
            AcerLightingHid.DeviceIdSticks, stickCaps, LightingEffect.Spin, true, 25, 10, new Rgb(0x00, 0x00, 0xFF), false);
        Assert.Equal([0xA4, 0x45, 0x07, 0x19, 0x05, 0x05, 0x00, 0x00, 0xFF, 0x00, 0x00], stickSpin);

        // Sticks Rainbow CW (direction 0x05): a4 45 11 64 05 05 00 00 00 00 00
        var stickRainbow = AcerLightingHid.BuildZoneReport(
            AcerLightingHid.DeviceIdSticks, stickCaps, LightingEffect.Rainbow, true, 100, 10, new Rgb(0xFF, 0x00, 0x00), false);
        Assert.Equal([0xA4, 0x45, 0x11, 0x64, 0x05, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00], stickRainbow);

        // Rear Logo Rainbow falls back to Neon/ColorCycle (0x05) since 0x3B lacks Rainbow bit 0x10000.
        var logoRainbow = AcerLightingHid.BuildZoneReport(
            AcerLightingHid.DeviceIdLogo, logoCaps, LightingEffect.Rainbow, true, 75, 6, new Rgb(0xFF, 0xFF, 0xFF), false);
        Assert.Equal([0xA4, 0x80, 0x05, 0x4B, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], logoRainbow);

        // Rear Logo Off sends Static + black.
        var logoOff = AcerLightingHid.BuildZoneReport(
            AcerLightingHid.DeviceIdLogo, logoCaps, LightingEffect.Steady, false, 100, 5, new Rgb(0xFF, 0xFF, 0xFF), false);
        Assert.Equal([0xA4, 0x80, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], logoOff);

        // Rear Logo Spin with holdSteady=false falls back to Breathing (0x04); holdSteady=true keeps Static (0x02).
        var logoSpinBreathe = AcerLightingHid.BuildZoneReport(
            AcerLightingHid.DeviceIdLogo, logoCaps, LightingEffect.Spin, true, 75, 6, new Rgb(0x00, 0xAA, 0xFF), holdSteady: false);
        Assert.Equal([0xA4, 0x80, 0x04, 0x4B, 0x03, 0x00, 0x00, 0xAA, 0xFF, 0x00, 0x00], logoSpinBreathe);

        var logoSpinSteady = AcerLightingHid.BuildZoneReport(
            AcerLightingHid.DeviceIdLogo, logoCaps, LightingEffect.Spin, true, 75, 6, new Rgb(0x00, 0xAA, 0xFF), holdSteady: true);
        Assert.Equal([0xA4, 0x80, 0x02, 0x4B, 0x00, 0x00, 0x00, 0xAA, 0xFF, 0x00, 0x00], logoSpinSteady);

        // ENE 0CF2:5130 verified 0xA3 reply: [A3, 80, 01, 05, 01, 3B, 00, 00, 00]
        var (leds, caps) = AcerLightingHid.ParseDeviceInfo([0xA3, 0x80, 0x01, 0x05, 0x01, 0x3B, 0x00, 0x00, 0x00], null, 0x80);
        Assert.Equal(1, leds);
        Assert.Equal(0x0000_003Bu, caps);
    }

    [Fact]
    public void AcerButtons_ParsesPredatorSenseAndWidgetInputs()
    {
        Assert.Equal(AcerButtons.ButtonKind.OemButton, AcerButtons.ParseRawKeyboard(0x0075, AcerButtons.RiKeyE0));
        Assert.Equal(AcerButtons.ButtonKind.None, AcerButtons.ParseRawKeyboard(0x0075, AcerButtons.RiKeyE0 | AcerButtons.RiKeyBreak));
        Assert.Equal(AcerButtons.ButtonKind.None, AcerButtons.ParseRawKeyboard(0x0075, 0));

        // Page 0x88 report 0x37 / 0x04 press vs release
        Assert.Equal(AcerButtons.ButtonKind.OemButton, AcerButtons.ParseHidReport([0x37, 0x81, 0xFF]));
        Assert.Equal(AcerButtons.ButtonKind.None, AcerButtons.ParseHidReport([0x37, 0x00, 0x00]));

        // Page 0xFFF1 report 0x9E event 5 = Widget key; events 1..4 = trigger switches (ignored)
        Assert.Equal(AcerButtons.ButtonKind.GameBarButton, AcerButtons.ParseHidReport([0x9E, 0x00, 0x00, 0x05, 0x00]));
        Assert.Equal(AcerButtons.ButtonKind.None, AcerButtons.ParseHidReport([0x9E, 0x00, 0x00, 0x01, 0x00]));
        // Page 0xFFF1 reports (0x9E / 0x9A) must never false-match the page 0x88 [id, 0x81, 0xFF] pattern.
        Assert.Equal(AcerButtons.ButtonKind.None, AcerButtons.ParseHidReport([0x9A, 0x81, 0xFF]));
        Assert.Equal(AcerButtons.ButtonKind.None, AcerButtons.ParseHidReport([0x9E, 0x81, 0xFF, 0x00]));
    }

    [Fact]
    public void AcerButtons_PressTracker_SuppressesKeyHoldAutoRepeatUntilRelease()
    {
        var tracker = new AcerButtons.PressTracker();

        // Keyboard E0 75: initial make fires, repeated makes while held are suppressed, break re-arms.
        Assert.Equal(AcerButtons.ButtonKind.OemButton, tracker.FeedRawKeyboard(0x0075, AcerButtons.RiKeyE0));
        Assert.Equal(AcerButtons.ButtonKind.None, tracker.FeedRawKeyboard(0x0075, AcerButtons.RiKeyE0));
        Assert.Equal(AcerButtons.ButtonKind.None, tracker.FeedRawKeyboard(0x0075, AcerButtons.RiKeyE0 | AcerButtons.RiKeyBreak));
        Assert.Equal(AcerButtons.ButtonKind.OemButton, tracker.FeedRawKeyboard(0x0075, AcerButtons.RiKeyE0));

        // Page 0x88 HID: repeated press reports while held are suppressed until [id, 0x00, 0x00] release.
        Assert.Equal(AcerButtons.ButtonKind.OemButton, tracker.FeedHidReport([0x37, 0x81, 0xFF]));
        Assert.Equal(AcerButtons.ButtonKind.None, tracker.FeedHidReport([0x37, 0x81, 0xFF]));
        Assert.Equal(AcerButtons.ButtonKind.None, tracker.FeedHidReport([0x37, 0x00, 0x00]));
        Assert.Equal(AcerButtons.ButtonKind.OemButton, tracker.FeedHidReport([0x37, 0x81, 0xFF]));
    }

    [Fact]
    public void FanViewModel_Atlas8_ExposesReadOnlyCustomSegmentWhenLeftInCustomExternally()
    {
        Assert.Equal(
            [FanMode.Auto, FanMode.Max],
            Tsume.App.ViewModels.FanViewModel.LayoutModesFor(AcerBoards.Atlas8.Profile, limits: null, FanMode.Auto));
        Assert.Equal(
            [FanMode.Auto, FanMode.Max],
            Tsume.App.ViewModels.FanViewModel.LayoutModesFor(AcerBoards.Atlas8.Profile, limits: null, FanMode.Max));
        Assert.Equal(
            [FanMode.Auto, FanMode.Max, FanMode.Custom],
            Tsume.App.ViewModels.FanViewModel.LayoutModesFor(AcerBoards.Atlas8.Profile, limits: null, FanMode.Custom));
    }

    [Fact]
    public void BackButtonControllers_KeysFor_ExcludesMsiOnlyF22OnAtlas8()
    {
        var acerKeys = BackButtonControllers.KeysFor(AcerBoards.Atlas8.Profile.Controls!.BackButtons);
        var msiKeys = BackButtonControllers.KeysFor(MsiBoards.Claw8.Profile.Controls!.BackButtons);

        Assert.DoesNotContain(acerKeys, k => k.Code == Core.Controller.BackButtonKey.F22Code);
        Assert.Contains(acerKeys, k => k.Code == 156); // F23 is supported on Acer HJC
        Assert.Contains(msiKeys, k => k.Code == Core.Controller.BackButtonKey.F22Code);
    }

    [Fact]
    public void SystemButtonsViewModel_OemButtonDetail_UsesPredatorSenseWordingOnAtlas8()
    {
        var acerControls = AcerBoards.Atlas8.Profile.Controls!;
        var openTsume = new Tsume.App.ViewModels.HardwareButtonOption(HardwareButtonAction.OpenTsume, "Open Tsume");
        var screenshot = new Tsume.App.ViewModels.HardwareButtonOption(HardwareButtonAction.Screenshot, "Screenshot");
        var disabled = new Tsume.App.ViewModels.HardwareButtonOption(HardwareButtonAction.Disabled, "Disabled");

        var detailTsume = Tsume.App.ViewModels.SystemButtonsViewModel.OemButtonDetailFor(acerControls, openTsume, oemRuns: false);
        var detailCustom = Tsume.App.ViewModels.SystemButtonsViewModel.OemButtonDetailFor(acerControls, screenshot, oemRuns: false);
        var detailDisabled = Tsume.App.ViewModels.SystemButtonsViewModel.OemButtonDetailFor(acerControls, disabled, oemRuns: false);

        Assert.Contains("PredatorSense", detailTsume);
        Assert.DoesNotContain("MSI", detailTsume);
        Assert.Contains("PredatorSense", detailCustom);
        Assert.DoesNotContain("MSI", detailCustom);
        Assert.Contains("PredatorSense", detailDisabled);
        Assert.DoesNotContain("M button", detailDisabled);
    }

    [Fact]
    public void HelperJson_RoundTripsAtlas8DeviceProfile()
    {
        var reply = HelperReply.Success(new Core.Telemetry.HardwareSnapshot
        {
            Mode = PerformanceMode.Balanced,
            Limits = new PowerLimits(25, 28),
            Fan = FanMode.Auto,
            ChargeLimit = 80,
            CpuTemperature = 58,
            FanRpm = 2800,
            FirmwareVersion = "V1.04",
        }) with { Device = AcerBoards.Atlas8.Profile };

        var json = HelperJson.Write(reply);
        var parsed = HelperJson.ReadReply(json);

        Assert.NotNull(parsed?.Device);
        Assert.True(AcerBoards.Atlas8.Profile.Matches(parsed.Device));
    }

    [Fact]
    public void ClawController_IsClaw_MatchesMsiAndAcerGamepadIds()
    {
        Assert.True(ClawController.IsClaw(@"\\?\hid#vid_0db0&pid_1901&mi_01#7&1234#{4d1e55b2}"));
        Assert.True(ClawController.IsClaw(@"\\?\hid#vid_0502&pid_f901&mi_01#7&1234#{4d1e55b2}"));
        Assert.True(ClawController.IsClaw(@"\\?\hid#vid_0489&pid_f311&mi_02#7&1234#{4d1e55b2}"));
        Assert.False(ClawController.IsClaw(@"\\?\hid#vid_046d&pid_c52b&mi_00#7&1234#{4d1e55b2}"));
    }

    [Fact]
    public void SettingsViewModel_PauseToggleSaysPredatorSenseOnlyForAcerDevices()
    {
        Assert.Equal("Pause PredatorSense", Tsume.App.ViewModels.SettingsViewModel.OemPauseLabelFor(AcerBoards.Atlas8.Profile));
        Assert.Equal("Pause PredatorSense", Tsume.App.ViewModels.SettingsViewModel.OemPauseLabelFor(AcerBoards.Atlas8ArcG3.Profile));
        Assert.Equal("Pause MSI Center M", Tsume.App.ViewModels.SettingsViewModel.OemPauseLabelFor(MsiBoards.Claw8.Profile));
        Assert.Equal("Pause MSI Center M", Tsume.App.ViewModels.SettingsViewModel.OemPauseLabelFor(MsiBoards.ClawA8.Profile));
        Assert.Equal("Pause MSI Center M", Tsume.App.ViewModels.SettingsViewModel.OemPauseLabelFor(DeviceProfile.Unknown));
    }

    private sealed class FakeAcerEcHid : IAcerEcHid
    {
        public byte UsageMode { get; set; } = AcerDevice.ModeBalanced;
        public byte[] OcProfile3 { get; } = new byte[AcerEcHid.OcProfileFieldLength];
        public bool FailGetProfile { get; set; }
        public bool FailSetMode { get; set; }
        public List<string> Log { get; } = [];

        public byte? GetSystemUsageMode()
        {
            Log.Add("GetMode");
            return UsageMode;
        }

        public bool SetSystemUsageMode(byte mode)
        {
            Log.Add($"SetMode({mode})");
            if (FailSetMode) return false;
            UsageMode = mode;
            return true;
        }

        public byte[]? GetOcProfile(byte profile)
        {
            Log.Add($"GetOcProfile({profile})");
            if (FailGetProfile) return null;
            return profile == AcerDevice.ManualOcProfile ? OcProfile3.ToArray() : null;
        }

        public bool SetOcProfile(byte profile, ReadOnlySpan<byte> fields)
        {
            Log.Add($"SetOcProfile({profile})");
            if (profile != AcerDevice.ManualOcProfile) return false;
            fields.CopyTo(OcProfile3);
            return true;
        }

        public ushort? GetOcProfileLimit(byte profile, byte limitType) => limitType switch
        {
            1 => 10,
            2 => 40,
            4 => 65,
            _ => null,
        };

        public bool SetBatteryBoundary(bool enabled, byte upperPercent, byte lowerPercent)
        {
            Log.Add($"SetBatteryBoundary({enabled},{upperPercent},{lowerPercent})");
            return true;
        }

        public (bool Enabled, byte Upper, byte Lower)? GetBatteryBoundary() => (false, 80, 50);

        public void Dispose() { }
    }

    private sealed class FakeAcerWmi : IAcerWmi
    {
        public int CpuTempC { get; set; } = 55;
        public int Fan1Rpm { get; set; } = 2800;
        public int Fan2Rpm { get; set; } = 2800;
        public ulong LastFanBehavior { get; private set; } = AcerDevice.FanBehaviorAuto;
        public bool BatteryHealthEnabled { get; private set; }
        public bool FailSetFanSpeed { get; set; }
        public List<ulong> FanSpeedWrites { get; } = [];

        public ulong? GetGamingSysInfo(uint input) => input switch
        {
            0x0000 => 0x0000_008D_0000_0000UL,
            AcerDevice.SensorCpuTemp => ((ulong)CpuTempC << 8),
            AcerDevice.SensorFan1Rpm => ((ulong)Fan1Rpm << 8),
            AcerDevice.SensorFan2Rpm => ((ulong)Fan2Rpm << 8),
            _ => null,
        };

        public bool SetGamingFanBehavior(ulong input)
        {
            LastFanBehavior = input;
            return true;
        }

        public ulong? GetGamingFanBehavior(uint fanMask)
        {
            var f1 = (LastFanBehavior >> 18) & 0x3;
            var f2 = (LastFanBehavior >> 20) & 0x3;
            return (f1 << 10) | (f2 << 12);
        }

        public bool SetGamingFanSpeed(ulong input)
        {
            if (FailSetFanSpeed) return false;
            lock (FanSpeedWrites) FanSpeedWrites.Add(input);
            return true;
        }
        public ulong? GetGamingFanSpeed(uint fanId) => 50UL << 8;
        public bool? GetBatteryHealthControl() => BatteryHealthEnabled;

        public bool SetBatteryHealthControl(bool enabled)
        {
            BatteryHealthEnabled = enabled;
            return true;
        }

        public string? ReadBiosVersion() => "V1.04";
        public void Dispose() { }
    }
}
