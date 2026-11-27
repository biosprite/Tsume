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
    private readonly AsyncCommand _runQuickSnapshot;
    private readonly AsyncCommand _runEnvironmentCheck;
    private readonly AsyncCommand _shareLogs;
    private bool _runningFirmwareCheck;

    public DeveloperViewModel(HelperClient helper, SavedState state, Func<Task> dismiss)
    {
        _helper = helper;
        _state = state;

        OpenHardwareDiscovery = new AsyncCommand(async () =>
        {
            await dismiss();
            DeviceDiscoveryWindow.ShowOrActivate(_helper);
        });

        _runQuickSnapshot = new AsyncCommand(async () =>
        {
            QuickSnapshotDetail = Strings.QuickHardwareSnapshotBusy;

            try
            {
                var zip = await DeviceSupportExporter.ExportQuickSnapshotAsync(_helper);
                LogBundle.CopyFileToClipboard(zip);
                LogBundle.RevealInExplorer(zip);
                QuickSnapshotDetail = Strings.QuickHardwareSnapshotDone;
                await dismiss();
            }
            catch (Exception error)
            {
                QuickSnapshotDetail = Strings.QuickHardwareSnapshotFailed(error.Message);
            }
        });

        _runEnvironmentCheck = new AsyncCommand(async () =>
        {
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
        });

        RunFirmwareProbe = new AsyncCommand(() => RunFirmwareCheckAsync(
            FirmwareCheckKind.Probe,
            Strings.RunFirmwareProbeBusy,
            detail => FirmwareProbeDetail = detail));

        RunWriteCheck = new AsyncCommand(() => RunFirmwareCheckAsync(
            FirmwareCheckKind.WriteCheck,
            Strings.RunWriteCheckBusy,
            detail => WriteCheckDetail = detail));

        RunModeCheck = new AsyncCommand(() => RunFirmwareCheckAsync(
            FirmwareCheckKind.ModeCheck,
            Strings.RunModeCheckBusy,
            detail => ModeCheckDetail = detail));

        _shareLogs = SettingsViewModel.CreateShareLogsCommand(_helper, dismiss, detail => ShareLogsDetail = detail);

        ToggleIncludeHardwareInLogs = new Command(() =>
        {
            _state.IncludeHardwareInLogs = !_state.IncludeHardwareInLogs;
            _state.Save();
            Notify(nameof(IncludeHardwareInLogs));
        });
    }

    public ICommand OpenHardwareDiscovery { get; }
    public ICommand RunQuickSnapshot => _runQuickSnapshot;
    public ICommand RunEnvironmentCheck => _runEnvironmentCheck;
    public ICommand RunFirmwareProbe { get; }
    public ICommand RunWriteCheck { get; }
    public ICommand RunModeCheck { get; }
    public ICommand ShareLogs => _shareLogs;
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
        if (!_runQuickSnapshot.IsRunning) QuickSnapshotDetail = Strings.QuickHardwareSnapshotDetail;
        if (!_runEnvironmentCheck.IsRunning && string.IsNullOrWhiteSpace(EnvironmentSummary))
            EnvironmentDetail = Strings.CheckEnvironmentDetail;
        if (!_shareLogs.IsRunning) ShareLogsDetail = Strings.DeveloperShareLogsDetail;
        Notify(nameof(IncludeHardwareInLogs));
    }
}
