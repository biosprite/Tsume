using Tsume.Core.Performance;

namespace Tsume.Hardware.Msi;

/// <summary>
/// Translates between <see cref="PerformanceMode"/> and the byte in a board's shift-mode register.
///
/// <code>
///   bit 7     shift modes supported     always 1
///   bit 6     shift mode active         always 1
///   bits 5-0  which mode
/// </code>
/// </summary>
public sealed record MsiShiftModes
{
    /// <summary>The bits written alongside every mode.</summary>
    public byte SupportedAndActive { get; init; } = 0b1100_0000;

    public byte ModeMask { get; init; } = 0b0011_1111;

    /// <summary>The code for each mode the board offers.</summary>
    public required IReadOnlyDictionary<PerformanceMode, byte> Codes { get; init; }

    /// <summary>What any code Tsume does not write itself reads as.</summary>
    public required PerformanceMode Otherwise { get; init; }

    public byte Encode(PerformanceMode mode) => Codes.TryGetValue(mode, out var code)
        ? (byte)(SupportedAndActive | code)
        : throw new ArgumentOutOfRangeException(nameof(mode), mode, null);

    public PerformanceMode Decode(byte register)
    {
        var code = register & ModeMask;

        // When Manual shares its shift code with another mode (e.g. 0x04 on MS-1T42, MS-1T52, MS-1T8K),
        // decode it as Manual so ReadMode() matches MsiDevice.HardwareModeFor() and ModeKeeper.Resolve().
        if (Codes.TryGetValue(PerformanceMode.Manual, out var manualCode) && manualCode == code)
            return PerformanceMode.Manual;

        foreach (var (mode, itsCode) in Codes)
            if (itsCode == code) return mode;

        return Otherwise;
    }
}
