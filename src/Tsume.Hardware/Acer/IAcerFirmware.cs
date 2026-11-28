namespace Tsume.Hardware.Acer;

/// <summary>
/// EC HID-over-I2C transport (<c>1025:174B</c>, usage page <c>0xFF05</c>, report <c>0xA0</c>) for Acer handheld
/// operating mode, Manual OC Profile 3 PL1/PL2 limits, and battery boundary control.
/// </summary>
public interface IAcerEcHid : IDisposable
{
    /// <summary>Reads the applied EC operating mode (<c>0</c> Turbo, <c>1</c> Balanced, <c>2</c> Quiet, <c>3</c> Manual).</summary>
    byte? GetSystemUsageMode();

    /// <summary>Sets the EC operating mode (<c>0..3</c>), triggering SCI <c>_Q30</c>, DTT policy update, and PECI limits.</summary>
    bool SetSystemUsageMode(byte mode);

    /// <summary>Reads the 19-byte OC profile payload (<c>f1..f11</c>) for <paramref name="profile"/>.</summary>
    byte[]? GetOcProfile(byte profile);

    /// <summary>Writes the 19-byte OC profile payload (<c>f1..f11</c>) for <paramref name="profile"/>.</summary>
    bool SetOcProfile(byte profile, ReadOnlySpan<byte> fields);

    /// <summary>Reads one EC-reported OC profile limit (<c>1</c>=PL1_Min, <c>2</c>=PL1_Max, <c>4</c>=PL2_Max).</summary>
    ushort? GetOcProfileLimit(byte profile, byte limitType);

    /// <summary>Enables or disables the EC HID battery charge boundary (<c>0x0B03</c> function 3).</summary>
    bool SetBatteryBoundary(bool enabled, byte upperPercent, byte lowerPercent);

    /// <summary>Reads the EC HID battery charge boundary state, or null if unanswered.</summary>
    (bool Enabled, byte Upper, byte Lower)? GetBatteryBoundary();
}

/// <summary>
/// BIOS WMI (<c>root\WMI</c>) transport for <c>AcerGamingFunction</c> (fans and sensors) and <c>BatteryControl</c>
/// (80 % battery health charge limit).
/// </summary>
public interface IAcerWmi : IDisposable
{
    /// <summary>Calls <c>AcerGamingFunction.GetGamingSysInfo</c>; returns null if the call fails or status byte != 0.</summary>
    ulong? GetGamingSysInfo(uint input);

    /// <summary>Calls <c>AcerGamingFunction.SetGamingFanBehavior</c>; returns true when status byte == 0.</summary>
    bool SetGamingFanBehavior(ulong input);

    /// <summary>Calls <c>AcerGamingFunction.GetGamingFanBehavior</c>; returns null if failed or status byte != 0.</summary>
    ulong? GetGamingFanBehavior(uint fanMask);

    /// <summary>Calls <c>AcerGamingFunction.SetGamingFanSpeed</c>; returns true when status byte == 0.</summary>
    bool SetGamingFanSpeed(ulong input);

    /// <summary>Calls <c>AcerGamingFunction.GetGamingFanSpeed</c>; returns null if failed or status byte != 0.</summary>
    ulong? GetGamingFanSpeed(uint fanId);

    /// <summary>Reads <c>BatteryControl.GetBatteryHealthControlStatus</c> for battery 1 (true = 80 % limit on).</summary>
    bool? GetBatteryHealthControl();

    /// <summary>Sets <c>BatteryControl.SetBatteryHealthControl</c> for battery 1 (true = 80 % limit on, false = 100 %).</summary>
    bool SetBatteryHealthControl(bool enabled);

    /// <summary>Reads the BIOS version string for snapshot telemetry, or null if unavailable.</summary>
    string? ReadBiosVersion();
}
