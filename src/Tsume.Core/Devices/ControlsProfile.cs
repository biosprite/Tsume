using System.Text.Json.Serialization;

namespace Tsume.Core.Devices;

/// <summary>A firmware keyboard shortcut emitted for a hardware button (e.g. Win+G or F15/F16).</summary>
public sealed record KeyTrigger(ushort VirtualKey, bool Win = false, bool Ctrl = false, bool Alt = false, bool Shift = false);

/// <summary>One remappable rear button and its verified byte offset in the controller's profile image.</summary>
public sealed record BackButtonDefinition(string Id, string Label, string Subtitle, int Offset);

/// <summary>Manufacturer control suite that competes with Tsume for buttons, power modes, or lighting.</summary>
public enum OemSoftwareKind
{
    None,
    MsiCenter,
    AcerPredatorSense,
}

/// <summary>Verified controller back-button map; selected by <see cref="Protocol"/>.</summary>
public sealed record BackButtonsProfile
{
    public const string MsiHidV1 = "msi-hid-v1";

    public required string Protocol { get; init; }
    public ushort VendorId { get; init; }

    /// <summary>Optional full list of USB Vendor IDs when a controller ships under more than one VID (e.g. Acer 0x0502 / Foxconn 0x0489).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ushort>? VendorIds { get; init; }

    [JsonIgnore]
    public IReadOnlyList<ushort> EffectiveVendorIds =>
        VendorIds is { Count: > 0 } ? VendorIds : VendorId != 0 ? [VendorId] : [];

    public IReadOnlyList<ushort> ProductIds { get; init; } = [];
    public int ProfileLength { get; init; }
    public int BlockLength { get; init; } = 8;
    public int MaxSimultaneousKeys { get; init; } = 3;

    /// <summary>Minimum USB <c>bcdDevice</c> firmware version (e.g. <c>0x0217</c>) that uses these profile offsets; null if all versions do.</summary>
    public ushort? MinFirmwareVersion { get; init; }

    public IReadOnlyList<BackButtonDefinition> Buttons { get; init; } = [];

    public bool Matches(BackButtonsProfile other) =>
        Protocol == other.Protocol
        && VendorId == other.VendorId
        && EffectiveVendorIds.SequenceEqual(other.EffectiveVendorIds)
        && ProductIds.SequenceEqual(other.ProductIds)
        && ProfileLength == other.ProfileLength
        && BlockLength == other.BlockLength
        && MaxSimultaneousKeys == other.MaxSimultaneousKeys
        && MinFirmwareVersion == other.MinFirmwareVersion
        && Buttons.SequenceEqual(other.Buttons);
}

/// <summary>Hardware buttons and verified rear-paddle capabilities declared by a board profile.</summary>
public sealed record ControlsProfile
{
    /// <summary>Acer HID vendor usage page <c>0xFF5A</c> feature-report lighting (<c>0xA1</c>–<c>0xA4</c>).</summary>
    public const string AcerHidLighting = "acer-hid-0xff5a";

    public bool HasGameBarButton { get; init; }
    public string? GameBarButtonLabel { get; init; }

    /// <summary>True when firmware natively opens Xbox Game Bar via Win+G without OEM software.</summary>
    public bool GameBarNativeWinG { get; init; }

    /// <summary>Firmware keys/chords to swallow when the Game Bar button is reassigned.</summary>
    public IReadOnlyList<KeyTrigger> GameBarTriggers { get; init; } = [];

    public bool HasOemButton { get; init; }
    public string? OemButtonLabel { get; init; }
    public bool HasMsiCenterConflict { get; init; }

    /// <summary>OEM control suite that competes with Tsume on this machine; falls back to <see cref="HasMsiCenterConflict"/> when unset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OemSoftwareKind OemSoftware { get; init; }

    [JsonIgnore]
    public OemSoftwareKind EffectiveOemSoftware =>
        OemSoftware != OemSoftwareKind.None
            ? OemSoftware
            : HasMsiCenterConflict ? OemSoftwareKind.MsiCenter : OemSoftwareKind.None;

    public int? OemButtonWmiCode { get; init; }
    public int? GameBarWmiCode { get; init; }

    /// <summary>True when dedicated buttons arrive via Acer Raw Input / vendor HID (<c>E0 75</c>, page <c>0x88</c>, page <c>0xFFF1</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasAcerHidButtons { get; init; }

    /// <summary>Lighting transport protocol; null uses the default HJC profile lighting block.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LightingProtocol { get; init; }

    /// <summary>Null until the controller's profile map is verified on this board.</summary>
    public BackButtonsProfile? BackButtons { get; init; }

    public bool Matches(ControlsProfile other) =>
        HasGameBarButton == other.HasGameBarButton
        && GameBarButtonLabel == other.GameBarButtonLabel
        && GameBarNativeWinG == other.GameBarNativeWinG
        && GameBarTriggers.SequenceEqual(other.GameBarTriggers)
        && HasOemButton == other.HasOemButton
        && OemButtonLabel == other.OemButtonLabel
        && HasMsiCenterConflict == other.HasMsiCenterConflict
        && EffectiveOemSoftware == other.EffectiveOemSoftware
        && OemButtonWmiCode == other.OemButtonWmiCode
        && GameBarWmiCode == other.GameBarWmiCode
        && HasAcerHidButtons == other.HasAcerHidButtons
        && LightingProtocol == other.LightingProtocol
        && (BackButtons is null ? other.BackButtons is null : other.BackButtons is not null && BackButtons.Matches(other.BackButtons));
}
