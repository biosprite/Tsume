using System.IO;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.App.Shell;
using Tsume.Core.Ipc;

namespace Tsume.App.ViewModels;

/// <summary>
/// Settings → Troubleshooting → Developer options:
/// launches the standalone Hardware Discovery window, runs 1-click hands-free hardware snapshots,
/// runs the read-only environment &amp; read-back check, runs firmware verification tools
/// (probe, write check, mode check) logged to <c>firmware-checks.log</c>, and toggles embedding hardware info in Share logs.
/// </summary>
public sealed class DeveloperViewModel : LayerViewModel
{
    private readonly HelperClient _helper;
    private readonly SavedState _state;
    private bool _runningSnapshot;
    private bool _checkingEnv;
    private bool _runningFirmwareCheck;
    private bool _sharingLogs;

    public DeveloperViewModel(HelperClient helper, SavedState state, Func<Task> dismiss)
    {
        _helper = helper;
        _state = state;

        OpenHardwareDiscovery = new Command(async () =>
        {
            await dismiss();
            DeviceDiscoveryWindow.ShowOrActivate(_helper);
        });

        RunQuickSnapshot = new Command(async () =>
        {
            if (_runningSnapshot) return;
            _runningSnapshot = true;
            QuickSnapshotDetail = Strings.QuickHardwareSnapshotBusy;

            try
            {
                var zip = await DeviceSupportExporter.ExportQuickSnapshotAsync(_helper);
                LogBundle.CopyFileToClipboard(zip);
                LogBundle.RevealInExplorer(zip);
                QuickSnapshotDetail = Strings.QuickHardwareSnapshotDone;
                await dismiss();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                QuickSnapshotDetail = Strings.QuickHardwareSnapshotFailed(error.Message);
            }
            catch (Exception error)
            {
                QuickSnapshotDetail = Strings.QuickHardwareSnapshotFailed(error.Message);
            }
            finally
            {
                _runningSnapshot = false;
            }
        });

        RunEnvironmentCheck = new Command(async () =>
        {
            if (_checkingEnv) return;
            _checkingEnv = true;
            EnvironmentDetail = Strings.CheckEnvironmentBusy;

            try
            {
                var check = await DeviceSupportExporter.RunEnvironmentCheckAsync(_helper);
                EnvironmentSummary = string.Join(Environment.NewLine,
                    check.Findings.Select(f => $"[{f.Level.ToUpperInvariant()}] {f.Area}: {f.Message}"));
                EnvironmentDetail = $"{check.DeviceName} · {check.Findings.Count} checks complete";
                Notify(nameof(EnvironmentSummaryVisibility));
            }
            catch (Exception error)
            {
                EnvironmentDetail = error.Message;
            }
            finally
            {
                _checkingEnv = false;
            }
        });

        RunFirmwareProbe = new Command(() => _ = RunFirmwareCheckAsync(
            FirmwareCheckKind.Probe,
            Strings.RunFirmwareProbeBusy,
            detail => FirmwareProbeDetail = detail));

        RunWriteCheck = new Command(() => _ = RunFirmwareCheckAsync(
            FirmwareCheckKind.WriteCheck,
            Strings.RunWriteCheckBusy,
            detail => WriteCheckDetail = detail));

        RunModeCheck = new Command(() => _ = RunFirmwareCheckAsync(
            FirmwareCheckKind.ModeCheck,
            Strings.RunModeCheckBusy,
            detail => ModeCheckDetail = detail));

        ShareLogs = new Command(async () =>
        {
            if (_sharingLogs) return;
            _sharingLogs = true;
            ShareLogsDetail = Strings.ShareLogsBusy;

            try
            {
                var zip = await LogBundle.ExportAsync(_helper);
                LogBundle.CopyFileToClipboard(zip);
                LogBundle.RevealInExplorer(zip);
                await dismiss();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ShareLogsDetail = Strings.ShareLogsFailed(error.Message);
            }
            finally
            {
                _sharingLogs = false;
            }
        });

        ToggleIncludeHardwareInLogs = new Command(() =>
        {
            _state.IncludeHardwareInLogs = !_state.IncludeHardwareInLogs;
            _state.Save();
            Notify(nameof(IncludeHardwareInLogs));
        });
    }

    public ICommand OpenHardwareDiscovery { get; }
    public ICommand RunQuickSnapshot { get; }
    public ICommand RunEnvironmentCheck { get; }
    public ICommand RunFirmwareProbe { get; }
    public ICommand RunWriteCheck { get; }
    public ICommand RunModeCheck { get; }
    public ICommand ShareLogs { get; }
    public ICommand ToggleIncludeHardwareInLogs { get; }

    public bool IncludeHardwareInLogs => _state.IncludeHardwareInLogs;

    public string QuickSnapshotDetail { get; private set => Set(ref field, value); } = Strings.QuickHardwareSnapshotDetail;
    public string EnvironmentDetail { get; private set => Set(ref field, value); } = Strings.CheckEnvironmentDetail;
    public string EnvironmentSummary { get; private set => Set(ref field, value); } = "";

    public Visibility EnvironmentSummaryVisibility =>
        string.IsNullOrWhiteSpace(EnvironmentSummary) ? Visibility.Collapsed : Visibility.Visible;

    public string FirmwareProbeDetail { get; private set => Set(ref field, value); } = Strings.RunFirmwareProbeDetail;
    public string WriteCheckDetail { get; private set => Set(ref field, value); } = Strings.RunWriteCheckDetail;
    public string ModeCheckDetail { get; private set => Set(ref field, value); } = Strings.RunModeCheckDetail;
    public string ShareLogsDetail { get; private set => Set(ref field, value); } = Strings.DeveloperShareLogsDetail;
    public string FirmwareCheckOutput { get; private set => Set(ref field, value); } = "";

    public Visibility FirmwareCheckOutputVisibility =>
        string.IsNullOrWhiteSpace(FirmwareCheckOutput) ? Visibility.Collapsed : Visibility.Visible;

    private async Task RunFirmwareCheckAsync(FirmwareCheckKind kind, string busyDetail, Action<string> setDetail)
    {
        if (_runningFirmwareCheck) return;
        _runningFirmwareCheck = true;
        setDetail(busyDetail);

        try
        {
            var (ok, report, msiRunning) = await DeviceSupportExporter.RunFirmwareCheckAsync(_helper, kind);
            FirmwareCheckOutput = report;
            Notify(nameof(FirmwareCheckOutputVisibility));

            if (!ok)
                setDetail(Strings.FirmwareCheckFailed(report));
            else if (msiRunning && kind is FirmwareCheckKind.WriteCheck or FirmwareCheckKind.ModeCheck)
                setDetail(Strings.FirmwareCheckLoggedMsiWarning);
            else
                setDetail(Strings.FirmwareCheckLogged);
        }
        catch (Exception error)
        {
            FirmwareCheckOutput = error.Message;
            Notify(nameof(FirmwareCheckOutputVisibility));
            setDetail(Strings.FirmwareCheckFailed(error.Message));
        }
        finally
        {
            _runningFirmwareCheck = false;
        }
    }

    protected override void OnOpened()
    {
        if (!_runningSnapshot) QuickSnapshotDetail = Strings.QuickHardwareSnapshotDetail;
        if (!_checkingEnv && string.IsNullOrWhiteSpace(EnvironmentSummary))
            EnvironmentDetail = Strings.CheckEnvironmentDetail;
        if (!_sharingLogs) ShareLogsDetail = Strings.DeveloperShareLogsDetail;
        Notify(nameof(IncludeHardwareInLogs));
    }
}
