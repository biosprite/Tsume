using System.Runtime.InteropServices;
using Tsume.App.Platform.Interop;
using Tsume.Core.Diagnostics;
using static Tsume.App.Platform.Interop.User32;

namespace Tsume.App.Platform;

/// <summary>Reads active display resolution, refresh-rate lists (signal + desktop), and EDID panel IDs.</summary>
public static class DisplayInventory
{
    public static DisplayInventorySnapshot Capture()
    {
        var devMode = DEVMODE.Create();
        var hasMode = EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref devMode);

        var desktopRates = new SortedSet<int>();
        if (hasMode)
        {
            var probe = DEVMODE.Create();
            for (var i = 0; EnumDisplaySettings(null, i, ref probe); i++)
            {
                if (probe.PelsWidth == devMode.PelsWidth && probe.PelsHeight == devMode.PelsHeight && probe.BitsPerPel == devMode.BitsPerPel)
                    desktopRates.Add((int)probe.DisplayFrequency);
            }
        }

        return new DisplayInventorySnapshot
        {
            ActiveWidth = hasMode ? (int)devMode.PelsWidth : 0,
            ActiveHeight = hasMode ? (int)devMode.PelsHeight : 0,
            BitsPerPel = hasMode ? (int)devMode.BitsPerPel : 0,
            CurrentRefreshHz = DisplayMode.CurrentRefreshRate(),
            SignalRefreshRates = [.. DisplaySignal.Rates()],
            DesktopRefreshRates = [.. desktopRates],
            Panels = ReadActivePanels(),
        };
    }

    private static List<DisplayPanelInfo> ReadActivePanels()
    {
        var panels = new List<DisplayPanelInfo>();
        const uint flags = QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_REFRESH_RATE_AWARE;

        if (GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount) != 0 || pathCount == 0)
            return panels;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, 0) != 0)
            return panels;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pathCount; i++)
        {
            var target = paths[i].Target;
            var req = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                Type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                AdapterId = target.AdapterId,
                Id = target.Id,
            };

            if (DisplayConfigGetDeviceInfo(ref req) != 0) continue;

            var edidMaker = DecodeEdidManufacturer(req.EdidManufactureId);
            var edidProd = $"0x{req.EdidProductCodeId:X4}";
            var key = $"{edidMaker}:{edidProd}:{req.FriendlyName}";
            if (!seen.Add(key)) continue;

            panels.Add(new DisplayPanelInfo(
                string.IsNullOrWhiteSpace(req.FriendlyName) ? "Internal Display" : req.FriendlyName,
                edidMaker,
                edidProd,
                OutputTechName(req.OutputTechnology)));
        }

        return panels;
    }

    private static string DecodeEdidManufacturer(ushort id)
    {
        if (id == 0) return "0000";
        // EDID manufacturer ID is big-endian 3×5-bit letters ('A' = 1).
        var be = (ushort)((id >> 8) | (id << 8));
        char c1 = (char)(((be >> 10) & 0x1F) + 'A' - 1);
        char c2 = (char)(((be >> 5) & 0x1F) + 'A' - 1);
        char c3 = (char)((be & 0x1F) + 'A' - 1);
        var letters = new string([c1, c2, c3]);
        return letters.All(c => c is >= 'A' and <= 'Z') ? letters : $"0x{id:X4}";
    }

    private static string OutputTechName(uint tech) => tech switch
    {
        0x8000_0000 => "Internal",
        11 => "Embedded DisplayPort (eDP)",
        10 => "DisplayPort External",
        5 => "HDMI",
        6 => "LVDS",
        15 => "DisplayPort Embedded",
        _ => $"Tech(0x{tech:X})",
    };
}
