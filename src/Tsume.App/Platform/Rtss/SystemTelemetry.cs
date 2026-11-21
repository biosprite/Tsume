using System.Runtime.InteropServices;

namespace Tsume.App.Platform.Rtss;

/// <summary>One snapshot of host telemetry used to render Tsume's Steam-style RTSS overlay.</summary>
public sealed record TelemetryFrame
{
    public int? CpuPercent { get; init; }
    public int? CpuTempC { get; init; }
    public int? FanRpm { get; init; }
    public int? GpuPercent { get; init; }
    public double? VramUsedGb { get; init; }
    public double? RamUsedGb { get; init; }
    public double? RamTotalGb { get; init; }
    public Battery.Reading? Battery { get; init; }
    public string? PowerModeLabel { get; init; }
    public int? SustainedWatts { get; init; }
    public int? BoostWatts { get; init; }
    public float? LiveFps { get; init; }
    public float? LiveFrameTimeMs { get; init; }
    public DateTime Now { get; init; } = DateTime.Now;
}

/// <summary>
/// Lightweight, zero-dependency system telemetry sampler (CPU %, GPU 3D %, VRAM, RAM, battery).
/// Lazily opens Windows PDH GPU counters only while Tsume's Steam-style overlay is active,
/// and releases them immediately when stopped so idle overhead is zero.
/// </summary>
public sealed class SystemTelemetry : IDisposable
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtLarge = 0x00000400;
    private const uint PdhCStatusValidData = 0x00000000;
    private const uint PdhCStatusNewData = 0x00000001;

    private bool _hasCpuSample;
    private long _prevIdle;
    private long _prevKernel;
    private long _prevUser;

    private nint _pdhQuery;
    private nint _gpuUtilCounter;
    private nint _gpuDedicatedCounter;
    private nint _gpuSharedCounter;
    private bool _pdhInitialized;
    private bool _pdhPrimed;

    /// <summary>Samples CPU, GPU, VRAM, RAM and battery.</summary>
    public (int? CpuPercent, int? GpuPercent, double? VramGb, double? RamUsedGb, double? RamTotalGb, Battery.Reading? Battery) Sample(bool includeGpu)
    {
        var cpu = SampleCpuPercent();
        var (ramUsed, ramTotal) = SampleMemoryGb();
        var battery = Battery.Read();

        int? gpu = null;
        double? vram = null;
        if (includeGpu)
            (gpu, vram) = SampleGpu();

        return (cpu, gpu, vram, ramUsed, ramTotal, battery);
    }

    /// <summary>Releases PDH counters when the overlay is turned off.</summary>
    public void Reset()
    {
        _hasCpuSample = false;
        ClosePdh();
    }

    public void Dispose() => ClosePdh();

    private int? SampleCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;

        if (!_hasCpuSample)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasCpuSample = true;
            return null;
        }

        var dIdle = idle - _prevIdle;
        var dKernel = kernel - _prevKernel;
        var dUser = user - _prevUser;

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        var total = dKernel + dUser;
        if (total <= 0) return null;

        var busy = Math.Max(0, total - dIdle);
        return (int)Math.Clamp(Math.Round(100.0 * busy / total), 0, 100);
    }

    private static (double? UsedGb, double? TotalGb) SampleMemoryGb()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return (null, null);

        const double gb = 1024.0 * 1024.0 * 1024.0;
        var total = status.TotalPhys / gb;
        var used = (status.TotalPhys - status.AvailPhys) / gb;
        return (Math.Max(0, used), total);
    }

    private (int? GpuPercent, double? VramGb) SampleGpu()
    {
        EnsurePdh();
        if (_pdhQuery == 0) return (null, null);

        if (PdhCollectQueryData(_pdhQuery) != 0) return (null, null);

        if (!_pdhPrimed)
        {
            // Rate counters (GPU Utilization) require two collections before returning a delta.
            _pdhPrimed = true;
            return (null, ReadVramGb());
        }

        int? gpuPercent = null;
        if (_gpuUtilCounter != 0 && TrySumDoubleCounter(_gpuUtilCounter, out var sum))
            gpuPercent = (int)Math.Clamp(Math.Round(sum), 0, 100);

        return (gpuPercent, ReadVramGb());
    }

    private double? ReadVramGb()
    {
        // Discrete GPUs report VRAM under Dedicated Usage; integrated GPUs (Arc / Radeon iGPU) use Shared Usage.
        const double gb = 1024.0 * 1024.0 * 1024.0;

        long dedicated = _gpuDedicatedCounter != 0 && TryMaxLargeCounter(_gpuDedicatedCounter, out var d) ? d : 0;
        long shared = _gpuSharedCounter != 0 && TryMaxLargeCounter(_gpuSharedCounter, out var s) ? s : 0;

        var bytes = dedicated > 64L << 20 ? dedicated : Math.Max(dedicated, shared);
        return bytes > 0 ? bytes / gb : null;
    }

    private void EnsurePdh()
    {
        if (_pdhInitialized) return;
        _pdhInitialized = true;

        if (PdhOpenQueryW(null, 0, out _pdhQuery) != 0)
        {
            _pdhQuery = 0;
            return;
        }

        // English counter names work regardless of Windows' display language.
        if (PdhAddEnglishCounterW(_pdhQuery, @"\GPU Engine(*engtype_3D)\Utilization Percentage", 0, out _gpuUtilCounter) != 0)
            _gpuUtilCounter = 0;
        if (PdhAddEnglishCounterW(_pdhQuery, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _gpuDedicatedCounter) != 0)
            _gpuDedicatedCounter = 0;
        if (PdhAddEnglishCounterW(_pdhQuery, @"\GPU Adapter Memory(*)\Shared Usage", 0, out _gpuSharedCounter) != 0)
            _gpuSharedCounter = 0;
    }

    private void ClosePdh()
    {
        if (_pdhQuery != 0)
        {
            PdhCloseQuery(_pdhQuery);
            _pdhQuery = 0;
        }

        _gpuUtilCounter = 0;
        _gpuDedicatedCounter = 0;
        _gpuSharedCounter = 0;
        _pdhInitialized = false;
        _pdhPrimed = false;
    }

    private static unsafe bool TrySumDoubleCounter(nint counter, out double total)
    {
        total = 0;
        uint bufferSize = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bufferSize, out var count, 0);
        if (status != PdhMoreData || bufferSize == 0) return false;

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bufferSize, out count, buffer) != 0)
                return false;

            var items = (PdhCounterItemDouble*)buffer;
            var any = false;
            for (var i = 0; i < count; i++)
            {
                if (items[i].CStatus is PdhCStatusValidData or PdhCStatusNewData)
                {
                    total += items[i].Value;
                    any = true;
                }
            }
            return any;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe bool TryMaxLargeCounter(nint counter, out long max)
    {
        max = 0;
        uint bufferSize = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref bufferSize, out var count, 0);
        if (status != PdhMoreData || bufferSize == 0) return false;

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref bufferSize, out count, buffer) != 0)
                return false;

            var items = (PdhCounterItemLarge*)buffer;
            var any = false;
            for (var i = 0; i < count; i++)
            {
                if (items[i].CStatus is PdhCStatusValidData or PdhCStatusNewData)
                {
                    if (items[i].Value > max) max = items[i].Value;
                    any = true;
                }
            }
            return any;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PdhCounterItemDouble
    {
        [FieldOffset(0)] public nint Name;
        [FieldOffset(8)] public uint CStatus;
        [FieldOffset(16)] public double Value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PdhCounterItemLarge
    {
        [FieldOffset(0)] public nint Name;
        [FieldOffset(8)] public uint CStatus;
        [FieldOffset(16)] public long Value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string fullCounterPath, nuint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
