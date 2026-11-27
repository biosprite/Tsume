using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Tsume.App.Platform.Interop;
using Tsume.Core.Devices;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Platform.Rtss;

/// <summary>Who already owns RTSS's <c>AIDA64_SensorValues</c> shared memory when Tsume cannot claim it.</summary>
public enum AidaConflictKind
{
    None = 0,
    Aida64 = 1,
    Winhanced = 2,
    Other = 3,
}

/// <summary>Snapshot of values Tsume publishes into <c>AIDA64_SensorValues</c> for RTSS's OverlayEditor plugin.</summary>
public readonly record struct AidaSensorSnapshot(
    int OverlayLevel,
    int? CpuTempC = null,
    double? CpuPowerWatts = null,
    double? GpuPowerWatts = null,
    double? CpuClockMhz = null,
    double? GpuVramMb = null,
    int TempWarnC = DeviceProfile.DefaultTempWarnC,
    int TempHotC = DeviceProfile.DefaultTempHotC,
    int? PosX = null,
    int? PosY = null);

/// <summary>
/// Publishes Tsume's live overlay level and missing handheld telemetry (EC temperature and RAPL power) into the
/// <c>AIDA64_SensorValues</c> shared-memory block that RTSS OverlayEditor's built-in AIDA provider polls.
/// Never fights another owner: if real AIDA64 or a Winhanced leftover already holds <c>AIDA64_SensorValues</c>,
/// Tsume leaves it untouched and reports the conflict so the UI can tell the user.
/// </summary>
public sealed class AidaSensorBridge : IDisposable
{
    private const string MappingName = "AIDA64_SensorValues";
    private const uint MappingSize = 16384;
    private const uint PageReadWrite = 0x04;
    private const uint FileMapAllAccess = 0x000F001F;
    private const uint FileMapRead = 0x0004;
    private const int ErrorAlreadyExists = 183;

    private static readonly Journal Log = new("rtss");

    private readonly Lock _gate = new();
    private nint _mapping;
    private nint _view;
    private AidaConflictKind _conflict;
    private bool _disposed;

    public AidaConflictKind Conflict
    {
        get { lock (_gate) return _conflict; }
    }

    public bool IsOwned
    {
        get { lock (_gate) return _view != 0; }
    }

    /// <summary>
    /// Ensures Tsume owns <c>AIDA64_SensorValues</c> and writes <paramref name="snapshot"/> in the exact
    /// level-0 XML format expected by OverlayEditor's <c>CAIDA64Parser</c>. Returns false when another
    /// application already owns the mapping (conflict recorded in <see cref="Conflict"/>).
    /// </summary>
    public bool TryPublish(AidaSensorSnapshot snapshot, string? rtssFolder = null)
    {
        lock (_gate)
        {
            if (_disposed) return false;

            if (_view == 0 && !TryAcquireLocked(rtssFolder))
                return false;

            WriteXmlLocked(FormatXml(snapshot));
            return true;
        }
    }

