using System.Runtime.InteropServices;
using Tsume.Upscaling;

namespace Tsume.App.Platform.Rtss;

/// <summary>
/// Lightweight Windows PDH sampler for <c>\Energy Meter(*)\Power</c> (milliwatts),
/// <c>\Processor Information(_Total)\% Processor Performance</c> (multiplied by the CPU's nominal clock from
/// <c>CallNtPowerInformation</c>, matching Windows Task Manager's live turbo clock), and
/// <c>\GPU Adapter Memory(*)\Dedicated Usage</c> + <c>Shared Usage</c> (matching Windows Task Manager's GPU memory
/// on unified-memory handhelds such as Intel Arc iGPUs and AMD APUs).
/// Opened lazily only when detailed overlay levels are active, and closed immediately when turned off.
/// </summary>
public sealed class EnergyMeterTelemetry : IDisposable
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhCStatusValidData = 0x00000000;
    private const uint PdhCStatusNewData = 0x00000001;
    private const int ProcessorInformationLevel = 11;
    private const double BytesPerMiB = 1024.0 * 1024.0;
    private const double MaxSaneGpuBytes = 1024.0 * 1024.0 * 1024.0 * 1024.0; // 1 TiB

    // Sampled on the telemetry thread, reset from whichever thread changes the overlay.
    private readonly Lock _gate = new();

    private nint _query;
    private nint _powerCounter;
    private nint _cpuPerfCounter;
    private nint _gpuDedicatedMemCounter;
    private nint _gpuSharedMemCounter;
    private uint _baseClockMhz;
    private string? _primaryGpuLuidPrefix;
    private bool _initialized;
    private bool _cpuPerfPrimed;

    /// <summary>
    /// Reads CPU/GPU power in Watts, effective CPU clock in MHz, and primary GPU memory usage in MiB,
    /// or null for any unavailable counter.
    /// </summary>
    public (double? CpuWatts, double? GpuWatts, double? CpuClockMhz, double? GpuVramMb) Sample()
    {
        using var held = _gate.EnterScope();

        EnsureOpen();
        if (_query == 0) return (null, null, null, null);

        if (PdhCollectQueryData(_query) != 0) return (null, null, null, null);

        var (cpuW, gpuW) = _powerCounter != 0 ? ReadPowerRails() : (null, null);
        var cpuMhz = ReadCpuClockMhz();
        var gpuVramMb = ReadGpuVramMb();
        return (cpuW, gpuW, cpuMhz, gpuVramMb);
    }

    /// <summary>Releases PDH handles when the overlay is turned off or set to FPS-only.</summary>
    public void Reset() => Close();

    public void Dispose() => Close();

    private void EnsureOpen()
    {
        if (_initialized) return;
        _initialized = true;

        if (_baseClockMhz == 0)
            _baseClockMhz = ReadBaseClockMhz();

        if (_primaryGpuLuidPrefix is null && GpuVendor.GetPrimaryAdapterLuid() is { } luid && luid != 0)
        {
            var low = unchecked((uint)luid);
            var high = unchecked((uint)(luid >> 32));
            _primaryGpuLuidPrefix = $"luid_0x{high:x8}_0x{low:x8}_";
        }

        if (PdhOpenQueryW(null, 0, out _query) != 0)
        {
            _query = 0;
            return;
        }

        // English counter names work regardless of Windows display language.
        if (PdhAddEnglishCounterW(_query, @"\Energy Meter(*)\Power", 0, out _powerCounter) != 0)
            _powerCounter = 0;

        if (PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Performance", 0, out _cpuPerfCounter) != 0)
            _cpuPerfCounter = 0;

        if (PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _gpuDedicatedMemCounter) != 0)
            _gpuDedicatedMemCounter = 0;

        if (PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Shared Usage", 0, out _gpuSharedMemCounter) != 0)
            _gpuSharedMemCounter = 0;

        if (_powerCounter == 0 && _cpuPerfCounter == 0 && _gpuDedicatedMemCounter == 0 && _gpuSharedMemCounter == 0)
        {
            PdhCloseQuery(_query);
            _query = 0;
        }
    }

    private void Close()
    {
        using var held = _gate.EnterScope();

        if (_query != 0)
        {
            PdhCloseQuery(_query);
            _query = 0;
        }

        _powerCounter = 0;
        _cpuPerfCounter = 0;
        _gpuDedicatedMemCounter = 0;
        _gpuSharedMemCounter = 0;
        _initialized = false;
        _cpuPerfPrimed = false;
    }

    private double? ReadCpuClockMhz()
    {
        if (_cpuPerfCounter == 0 || _baseClockMhz == 0) return null;

        if (!_cpuPerfPrimed)
        {
            // % Processor Performance is a rate counter and requires two collections before returning a delta.
            _cpuPerfPrimed = true;
            return null;
        }

        if (PdhGetFormattedCounterValue(_cpuPerfCounter, PdhFmtDouble, out _, out var value) != 0)
            return null;

        if (value.CStatus is not (PdhCStatusValidData or PdhCStatusNewData))
            return null;

        var pct = value.DoubleValue;
        if (double.IsNaN(pct) || double.IsInfinity(pct) || pct <= 0 || pct > 1000)
            return null;

        var mhz = _baseClockMhz * (pct / 100.0);
        return mhz is >= 100 and <= 10_000 ? mhz : null;
    }

    private static unsafe uint ReadBaseClockMhz()
    {
        var cores = Math.Max(1, Environment.ProcessorCount);
        var bytes = cores * sizeof(ProcessorPowerInformation);
        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            if (CallNtPowerInformation(ProcessorInformationLevel, 0, 0, buffer, (uint)bytes) != 0)
                return 0;

            var items = (ProcessorPowerInformation*)buffer;
            uint maxMhz = 0;
            for (var i = 0; i < cores; i++)
            {
                if (items[i].MaxMhz > maxMhz) maxMhz = items[i].MaxMhz;
            }
            return maxMhz;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private unsafe (double? CpuWatts, double? GpuWatts) ReadPowerRails()
    {
        uint bufferSize = 0;
        var status = PdhGetFormattedCounterArrayW(_powerCounter, PdhFmtDouble, ref bufferSize, out var count, 0);
        if (status != PdhMoreData || bufferSize == 0) return (null, null);

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (PdhGetFormattedCounterArrayW(_powerCounter, PdhFmtDouble, ref bufferSize, out count, buffer) != 0)
                return (null, null);

            double? pp0 = null;
            double? pp1 = null;
            double? pkg = null;

            var items = (PdhCounterItemDouble*)buffer;
            for (var i = 0; i < count; i++)
            {
                if (items[i].CStatus is not (PdhCStatusValidData or PdhCStatusNewData)) continue;

                var mw = items[i].Value;
                if (double.IsNaN(mw) || double.IsInfinity(mw) || mw < 0 || mw > 500_000) continue;

                var name = Marshal.PtrToStringUni(items[i].Name);
                if (string.IsNullOrEmpty(name)) continue;

                var watts = mw / 1000.0;
                if (name.Equals("rapl_package0_pp0", StringComparison.OrdinalIgnoreCase)
                    || (pp0 is null && name.EndsWith("_pp0", StringComparison.OrdinalIgnoreCase)))
                {
                    pp0 = watts;
                }
                else if (name.Equals("rapl_package0_pp1", StringComparison.OrdinalIgnoreCase)
                    || (pp1 is null && name.EndsWith("_pp1", StringComparison.OrdinalIgnoreCase)))
                {
                    pp1 = watts;
                }
                else if (name.Equals("rapl_package0_pkg", StringComparison.OrdinalIgnoreCase)
                    || (pkg is null && name.EndsWith("_pkg", StringComparison.OrdinalIgnoreCase)))
                {
                    pkg = watts;
                }
            }

            return (pp0 ?? pkg, pp1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private double? ReadGpuVramMb()
    {
        if (_gpuDedicatedMemCounter == 0 && _gpuSharedMemCounter == 0) return null;

        var totalsByInstance = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_gpuDedicatedMemCounter != 0)
            AccumulateGpuMemoryCounter(_gpuDedicatedMemCounter, totalsByInstance);
        if (_gpuSharedMemCounter != 0)
            AccumulateGpuMemoryCounter(_gpuSharedMemCounter, totalsByInstance);

        if (totalsByInstance.Count == 0) return null;

        if (_primaryGpuLuidPrefix is { } prefix)
        {
            var matched = false;
            var primaryBytes = 0.0;
            foreach (var (instance, bytes) in totalsByInstance)
            {
                if (instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    primaryBytes += bytes;
                    matched = true;
                }
            }

            if (matched)
                return primaryBytes > 0 ? primaryBytes / BytesPerMiB : null;
        }

        // Fallback if DXGI LUID was unavailable or changed (e.g. driver reset): pick the busiest adapter.
        var maxBytes = 0.0;
        foreach (var bytes in totalsByInstance.Values)
        {
            if (bytes > maxBytes) maxBytes = bytes;
        }

        return maxBytes > 0 ? maxBytes / BytesPerMiB : null;
    }

    private static unsafe void AccumulateGpuMemoryCounter(nint counter, Dictionary<string, double> totalsByInstance)
    {
        uint bufferSize = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bufferSize, out var count, 0);
        if (status != PdhMoreData || bufferSize == 0) return;

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bufferSize, out count, buffer) != 0)
                return;

            var items = (PdhCounterItemDouble*)buffer;
            for (var i = 0; i < count; i++)
            {
                if (items[i].CStatus is not (PdhCStatusValidData or PdhCStatusNewData)) continue;

                var bytes = items[i].Value;
                if (double.IsNaN(bytes) || double.IsInfinity(bytes) || bytes < 0 || bytes > MaxSaneGpuBytes) continue;

                var name = Marshal.PtrToStringUni(items[i].Name);
                if (string.IsNullOrEmpty(name)) continue;

                totalsByInstance[name] = totalsByInstance.TryGetValue(name, out var existing)
                    ? existing + bytes
                    : bytes;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PdhCounterItemDouble
    {
        [FieldOffset(0)] public nint Name;
        [FieldOffset(8)] public uint CStatus;
        [FieldOffset(16)] public double Value;
    }

    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(
        int informationLevel, nint inputBuffer, uint inputBufferLength, nint outputBuffer, uint outputBufferLength);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string fullCounterPath, nuint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(
        nint counter, uint format, out uint type, out PdhFmtCounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
