using System.Runtime.InteropServices;
using System.ServiceProcess;
using Tsume.Hardware;

namespace Tsume.Helper;

/// <summary>
/// The helper as an installed Windows service. It answers the pipe and keeps the chosen mode and charge
/// limit in place across restarts and sleep (<see cref="ModeKeeper"/>, <see cref="ChargeKeeper"/>).
/// </summary>
public sealed class HelperService : ServiceBase
{
    /// <summary>The service name. The installer must use the same one.</summary>
    public const string Name = "TsumeHelper";

    private static readonly TimeSpan StopPatience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartOver = TimeSpan.FromSeconds(5);

    private const int BoardAttempts = 30;
    private static readonly TimeSpan BoardPause = TimeSpan.FromSeconds(2);

    private CancellationTokenSource? _stop;
    private Task? _serving;
    private volatile ModeKeeper? _keeper;
    private volatile ChargeKeeper? _charge;
    private nint _screenWatch;

    public HelperService()
    {
        ServiceName = Name;
        CanStop = true;
        CanShutdown = true;
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
        AutoLog = false;   // the helper keeps its own log
    }

    protected override void OnStart(string[] args)
    {
        _stop = new CancellationTokenSource();
        var stop = _stop.Token;
        PanelStarter.SetServiceStop(stop);
        _serving = Task.Run(() => ServeAsync(stop));

        // Modern standby: the machine idles with the screen off and Windows sends no wake message,
        // so the screen coming back on stands in for one.
        var screenState = ScreenState;
        _screenWatch = RegisterPowerSettingNotification(ServiceHandle, ref screenState, ToAService);

        // Someone may have signed in before the service was listening (a fast boot, or an update of Tsume).
        Task.Run(() => PanelStarter.StartFor(PanelStarter.ScreenSession, "start-up", stop));
    }

    /// <summary>Starts the panel as someone signs in or unlocks, ahead of Windows' own start-up apps.</summary>
    protected override void OnSessionChange(SessionChangeDescription change)
    {
        var occasion = change.Reason switch
        {
            SessionChangeReason.SessionLogon => "sign-in",
            SessionChangeReason.SessionUnlock => "unlock",
            SessionChangeReason.ConsoleConnect => "console",
            _ => null,
        };
        if (occasion is null) return;

        var session = change.SessionId;
        var stop = _stop?.Token ?? CancellationToken.None;
        Task.Run(() => PanelStarter.StartFor(session, occasion, stop));
    }

    protected override void OnStop() => StopServing();

    protected override void OnShutdown() => StopServing();

    /// <summary>
    /// On waking from hibernation, and whenever the screen goes on or off, re-checks the firmware against
    /// what was chosen. On wake, also makes sure the panel is still running (handles wake without sign-in). On a
    /// board whose limits differ on battery, also on a switch between wall power and battery.
    /// </summary>
    protected override bool OnPowerEvent(PowerBroadcastStatus status)
    {
        var isWake = status is PowerBroadcastStatus.ResumeAutomatic
            or PowerBroadcastStatus.ResumeSuspend
            or PowerBroadcastStatus.ResumeCritical;

        var occasion = isWake
            ? "wake"
            : status == (PowerBroadcastStatus)PowerSettingChanged
                ? "screen on or off"
                : null;

        // Answer Windows at once; the firmware is talked to on another thread.
        if (occasion is not null && _keeper is { } keeper && _charge is { } charge)
            Task.Run(() =>
            {
                keeper.RestoreIfForgotten(occasion);
                charge.RestoreIfForgotten(occasion);
            });

        // A switch between wall power and battery (Windows sends this for battery level changes too). Only boards
        // whose limits differ on battery act on it; on any other board nothing is done here.
        if (status == PowerBroadcastStatus.PowerStatusChange && _keeper is { FollowsPowerSource: true } sourceKeeper)
            Task.Run(sourceKeeper.OnPowerStatusChange);

        if (isWake)
        {
            var stop = _stop?.Token ?? CancellationToken.None;
            Task.Run(() => PanelStarter.EnsureRunningAfterWake(PanelStarter.ScreenSession, stop));
        }

        return true;
    }

    /// <summary>Serves until stopped. A failure is logged and serving starts over, so the service is never running yet deaf.</summary>
    private async Task ServeAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await ServeOnceAsync(stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Logged where it happened.
            }

            try { await Task.Delay(StartOver, stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Early in a boot WMI may not answer yet; an unnamed board is asked for again before it is believed.</summary>
    private static async Task<(Board, IDevice?)> OpenDeviceAsync(CancellationToken stop)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (machine, device) = DeviceCatalog.Open();
            if (machine.IsNamed || attempt == BoardAttempts) return (machine, device);

            await Task.Delay(BoardPause, stop);
        }
    }

    private async Task ServeOnceAsync(CancellationToken stop)
    {
        var handler = new RequestHandler();
        IDevice? ownedDevice = null;

        // Start the pipe immediately so UIAccess panel launches never time out waiting on slow early-boot WMI.
        var hardwareReady = Task.Run(async () =>
        {
            try
            {
                var (machine, found) = await OpenDeviceAsync(stop);
                ownedDevice = found;
                if (stop.IsCancellationRequested) return;

                Log.Write($"Running as a service on {machine}{(found is null ? " — not a supported device; every request will be refused" : $": {found.Profile.Name}")}.");

                var keeper = found is null ? null : new ModeKeeper(found);
                var charge = found is null ? null : new ChargeKeeper(found);

                keeper?.RestoreIfForgotten("start-up");
                charge?.RestoreIfForgotten("start-up");
                _charge = charge;
                _keeper = keeper;
                handler.SetHardware(machine, found, keeper, charge);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                Log.Write($"Hardware discovery failed: {error}");
            }
        }, stop);

        try
        {
            await new PipeServer(handler).RunAsync(stop);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Without this the service would die silently and Windows would only say "stopped".
            Log.Write($"The service failed: {error}");
            throw;
        }
        finally
        {
            _keeper = null;
            _charge = null;
            try { await hardwareReady; }
            catch (OperationCanceledException) { }
            ownedDevice?.Dispose();
        }
    }

    private void StopServing()
    {
        if (_screenWatch != 0) UnregisterPowerSettingNotification(_screenWatch);
        _screenWatch = 0;

        _stop?.Cancel();

        try { _serving?.Wait(StopPatience); }
        catch (AggregateException) { /* already logged where it happened */ }

        _stop?.Dispose();
        _stop = null;
    }

    /// <summary>GUID_CONSOLE_DISPLAY_STATE: the screen turned on, off, or dimmed.</summary>
    private static readonly Guid ScreenState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private const int ToAService = 1;                  // DEVICE_NOTIFY_SERVICE_HANDLE
    private const int PowerSettingChanged = 0x8013;    // PBT_POWERSETTINGCHANGE

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid setting, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(nint handle);
}