    /// <summary>
    /// Clears the overlay level to 0 and closes Tsume's handle to <c>AIDA64_SensorValues</c>, so OverlayEditor
    /// immediately sees the mapping disappear and hides all Tsume layers. Never touches a mapping Tsume does not own.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            ReleaseLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseLocked();
        }
    }

    private bool TryAcquireLocked(string? rtssFolder)
    {
        var handle = CreateFileMappingA(new nint(-1), 0, PageReadWrite, 0, MappingSize, MappingName);
        var lastError = Marshal.GetLastWin32Error();
        if (handle == 0)
        {
            Log.Write($"CreateFileMapping({MappingName}) failed (Win32 error {lastError})");
            return false;
        }

        if (lastError == ErrorAlreadyExists)
        {
            if (IsOrphanedTsumeMapping(handle))
            {
                var reclaimedView = MapViewOfFile(handle, FileMapAllAccess, 0, 0, MappingSize);
                if (reclaimedView != 0)
                {
                    _mapping = handle;
                    _view = reclaimedView;
                    if (_conflict != AidaConflictKind.None)
                        _conflict = AidaConflictKind.None;
                    Log.Write($"reclaimed orphaned {MappingName} mapping");
                    return true;
                }
            }

            var kind = IdentifyExistingOwner(handle, rtssFolder);
            Kernel32.CloseHandle(handle);

            if (_conflict != kind)
            {
                _conflict = kind;
                Log.Write($"{MappingName} is already owned by {kind}; leaving it untouched");
            }
            return false;
        }

        var view = MapViewOfFile(handle, FileMapAllAccess, 0, 0, MappingSize);
        if (view == 0)
        {
            var mapError = Marshal.GetLastWin32Error();
            Kernel32.CloseHandle(handle);
            Log.Write($"MapViewOfFile({MappingName}) failed (Win32 error {mapError})");
            return false;
        }

        _mapping = handle;
        _view = view;
        if (_conflict != AidaConflictKind.None)
        {
            _conflict = AidaConflictKind.None;
            Log.Write($"acquired {MappingName} after previous conflict cleared");
        }
        return true;
    }

    private void ReleaseLocked()
    {
        if (_view != 0)
        {
            // Write level 0 before unmapping in case OverlayEditor holds a transient handle during this tick.
            WriteXmlLocked(FormatXml(new AidaSensorSnapshot(OverlayLevel: 0)));
            UnmapViewOfFile(_view);
            _view = 0;
        }

        if (_mapping != 0)
        {
            Kernel32.CloseHandle(_mapping);
            _mapping = 0;
        }
    }

    private unsafe void WriteXmlLocked(string xml)
    {
        if (_view == 0) return;

        var maxBytes = (int)MappingSize - 1;
        var bytes = Encoding.ASCII.GetBytes(xml);
        var length = Math.Min(bytes.Length, maxBytes);

        var dest = (byte*)_view;
        fixed (byte* src = bytes)
        {
            Buffer.MemoryCopy(src, dest, maxBytes, length);
        }
        dest[length] = 0;
    }

    private static string FormatXml(AidaSensorSnapshot s)
    {
        // AIDA64Parser.cpp expects flat level-0 sensor tags with NO outer root element:
        //   <sys><id>...</id><label>...</label><value>...</value></sys>
        var sb = new StringBuilder(384);
        AppendSensor(sb, "sys", "TSUME_OVERLAY_LEVEL", "Overlay Level",
            s.OverlayLevel.ToString(CultureInfo.InvariantCulture));
        AppendSensor(sb, "sys", "TSUME_TEMP_WARN", "Temp Warn",
            s.TempWarnC.ToString(CultureInfo.InvariantCulture));
        AppendSensor(sb, "sys", "TSUME_TEMP_HOT", "Temp Hot",
            s.TempHotC.ToString(CultureInfo.InvariantCulture));

        if (s.CpuTempC is { } cpuTemp)
        {
            AppendSensor(sb, "temp", "TSUME_CPU_TEMP", "CPU Temp",
                cpuTemp.ToString(CultureInfo.InvariantCulture));
        }

        if (s.CpuPowerWatts is { } cpuW)
        {
            AppendSensor(sb, "pwr", "TSUME_CPU_POWER", "CPU Power",
                cpuW.ToString("F1", CultureInfo.InvariantCulture));
        }

        if (s.GpuPowerWatts is { } gpuW)
        {
            AppendSensor(sb, "pwr", "TSUME_GPU_POWER", "GPU Power",
                gpuW.ToString("F1", CultureInfo.InvariantCulture));
        }

        if (s.CpuClockMhz is { } cpuMhz)
        {
            AppendSensor(sb, "sys", "TSUME_CPU_CLOCK", "CPU Clock",
                cpuMhz.ToString("F0", CultureInfo.InvariantCulture));
        }

        if (s.GpuVramMb is { } gpuVramMb)
        {
            AppendSensor(sb, "sys", "TSUME_GPU_VRAM", "GPU VRAM",
                gpuVramMb.ToString("F0", CultureInfo.InvariantCulture));
        }

        if (s.PosX is { } posX)
        {
            AppendSensor(sb, "sys", "TSUME_POS_X", "Overlay Pos X",
                Math.Max(0, posX).ToString(CultureInfo.InvariantCulture));
        }

        if (s.PosY is { } posY)
        {
            AppendSensor(sb, "sys", "TSUME_POS_Y", "Overlay Pos Y",
                Math.Max(0, posY).ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static void AppendSensor(StringBuilder sb, string tag, string id, string label, string value)
    {
        sb.Append('<').Append(tag).Append('>')
          .Append("<id>").Append(id).Append("</id>")
          .Append("<label>").Append(label).Append("</label>")
          .Append("<value>").Append(value).Append("</value>")
          .Append("</").Append(tag).Append('>');
    }

    /// <summary>
    /// True when an existing mapping already contains Tsume's own sensor payload (e.g. left open briefly by
    /// RTSS OverlayEditor after an abrupt crash), so Tsume can safely reclaim it instead of flagging a conflict.
    /// </summary>
    private static bool IsOrphanedTsumeMapping(nint existingMapping)
    {
        var snippet = ReadSnippet(existingMapping);
        return snippet.Contains("<id>TSUME_OVERLAY_LEVEL</id>", StringComparison.Ordinal)
            && !snippet.Contains("WINHANCED_", StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe string ReadSnippet(nint existingMapping)
    {
        var readView = MapViewOfFile(existingMapping, FileMapRead, 0, 0, 0);
        if (readView == 0) return "";

        try
        {
            var ptr = (byte*)readView;
            var len = 0;
            while (len < 512 && ptr[len] != 0) len++;
            return len > 0 ? Encoding.ASCII.GetString(ptr, len) : "";
        }
        catch
        {
            return "";
        }
        finally
        {
            UnmapViewOfFile(readView);
        }
    }

    private static AidaConflictKind IdentifyExistingOwner(nint existingMapping, string? rtssFolder)
    {
        var snippet = ReadSnippet(existingMapping);

        if (snippet.Contains("WINHANCED_", StringComparison.OrdinalIgnoreCase)
            || IsProcessRunning("Winhanced")
            || IsWinhancedPluginEnabled(rtssFolder))
        {
            return AidaConflictKind.Winhanced;
        }

        if (IsProcessRunning("aida64")
            || snippet.Contains("<id>TCPU", StringComparison.OrdinalIgnoreCase)
            || snippet.Contains("<id>SCPU", StringComparison.OrdinalIgnoreCase))
        {
            return AidaConflictKind.Aida64;
        }

        return AidaConflictKind.Other;
    }

    private static bool IsProcessRunning(string name)
    {
        try
        {
            var procs = Process.GetProcessesByName(name);
            var any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return any;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWinhancedPluginEnabled(string? rtssFolder)
    {
        if (string.IsNullOrWhiteSpace(rtssFolder)) return false;

        try
        {
            var cfg = Path.Combine(rtssFolder, "Profiles", "Config");
            if (!File.Exists(cfg)) return false;

            var inPlugins = false;
            foreach (var raw in File.ReadLines(cfg, Encoding.Latin1))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inPlugins = line.Equals("[Plugins]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inPlugins) continue;
                var eq = line.IndexOf('=');
                if (eq > 0
                    && line[..eq].Trim().Equals("WinhancedOverlayBridge.dll", StringComparison.OrdinalIgnoreCase)
                    && line[(eq + 1)..].Trim() == "1")
                {
                    return true;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern nint CreateFileMappingA(
        nint hFile, nint lpAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint MapViewOfFile(
        nint hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, nuint dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(nint lpBaseAddress);
}
