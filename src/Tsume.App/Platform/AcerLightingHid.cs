using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Tsume.App.Platform.Interop;
using Tsume.Core.Lighting;

namespace Tsume.App.Platform;

/// <summary>
/// Direct Acer HID lighting driver for vendor usage page <c>0xFF5A</c>, usage <c>0x01</c> (feature reports
/// <c>0xA1</c>–<c>0xA4</c>). On the Predator Atlas 8 this discovers both:
/// <list type="bullet">
///   <item>Thumbstick RGB rings: USB <c>0502:F901</c> (<c>MI_02</c>), <c>devId = 0x45</c>.</item>
///   <item>Rear Predator Logo LED: I2C-HID <c>0CF2:5130</c> (<c>ENEK5130</c>), <c>devId = 0x80</c> (1 LED, cap mask <c>0x3B</c>).</item>
/// </list>
/// </summary>
public sealed class AcerLightingHid : IDisposable
{
    public const ushort UsagePage = 0xFF5A;
    public const ushort Usage = 0x01;

    public const byte ReportListDevices = 0xA1;
    public const byte ReportSelectDevice = 0xA2;
    public const byte ReportDeviceInfo = 0xA3;
    public const byte ReportSetMode = 0xA4;

    public const byte DeviceIdSticks = 0x45;
    public const byte DeviceIdLogo = 0x80;
    public const byte DeviceIdCoverLogo = 0x83;
    public const byte DeviceIdBaseLogo = 0x84;

    // 0xA4 mode bytes verified from retail 0502:F901 captures and ENE 0CF2:5130 firmware.
    public const byte ModeStatic = 0x02;
    public const byte ModeBlinking = 0x03;
    public const byte ModeBreathing = 0x04;
    public const byte ModeNeon = 0x05;
    public const byte ModeOperatingMode = 0x06;
    public const byte ModeWave = 0x07;
    public const byte ModeRainbow = 0x11;

    // Verified on wire: Wave and Rainbow use 0x05 (CW) / 0x06 (CCW).
    public const byte DirectionNone = 0x00;
    public const byte DirectionClockwise = 0x05;
    public const byte DirectionCounterClockwise = 0x06;

    // 0xA3 capability bitmask flags (u0x31).
    public const uint CapStatic = 0x0000_0002;
    public const uint CapBlinking = 0x0000_0004;
    public const uint CapBreathing = 0x0000_0008;
    public const uint CapNeon = 0x0000_0010;
    public const uint CapWave = 0x0000_0040;
    public const uint CapRainbow = 0x0001_0000;

    private const int MinFeatureLength = 64;
    private const int MaxRetries = 5;
    private const int RetryDelayMs = 20;

    public sealed record Zone(byte DeviceId, int LedCount, uint Capabilities);

