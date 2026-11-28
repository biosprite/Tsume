using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Tsume.App.Platform.Interop;
using Tsume.Core.Diagnostics;
using Tsume.Core.Lighting;

namespace Tsume.App.Platform;

/// <summary>
/// Strictly read-only HID bus, XInput, Windows LampArray, and vendor controller inventory.
/// Uses zero-access metadata handles so exclusive keyboards/mice/touchpads are enumerated too.
/// </summary>
public static partial class HidInventory
{
    public static async Task<HidTopologySnapshot> CaptureAsync(bool readVendorProfiles = true)
    {
        var collections = new List<HidCollectionInfo>();
        var hjcCandidates = new HashSet<(ushort Vid, ushort Pid)>();

        foreach (var path in Hid.DevicePaths())
        {
            using var meta = Hid.OpenMetadataOnly(path);
            if (meta is null) continue;

            var attrs = Hid.Attributes(meta);
            var full = Hid.DescribeAll(meta);
            if (attrs is null && full is null) continue;

            var vid = attrs?.VendorID ?? 0;
            var pid = attrs?.ProductID ?? 0;
            var ver = attrs?.VersionNumber ?? 0;

            bool canRw;
            using (var rw = Hid.OpenReadWrite(path))
                canRw = rw is not null;

            var caps = full?.Caps ?? default;
            var inIds = full is { } f1 ? f1.InputReports.Order().ToList() : [];
            var outIds = full is { } f2 ? f2.OutputReports.Order().ToList() : [];
            var featIds = full is { } f3 ? f3.FeatureReports.Order().ToList() : [];

            if (IsKnownHjcVendor(vid) && canRw && outIds.Contains(0x0F)
                && caps.OutputReportByteLength == 64 && caps.InputReportByteLength == 64)
            {
                hjcCandidates.Add((vid, pid));
            }

            collections.Add(new HidCollectionInfo
            {
                VendorId = vid,
                ProductId = pid,
                VersionBcd = ver,
                VersionText = $"{ver >> 8:X}.{ver & 0xFF:X2}",
                InterfaceNumber = MatchGroup(MiRegex(), path),
                CollectionNumber = MatchGroup(ColRegex(), path),
                Manufacturer = PrivacyScrubber.ScrubText(Hid.ManufacturerString(meta)),
                Product = PrivacyScrubber.ScrubText(Hid.ProductString(meta)),
                UsagePage = caps.UsagePage,
                Usage = caps.Usage,
                InputReportLength = caps.InputReportByteLength,
                OutputReportLength = caps.OutputReportByteLength,
                FeatureReportLength = caps.FeatureReportByteLength,
                InputReportIds = inIds,
                OutputReportIds = outIds,
                FeatureReportIds = featIds,
                CanOpenReadWrite = canRw,
                ScrubbedPath = PrivacyScrubber.ScrubDevicePath(path),
            });
        }

        var activeXInput = Enumerable.Range(0, XInput.MaxControllers)
            .Where(slot => XInput.XInputGetState((uint)slot, out _) == XInput.ERROR_SUCCESS)
            .ToList();

        var lampArrays = Hid.LampArrayDevicePaths()
            .Select(PrivacyScrubber.ScrubDevicePath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var vendorControllers = new List<VendorControllerSnapshot>();
        if (readVendorProfiles)
        {
            foreach (var (vid, pid) in hjcCandidates.OrderBy(x => x.Vid).ThenBy(x => x.Pid))
            {
                if (await TryReadHjcControllerAsync(vid, pid) is { } snap)
                    vendorControllers.Add(snap);
            }
        }

        return new HidTopologySnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Collections = collections
                .OrderBy(c => c.VendorId)
                .ThenBy(c => c.ProductId)
                .ThenBy(c => c.InterfaceNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.CollectionNumber, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            XInputActiveSlots = activeXInput,
            LampArrayDevices = lampArrays,
            VendorControllers = vendorControllers,
        };
    }

    private static bool IsKnownHjcVendor(ushort vid) =>
        vid == ClawController.MsiVendorId || ClawController.AcerVendorIds.Contains(vid);

    private static async Task<VendorControllerSnapshot?> TryReadHjcControllerAsync(ushort vid, ushort pid)
    {
        using var controller = ClawController.Find(vid, [pid]);
        if (controller is null) return null;

        var mode = await controller.ReadModeAsync();
        var profile = await controller.ReadProfileRangeAsync(0, ProfileMap.Length);

        string? sha = null;
        string? hex = null;
        string? summary = null;

        if (profile is not null)
        {
            sha = Convert.ToHexString(SHA256.HashData(profile)).ToLowerInvariant();
            hex = Convert.ToHexString(profile);
            var lighting = StickLighting.Decode(profile.AsSpan(ProfileMap.ActiveAnimation, ProfileMap.AnimationLength));
            summary = $"lighting={lighting}";
        }

        return new VendorControllerSnapshot
        {
            Protocol = "msi-hid-v1",
            VendorId = vid,
            ProductId = pid,
            FirmwareVersion = controller.FirmwareVersion,
            ModeByte = mode?.Mode,
            MKeysByte = mode?.MKeys,
            ProfileLength = profile?.Length ?? 0,
            ProfileSha256 = sha,
            ProfileHex = hex,
            Summary = summary,
        };
    }

    private static string? MatchGroup(Regex regex, string input)
    {
        var m = regex.Match(input);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"mi_([0-9a-f]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex MiRegex();

    [GeneratedRegex(@"col([0-9a-f]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex ColRegex();
}
