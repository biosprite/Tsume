using System.Text.Json;
using System.Text.Json.Serialization;
using Tsume.Core.Cooling;
using Tsume.Core.Performance;
using Tsume.Core.Devices;
using Tsume.Core.Telemetry;

namespace Tsume.Core.Ipc;

// The app (unelevated) and the helper (elevated) exchange one line of JSON each way over a named pipe:
//     app  →  {"op":"performance.set","mode":"Balanced","manual":{...}}
//     app  ←  {"ok":true,"snapshot":{...}}
// Requests are intentions, never raw register writes: the helper clamps every value to its
// DeviceProfile, so an unelevated process cannot push the hardware outside its safe range.

public static class HelperPipe
{
    public const string Name = "Tsume.Helper.v1";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(GetSnapshot),    "snapshot.get")]
[JsonDerivedType(typeof(GetReadings),    "readings.get")]
[JsonDerivedType(typeof(SetPerformance), "performance.set")]
[JsonDerivedType(typeof(SetFan),         "fan.set")]
[JsonDerivedType(typeof(SetChargeLimit), "charge.set")]
[JsonDerivedType(typeof(StartPanel),     "panel.start")]
[JsonDerivedType(typeof(GetLog),            "log.get")]
[JsonDerivedType(typeof(SetRtssProfile),    "rtss.profile.set")]
[JsonDerivedType(typeof(SetRtssExclusions), "rtss.exclusions.set")]
[JsonDerivedType(typeof(StopRtss),          "rtss.stop")]
public abstract record HelperRequest;

/// <summary>Read the current state without changing anything.</summary>
public sealed record GetSnapshot : HelperRequest;

/// <summary>Read only temperature and fan speed. Cheap; asked once a second.</summary>
public sealed record GetReadings : HelperRequest;

/// <summary>
/// Switch performance mode. <paramref name="Manual"/> is used only by <see cref="PerformanceMode.Manual"/>;
/// <paramref name="Custom"/> replaces a preset mode's limits, clamped to the same range as Manual.
/// </summary>
public sealed record SetPerformance(PerformanceMode Mode, PowerLimits Manual, PowerLimits? Custom = null) : HelperRequest;

/// <summary>Switch fan behaviour.</summary>
public sealed record SetFan(FanMode Mode) : HelperRequest;

/// <summary>Stop charging the battery at this level. Only a level the machine's profile lists is accepted; 100 charges to full.</summary>
public sealed record SetChargeLimit(int Percent) : HelperRequest;

/// <summary>
/// Start the panel again with UIAccess, the right the on-screen keyboard uses to draw over the taskbar, Start,
/// Search, and elevated windows. Only the helper, running as the system, can hand that to an unsigned
/// program. The flags are the panel's own start-up intentions; the helper always starts the fixed Tsume.exe beside
/// it, in the caller's own session, so neither the program run nor the session is anything a caller can name.
/// </summary>
public sealed record StartPanel(bool Background, bool Settings, bool Lighting, bool OptiScaler, bool Stay, bool Rtss = false) : HelperRequest;

/// <summary>Read the helper's own log (kept under the service account's profile when installed, so the panel cannot read it on disk).</summary>
public sealed record GetLog : HelperRequest;

/// <summary>
/// Updates validated keys in RTSS's Global profile file under Program Files (which requires elevation to write).
/// Only non-null fields are updated; all values are clamped by the helper and the path is resolved from RTSS's own install.
/// </summary>
public sealed record SetRtssProfile(
    int? FramerateLimit = null,
    int? PositionX = null,
    int? PositionY = null,
    int? ZoomRatio = null,
    int? EnableFill = null,
    uint? FillColor = null) : HelperRequest;

/// <summary>
/// Writes RTSS per-game exclusion profiles (<c>EnableHooking=0</c>) for the given bare executable names
/// (e.g. anti-cheat games), and removes previously Tsume-created exclusions no longer in the list.
/// Never touches user-created RTSS profiles.
/// </summary>
public sealed record SetRtssExclusions(IReadOnlyList<string> Executables) : HelperRequest;

/// <summary>Stops the running RTSS process (and its companion executables inside RTSS's install folder).</summary>
public sealed record StopRtss : HelperRequest;

/// <summary>The helper's answer to any request.</summary>
public sealed record HelperReply
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }

    /// <summary>True when the helper is running but does not know this machine, so it refuses everything.</summary>
    public bool Unsupported { get; init; }
    public HardwareSnapshot? Snapshot { get; init; }

    /// <summary>Set instead of <see cref="Snapshot"/> when only <see cref="GetReadings"/> was asked.</summary>
    public LiveReadings? Readings { get; init; }

    /// <summary>Sent only with the answer to <see cref="GetSnapshot"/>.</summary>
    public DeviceProfile? Device { get; init; }

    /// <summary>Sent only with the answer to <see cref="GetLog"/>.</summary>
    public string? Log { get; init; }

    public static HelperReply Success(HardwareSnapshot snapshot) => new() { Ok = true, Snapshot = snapshot };
    public static HelperReply Success(LiveReadings readings) => new() { Ok = true, Readings = readings };
    public static HelperReply Failure(string error) => new() { Ok = false, Error = error };

    public static HelperReply NotSupported(string machine) =>
        new() { Ok = false, Unsupported = true, Error = $"This device ({machine}) is not supported yet." };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(HelperRequest))]
[JsonSerializable(typeof(HelperReply))]
public sealed partial class HelperJson : JsonSerializerContext
{
    public static string Write(HelperRequest request) => JsonSerializer.Serialize(request, Default.HelperRequest);
    public static string Write(HelperReply reply) => JsonSerializer.Serialize(reply, Default.HelperReply);

    public static HelperRequest? ReadRequest(string line) => JsonSerializer.Deserialize(line, Default.HelperRequest);
    public static HelperReply? ReadReply(string line) => JsonSerializer.Deserialize(line, Default.HelperReply);
}