    private sealed class Endpoint(SafeFileHandle handle, int featureLength, List<Zone> zones) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;
        public int FeatureLength { get; } = featureLength;
        public List<Zone> Zones { get; } = zones;
        public void Dispose() => Handle.Dispose();
    }

    private readonly List<Endpoint> _endpoints;
    private readonly Lock _gate = new();
    private bool _disposed;

    private AcerLightingHid(List<Endpoint> endpoints) => _endpoints = endpoints;

    public bool HasSticks => _endpoints.Any(e => e.Zones.Any(z => z.DeviceId == DeviceIdSticks));
    public bool HasLogo => _endpoints.Any(e => e.Zones.Any(z => IsLogoDevice(z.DeviceId)));
    public int ZoneCount => _endpoints.Sum(e => e.Zones.Count);

    public static bool IsLogoDevice(byte deviceId) =>
        deviceId is DeviceIdLogo or DeviceIdCoverLogo or DeviceIdBaseLogo;

    /// <summary>Enumerates all HID collections on usage page <c>0xFF5A</c> and queries their device IDs and capabilities.</summary>
    public static AcerLightingHid? Find()
    {
        var endpoints = new List<Endpoint>();

        foreach (var path in Hid.DevicePaths())
        {
            var handle = Hid.OpenForFeature(path);
            if (handle is null) continue;

            var desc = Hid.DescribeAll(handle);
            if (desc is not { } d || d.Caps.UsagePage != UsagePage || d.Caps.Usage != Usage)
            {
                handle.Dispose();
                continue;
            }

            var featureLen = Math.Max((int)d.Caps.FeatureReportByteLength, MinFeatureLength);
            var fieldSizes = Hid.FeatureValueByteSizes(handle);
            var zones = DiscoverZones(handle, featureLen, fieldSizes);
            if (zones.Count == 0)
            {
                handle.Dispose();
                continue;
            }

            endpoints.Add(new Endpoint(handle, featureLen, zones));
        }

        return endpoints.Count > 0 ? new AcerLightingHid(endpoints) : null;
    }

    /// <summary>Applies <paramref name="lighting"/> to all discovered stick and rear-logo zones.</summary>
    public bool Apply(StickLighting lighting)
    {
        lock (_gate)
        {
            if (_disposed || _endpoints.Count == 0) return false;

            var anySent = false;
            var allOk = true;

            foreach (var endpoint in _endpoints)
            {
                foreach (var zone in endpoint.Zones)
                {
                    byte[] report;
                    if (zone.DeviceId == DeviceIdSticks)
                    {
                        report = BuildZoneReport(
                            zone.DeviceId,
                            zone.Capabilities,
                            lighting.Effect,
                            lighting.SticksLit,
                            lighting.StickBrightness,
                            lighting.Speed,
                            lighting.LeftStick,
                            holdSteady: false,
                            endpoint.FeatureLength);
                    }
                    else if (IsLogoDevice(zone.DeviceId))
                    {
                        report = BuildZoneReport(
                            zone.DeviceId,
                            zone.Capabilities,
                            lighting.Effect,
                            lighting.ButtonsLit,
                            lighting.ButtonBrightness,
                            lighting.Speed,
                            lighting.Buttons,
                            holdSteady: lighting.ButtonsSteady,
                            endpoint.FeatureLength);
                    }
                    else
                    {
                        continue;
                    }

                    anySent = true;
                    if (!SendWithRetry(endpoint.Handle, report))
                        allOk = false;
                }
            }

            return anySent && allOk;
        }
    }

    /// <summary>
    /// Builds the 11-byte <c>0xA4</c> feature report (padded to <paramref name="reportLength"/>) for one Acer
    /// <c>0xFF5A</c> zone, mapping Tsume's effects onto the device's supported capabilities.
    /// </summary>
    public static byte[] BuildZoneReport(
        byte deviceId,
        uint capabilities,
        LightingEffect effect,
        bool zoneLit,
        int brightnessPercent,
        int tsumeSpeed,
        Rgb colour,
        bool holdSteady,
        int reportLength = 11)
    {
        var buffer = new byte[Math.Max(reportLength, 11)];
        buffer[0] = ReportSetMode;
        buffer[1] = deviceId;

        if (effect == LightingEffect.Off || !zoneLit)
        {
            buffer[2] = ModeStatic;
            buffer[3] = 0;
            buffer[4] = 0;
            buffer[5] = DirectionNone;
            // RGB = 00 00 00, ledMask = 00 00 (all LEDs)
            return buffer;
        }

        var brightness = (byte)Math.Clamp(brightnessPercent, 1, 100);
        var speed = MapSpeed(tsumeSpeed);
        var effective = holdSteady && effect is LightingEffect.Breathe or LightingEffect.Rainbow or LightingEffect.Spin
            ? LightingEffect.Steady
            : effect;

        var (mode, outSpeed, direction, rgb) = ResolveEffect(effective, capabilities, speed, colour);

        buffer[2] = mode;
        buffer[3] = brightness;
        buffer[4] = outSpeed;
        buffer[5] = direction;
        buffer[6] = rgb.R;
        buffer[7] = rgb.G;
        buffer[8] = rgb.B;
        // Bytes 9..10 = ledMask 0x0000 (all LEDs in zone)
        return buffer;
    }

    /// <summary>Maps Tsume's 1..10 speed slider onto Acer's 1..5 firmware speed range.</summary>
    public static byte MapSpeed(int tsumeSpeed) =>
        (byte)Math.Clamp((Math.Clamp(tsumeSpeed, StickLighting.MinSpeed, StickLighting.MaxSpeed) + 1) / 2, 1, 5);

    private static (byte Mode, byte Speed, byte Direction, Rgb Colour) ResolveEffect(
        LightingEffect effect, uint capabilities, byte speed, Rgb colour) => effect switch
    {
        LightingEffect.Steady => (ModeStatic, (byte)0, DirectionNone, colour),

        LightingEffect.Breathe => (capabilities & CapBreathing) != 0 || capabilities == 0
            ? (ModeBreathing, speed, DirectionNone, colour)
            : (ModeStatic, (byte)0, DirectionNone, colour),

        LightingEffect.Spin => (capabilities & CapWave) != 0
            ? (ModeWave, speed, DirectionClockwise, colour)
            : (capabilities & CapBreathing) != 0
                ? (ModeBreathing, speed, DirectionNone, colour)
                : (ModeStatic, (byte)0, DirectionNone, colour),

        LightingEffect.Rainbow => (capabilities & CapRainbow) != 0
            ? (ModeRainbow, speed, DirectionClockwise, Rgb.Black)
            : (capabilities & CapNeon) != 0
                ? (ModeNeon, speed, DirectionNone, Rgb.Black)
                : (ModeStatic, (byte)0, DirectionNone, colour),

        _ => (ModeStatic, (byte)0, DirectionNone, colour),
    };

    private static List<Zone> DiscoverZones(
        SafeFileHandle handle, int featureLength, Dictionary<(byte ReportId, ushort Usage), int> fieldSizes)
    {
        var zones = new List<Zone>();
        var listBuf = new byte[featureLength];
        listBuf[0] = ReportListDevices;
        if (!Hid.HidD_GetFeature(handle, listBuf, (uint)listBuf.Length))
            return zones;

        var count = Math.Min((int)listBuf[1], Math.Max(0, listBuf.Length - 2));
        for (var i = 0; i < count; i++)
        {
            var devId = listBuf[2 + i];
            if (devId == 0) continue;

            var selectBuf = new byte[featureLength];
            selectBuf[0] = ReportSelectDevice;
            selectBuf[1] = devId;
            if (!Hid.HidD_SetFeature(handle, selectBuf, (uint)selectBuf.Length))
                continue;

            var infoBuf = new byte[featureLength];
            infoBuf[0] = ReportDeviceInfo;
            if (!Hid.HidD_GetFeature(handle, infoBuf, (uint)infoBuf.Length) || infoBuf[1] != devId)
                continue;

            var (ledCount, caps) = ParseDeviceInfo(infoBuf, fieldSizes, devId);
            zones.Add(new Zone(devId, ledCount, caps));
        }

        return zones;
    }

    /// <summary>
    /// Parses <c>0xA3</c> base info using descriptor field sizes when available, defaulting to the verified Atlas 8
    /// 9-byte layout (<c>[0xA3, devId, u34(1), u32(1), u35(1), u31(4)]</c>).
    /// </summary>
    public static (int LedCount, uint Capabilities) ParseDeviceInfo(
        ReadOnlySpan<byte> info, Dictionary<(byte ReportId, ushort Usage), int>? fieldSizes, byte devId)
    {
        var sz34 = fieldSizes?.GetValueOrDefault((ReportDeviceInfo, (ushort)0x34), 1) ?? 1;
        var sz32 = fieldSizes?.GetValueOrDefault((ReportDeviceInfo, (ushort)0x32), 1) ?? 1;
        var sz35 = fieldSizes?.GetValueOrDefault((ReportDeviceInfo, (ushort)0x35), 1) ?? 1;
        var sz33 = fieldSizes is not null && fieldSizes.TryGetValue((ReportDeviceInfo, (ushort)0x33), out var v33) ? v33 : 0;
        var sz31 = fieldSizes?.GetValueOrDefault((ReportDeviceInfo, (ushort)0x31), 4) ?? 4;

        var pos = 2 + sz34 + sz32;
        int ledCount = 1;
        if (pos + sz35 <= info.Length)
        {
            ledCount = sz35 >= 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(info.Slice(pos, 2))
                : info[pos];
        }
        pos += sz35 + sz33;

        uint caps = DefaultCapabilitiesFor(devId);
        if (sz31 >= 4 && pos + 4 <= info.Length)
            caps = BinaryPrimitives.ReadUInt32LittleEndian(info.Slice(pos, 4));

        return (ledCount, caps);
    }

    private static uint DefaultCapabilitiesFor(byte devId) =>
        devId == DeviceIdSticks
            ? CapStatic | CapBlinking | CapBreathing | CapNeon | CapWave | CapRainbow
            : CapStatic | CapBreathing | CapNeon;

    private static bool SendWithRetry(SafeFileHandle handle, byte[] report)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            if (Hid.HidD_SetFeature(handle, report, (uint)report.Length))
                return true;

            if (attempt + 1 < MaxRetries)
                Thread.Sleep(RetryDelayMs);
        }

        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var endpoint in _endpoints) endpoint.Dispose();
            _endpoints.Clear();
        }
    }
}
