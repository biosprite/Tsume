using System.Runtime.InteropServices;
using System.ServiceProcess;
using Tsume.Hardware;

namespace Tsume.Helper;

/// <summary>
/// The helper as an installed Windows service. It answers the pipe and keeps the chosen mode, charge limit and
/// custom fan curve in place across restarts and sleep (<see cref="ModeKeeper"/>, <see cref="ChargeKeeper"/>,
/// <see cref="FanCurveKeeper"/>).
/// </summary>
public sealed class HelperService : ServiceBase
{
    /// <summary>The service name. The installer must use the same one.</summary>
    public const string Name = "TsumeHelper";

    private static readonly TimeSpan StopPatience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartOver = TimeSpan.FromSeconds(5);

    private const int BoardAttempts = 30;
    private static readonly TimeSpan BoardPause = TimeSpan.FromSeconds(2);

    private sealed class DeviceSession(PowerEventCoordinator powerEvents)
    {
        public PowerEventCoordinator PowerEvents { get; } = powerEvents;
    }

    private readonly Lock _taskGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private CancellationTokenSource? _stop;
    private CancellationToken _stopToken = CancellationToken.None;
    private Task? _serving;
    private DeviceSession? _deviceSession;
    private nint _screenWatch;
    private nint _powerSettingWatch;
    private DeviceNotifyCallbackRoutine? _displayCallback;
    private bool _usingPowerSettingCallback;

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
        var cts = new CancellationTokenSource();
        var stop = cts.Token;
        lock (_taskGate)
        {
            _stop = cts;
            _stopToken = stop;
        }

        PanelStarter.SetServiceStop(stop);
        _serving = Task.Run(() => ServeAsync(stop));

        // Modern standby: the machine idles with the screen off and Windows sends no wake message,
        // so the screen coming back on stands in for one. Prefer PowerSettingRegisterNotification with
        // DEVICE_NOTIFY_CALLBACK so we receive the actual display state (Off/On/Dimmed) instead of
        // ServiceBase.OnPowerEvent's ambiguous PBT_POWERSETTINGCHANGE notification.
        var screenState = ScreenState;
        _displayCallback = OnPowerSettingCallback;
        var subscribe = new DeviceNotifySubscribeParameters
        {
            Callback = Marshal.GetFunctionPointerForDelegate(_displayCallback),
            Context = 0,
        };
        if (PowerSettingRegisterNotification(in screenState, DeviceNotifyCallback, ref subscribe, out _powerSettingWatch) == 0
            && _powerSettingWatch != 0)
        {
            _usingPowerSettingCallback = true;
        }
        else
        {
            _displayCallback = null;
            _powerSettingWatch = 0;
            _usingPowerSettingCallback = false;
            _screenWatch = RegisterPowerSettingNotification(ServiceHandle, ref screenState, ToAService);
        }

