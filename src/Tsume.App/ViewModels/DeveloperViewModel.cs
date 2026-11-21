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
/// runs the read-only environment &amp; read-back check, and toggles embedding hardware info in Share logs.
/// </summary>
public sealed class DeveloperViewModel : LayerViewModel
{
    private readonly HelperClient _helper;
    private readonly SavedState _state;
    private bool _runningSnapshot;
    private bool _checkingEnv;

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
    public ICommand ToggleIncludeHardwareInLogs { get; }

    public bool IncludeHardwareInLogs => _state.IncludeHardwareInLogs;

    public string QuickSnapshotDetail { get; private set => Set(ref field, value); } = Strings.QuickHardwareSnapshotDetail;
    public string EnvironmentDetail { get; private set => Set(ref field, value); } = Strings.CheckEnvironmentDetail;
    public string EnvironmentSummary { get; private set => Set(ref field, value); } = "";

    public Visibility EnvironmentSummaryVisibility =>
        string.IsNullOrWhiteSpace(EnvironmentSummary) ? Visibility.Collapsed : Visibility.Visible;

    protected override void OnOpened()
    {
        if (!_runningSnapshot) QuickSnapshotDetail = Strings.QuickHardwareSnapshotDetail;
        if (!_checkingEnv && string.IsNullOrWhiteSpace(EnvironmentSummary))
            EnvironmentDetail = Strings.CheckEnvironmentDetail;
        Notify(nameof(IncludeHardwareInLogs));
    }
}
