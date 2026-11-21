using System.Runtime.InteropServices;
using Tsume.App.Platform.Interop;

namespace Tsume.App.Platform;

/// <summary>Charge level and the power flowing in or out of the battery.</summary>
public static class Battery
{
    /// <summary>Which way power is moving through the battery.</summary>
    public enum Flow
    {
        Unknown,

        /// <summary>Neither in nor out: whatever the machine is using comes from somewhere else.</summary>
        Resting,
        Charging,
        Draining,
    }

    /// <param name="Percent">Charge, 0–100.</param>
    /// <param name="PluggedIn">True on wall power.</param>
    /// <param name="Watts">Positive while charging, negative while draining, null when unknown.</param>
    /// <param name="Flow">Whether the battery is filling, emptying, or being left alone.</param>
    /// <param name="EstimatedMinutes">Estimated remaining runtime in minutes while draining, or null when unknown.</param>
    public readonly record struct Reading(int Percent, bool PluggedIn, double? Watts, Flow Flow, int? EstimatedMinutes = null);

    public static Reading? Read()
    {
        var size = (uint)Marshal.SizeOf<PowrProf.SYSTEM_BATTERY_STATE>();
        if (PowrProf.CallNtPowerInformation(PowrProf.SystemBatteryState, 0, 0, out var state, size) != 0) return null;
        if (state.BatteryPresent == 0 || state.MaxCapacity == 0) return null;

        var percent = (int)Math.Round(100.0 * state.RemainingCapacity / state.MaxCapacity);

        // The driver reports int.MinValue when it does not know the rate.
        double? watts = state.Rate is int.MinValue or 0 ? null : state.Rate / 1000.0;

        var flow = state.Charging != 0 ? Flow.Charging
            : state.Discharging != 0   ? Flow.Draining
            : state.Rate == 0          ? Flow.Resting
            : Flow.Unknown;

        // Windows reports uint.MaxValue (0xFFFFFFFF) when remaining time is unknown; cap at 99h to filter garbage.
        int? estimatedMinutes = flow == Flow.Draining && state.EstimatedTime is > 0 and < 360_000
            ? (int)Math.Round(state.EstimatedTime / 60.0)
            : null;

        return new Reading(Math.Clamp(percent, 0, 100), state.AcOnLine != 0, watts, flow, estimatedMinutes);
    }
}