        // Someone may have signed in before the service was listening (a fast boot, or an update of Tsume).
        RunBackground(s => PanelStarter.StartFor(PanelStarter.ScreenSession, "start-up", s));
    }

    /// <summary>Starts the panel as someone signs in or unlocks, ahead of Windows' own start-up apps.</summary>
    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        var occasion = changeDescription.Reason switch
        {
            SessionChangeReason.SessionLogon => "sign-in",
            SessionChangeReason.SessionUnlock => "unlock",
            SessionChangeReason.ConsoleConnect => "console",
            _ => null,
        };
        if (occasion is null) return;

        var session = changeDescription.SessionId;
        RunBackground(s => PanelStarter.StartFor(session, occasion, s));
    }

    protected override void OnStop() => StopServing();

    protected override void OnShutdown() => StopServing();

    /// <summary>
    /// On waking from sleep/hibernation, and on genuine Off -&gt; On display wakes from Modern Standby, re-checks
    /// the firmware against what was chosen through a serialized, coalescing <see cref="PowerEventCoordinator"/>.
    /// On wake, also makes sure the panel is still running (handles wake without sign-in). On a board whose limits
    /// differ on battery (or change at low battery), also acts on <see cref="PowerBroadcastStatus.PowerStatusChange"/>.
    /// </summary>
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus == PowerBroadcastStatus.Suspend)
        {
            WithPowerEvents(events => events.OnSuspend());
            return true;
        }

        var isWake = powerStatus is PowerBroadcastStatus.ResumeAutomatic
            or PowerBroadcastStatus.ResumeSuspend
            or PowerBroadcastStatus.ResumeCritical;

        if (isWake)
        {
            WithPowerEvents(events => events.OnWake("wake"));
            RunBackground(s => PanelStarter.EnsureRunningAfterWake(PanelStarter.ScreenSession, s));
        }
        else if (!_usingPowerSettingCallback && powerStatus == (PowerBroadcastStatus)PowerSettingChanged)
        {
            WithPowerEvents(events => events.OnLegacyScreenStateChanged());
        }

        // A switch between wall power and battery (Windows sends this for battery level changes too). Boards whose
        // limits differ on battery, and devices whose firmware changes its limits at a battery level, act on it.
        if (powerStatus == PowerBroadcastStatus.PowerStatusChange)
            WithPowerEvents(events => events.OnPowerStatusChange());

        return true;
    }

    private uint OnPowerSettingCallback(nint context, uint type, nint setting)
    {
        try
        {
            if (type == PowerSettingChanged && setting != 0)
            {
                // POWERBROADCAST_SETTING: Guid PowerSetting (0..15), uint DataLength (16..19), byte Data[1] (20..).
                var settingGuid = Marshal.PtrToStructure<Guid>(setting);
                if (settingGuid == ScreenState)
                {
                    var dataLength = (uint)Marshal.ReadInt32(setting, 16);
                    if (dataLength >= sizeof(uint))
                    {
                        var state = unchecked((uint)Marshal.ReadInt32(setting, 20));
                        WithPowerEvents(events => events.OnDisplayStateChanged(state));
                    }
                }
            }
        }
        catch (Exception error)
        {
            Log.Write($"Display power callback failed: {error.Message}");
        }

        return 0;
    }

    private void WithPowerEvents(Action<PowerEventCoordinator> action)
    {
        PowerEventCoordinator? coordinator;
        lock (_taskGate)
        {
            if (_stop is null || _deviceSession is not { } session)
                return;

            coordinator = session.PowerEvents;
        }

        action(coordinator);
    }

    private void RunBackground(Action<CancellationToken> work)
    {
        lock (_taskGate)
        {
            if (_stop is null) return;
            QueueBackgroundLocked(work);
        }
    }

    private void QueueBackgroundLocked(Action<CancellationToken> work)
    {
        var stop = _stopToken;
        Task? task = null;
        task = Task.Run(() =>
        {
            try
            {
                work(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                Log.Write($"Background task failed: {error}");
            }
            finally
            {
                lock (_taskGate)
                {
                    _backgroundTasks.Remove(task!);
                }
            }
        });
        _backgroundTasks.Add(task);
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

    /// <summary>
    /// Early in a boot WMI or I2C-HID endpoints may not answer yet; keep retrying while the board is still
    /// unnamed or matches a known board whose firmware interface has not finished initializing.
    /// </summary>
    private static async Task<(Board, IDevice?)> OpenDeviceAsync(CancellationToken stop)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (machine, device) = DeviceCatalog.Open();
            if (device is not null || attempt == BoardAttempts) return (machine, device);
            if (machine.IsNamed && !DeviceCatalog.IsKnownBoard(machine)) return (machine, device);

            await Task.Delay(BoardPause, stop);
        }
    }

    private async Task ServeOnceAsync(CancellationToken stop)
    {
        var handler = new RequestHandler();
        IDevice? ownedDevice = null;
        DeviceSession? session = null;

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
                var fan = found is null ? null : new FanCurveKeeper(found);
                if (keeper is not null && fan is not null) keeper.AfterRestore = fan.RestoreIfForgotten;

                // A keeper that fails must not keep the pipe at "still starting up".
                try
                {
                    keeper?.RestoreIfForgotten("start-up");
                    charge?.RestoreIfForgotten("start-up");
                }
                catch (Exception error)
                {
                    Log.Write($"Restoring settings at start-up failed: {error.Message}");
                }
                fan?.RestoreIfForgotten("start-up");   // never throws
                if (found is not null && keeper is not null && charge is not null && fan is not null)
                {
                    var powerEvents = new PowerEventCoordinator(
                        onSystemPowerState: found.OnSystemPowerState,
                        onRestore: occasion =>
                        {
                            keeper.RestoreIfForgotten(occasion);
                            if (stop.IsCancellationRequested) return;
                            charge.RestoreIfForgotten(occasion);
                            if (stop.IsCancellationRequested) return;
                            fan.RestoreIfForgotten(occasion);
                        },
                        onPowerStatusChange: keeper.OnPowerStatusChange,
                        stop: stop,
                        onError: error => Log.Write($"Power event handler failed: {error}"));

                    lock (_taskGate)
                    {
                        if (!stop.IsCancellationRequested)
                            _deviceSession = session = new DeviceSession(powerEvents);
                    }
                }
                handler.SetHardware(machine, found, keeper, charge, fan);
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
            try { await hardwareReady; }
            catch (OperationCanceledException) { }

            List<Task>? pendingDeviceTasks = null;
            lock (_taskGate)
            {
                if (session is not null)
                {
                    if (ReferenceEquals(_deviceSession, session))
                        _deviceSession = null;
                    var powerTask = session.PowerEvents.WaitForIdleAsync();
                    if (!powerTask.IsCompleted) pendingDeviceTasks = [powerTask];
                }
            }

            var deviceTasksFinished = true;
            if (pendingDeviceTasks is { Count: > 0 })
            {
                var waitAll = Task.WhenAll(pendingDeviceTasks);
                try
                {
                    if (!stop.IsCancellationRequested)
                        await waitAll.WaitAsync(stop);
                    else
                        await waitAll.WaitAsync(StopPatience, CancellationToken.None);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    try { await waitAll.WaitAsync(StopPatience, CancellationToken.None); }
                    catch (System.TimeoutException) { deviceTasksFinished = false; }
                    catch (Exception) { }
                }
                catch (System.TimeoutException)
                {
                    deviceTasksFinished = false;
                }
                catch (Exception) { }

                if (!deviceTasksFinished)
                {
                    Log.Write("Timed out waiting for background hardware tasks before disposing device.");
                    if (ownedDevice is { } deferredDevice)
                    {
                        _ = waitAll.ContinueWith(
                            _ =>
                            {
                                try { deferredDevice.Dispose(); }
                                catch (Exception error) { Log.Write($"Deferred device disposal failed: {error.Message}"); }
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }
            }

            if (deviceTasksFinished)
                ownedDevice?.Dispose();
        }
    }

    private void StopServing()
    {
        if (_powerSettingWatch != 0)
        {
            _ = PowerSettingUnregisterNotification(_powerSettingWatch);
            _powerSettingWatch = 0;
        }
        _usingPowerSettingCallback = false;
        _displayCallback = null;

        if (_screenWatch != 0) UnregisterPowerSettingNotification(_screenWatch);
        _screenWatch = 0;

        CancellationTokenSource? cts;
        List<Task> tasks;
        lock (_taskGate)
        {
            cts = _stop;
            _stop = null;
            tasks = [.. _backgroundTasks];
        }

        cts?.Cancel();

        if (_serving is { } serving) tasks.Add(serving);

        var allFinished = true;
        if (tasks.Count > 0)
        {
            try
            {
                allFinished = Task.WhenAll(tasks).Wait(StopPatience);
            }
            catch (AggregateException)
            {
                // Already logged where it happened.
            }
        }

        // Only dispose the CancellationTokenSource once every task that could wait on its WaitHandle has exited.
        if (allFinished) cts?.Dispose();
    }

    /// <summary>GUID_CONSOLE_DISPLAY_STATE: the screen turned on, off, or dimmed.</summary>
    private static readonly Guid ScreenState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private const int ToAService = 1;                  // DEVICE_NOTIFY_SERVICE_HANDLE
    private const uint DeviceNotifyCallback = 2;       // DEVICE_NOTIFY_CALLBACK
    private const int PowerSettingChanged = 0x8013;    // PBT_POWERSETTINGCHANGE

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallbackRoutine(nint context, uint type, nint setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public nint Callback;
        public nint Context;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingRegisterNotification(
        in Guid settingGuid,
        uint flags,
        ref DeviceNotifySubscribeParameters recipient,
        out nint registrationHandle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingUnregisterNotification(nint registrationHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid setting, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(nint handle);
}
