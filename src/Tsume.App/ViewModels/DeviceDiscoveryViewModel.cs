using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.App.Theme;
using Tsume.Core.Diagnostics;
using Tsume.Core.Ipc;

namespace Tsume.App.ViewModels;

public sealed record GuidedActionStep(int Number, string Headline, string? Detail = null)
{
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

public sealed record GuidedTargetChip(string Label, string Value, Brush? SwatchBrush = null)
{
    public bool HasSwatch => SwatchBrush is not null;
}

public sealed class GuidedStepItem : Observable
{
    public required string Id { get; init; }
    public required int Number { get; init; }
    public required string Category { get; init; }
    public required string Glyph { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required IReadOnlyList<GuidedActionStep> ActionSteps { get; init; }
    public IReadOnlyList<GuidedTargetChip> Targets { get; init; } = [];
    public string? AfterCaptureNote { get; init; }
    public string? SkipHint { get; init; }
    public required string WhyItMatters { get; init; }
    public string NoteHint { get; init; } = "Optional note:";
    public bool IsButtonCaptureStep { get; init; }
    public bool IsBaselineStep { get; init; }
    public bool IsOptional { get; init; }

    public bool HasTargets => Targets.Count > 0;
    public bool HasAfterCaptureNote => !string.IsNullOrWhiteSpace(AfterCaptureNote);
    public bool HasSkipHint => !string.IsNullOrWhiteSpace(SkipHint);

    public string Status
    {
        get;
        set
        {
            if (Set(ref field, value)) Notify(nameof(SidebarStatus));
        }
    } = "Pending";

    public string Detail { get; set => Set(ref field, value); } = "";

    public bool IsCaptured
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Notify(nameof(SidebarStatus));
                Notify(nameof(ShowStartBadge));
                Notify(nameof(ShowOptionalBadge));
            }
        }
    }

    public bool IsSkipped
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Notify(nameof(SidebarStatus));
                Notify(nameof(ShowStartBadge));
                Notify(nameof(ShowOptionalBadge));
            }
        }
    }

    public bool IsSelected { get; set => Set(ref field, value); }

    public string SidebarStatus =>
        IsCaptured || IsSkipped ? Status :
        IsBaselineStep ? "Start here" :
        IsOptional ? "Pending · Optional" :
        Status;

    public bool ShowStartBadge => IsBaselineStep && !IsCaptured && !IsSkipped;
    public bool ShowOptionalBadge => IsOptional && !IsCaptured && !IsSkipped;
}

/// <summary>
/// Full state machine for the standalone Hardware Discovery window:
/// Quick Scan (hands-free), Guided Capture (11 steps + button timeline), Live Watch Mode,
/// Report Preview, and privacy-scrubbed Support Bundle export.
/// </summary>
public sealed class DeviceDiscoveryViewModel : Observable, IDisposable
{
    private const int MaxButtonEvents = 5000;

    private readonly HelperClient _helper;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Func<nint> _getWindowHandle = () => 0;

    private ElevatedInventory? _inventory;
    private HidTopologySnapshot? _hid;
    private DisplayInventorySnapshot? _display;
    private HardwareEnvironmentCheck? _envCheck;
    private CombinedStateSnapshot? _baseline;
    private CombinedStateSnapshot? _watchPrevious;

    private readonly HashSet<string> _volatileFirmwareKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _volatileEcOffsets = [];
    private readonly Dictionary<string, CaptureDiffEntry> _guidedDiffs = new(StringComparer.Ordinal);
    private readonly List<CaptureDiffEntry> _watchDiffs = [];
    private readonly List<CapturedButtonEvent> _buttonEvents = [];

    private ButtonCaptureSession? _buttonSession;
    private CancellationTokenSource? _watchCts;
    private int _watchChangeIndex;
    private bool _disposed;

    public DeviceDiscoveryViewModel(HelperClient helper, Dispatcher dispatcher)
    {
        _helper = helper;
        _dispatcher = dispatcher;

        Steps = new ObservableCollection<GuidedStepItem>(CreateGuidedSteps());
        _selectedStep = Steps[0];
        _selectedStep.IsSelected = true;

        ButtonPrompts =
        [
            new("oem_short", "OEM button", "SHORT PRESS"),
            new("oem_hold", "OEM button", "PRESS & HOLD"),
            new("quick_settings_short", "Quick Settings / Game Bar", "SHORT PRESS"),
            new("quick_settings_hold", "Quick Settings / Game Bar", "PRESS & HOLD"),
            new("back_left", "Back Left paddle", "M1 / L4"),
            new("back_right", "Back Right paddle", "M2 / R4"),
            new("volume_buttons", "Volume buttons", "VOL + / −"),
            new("other_button", "Other button", "CUSTOM"),
        ];
        SelectedButtonPrompt = ButtonPrompts[0];

        RunQuickScanCommand = new Command(async () => await RunQuickScanAsync());
        CaptureStepCommand = new Command(async () => await CaptureCurrentStepAsync());
        SkipStepCommand = new Command(SkipCurrentStep);
        PreviousStepCommand = new Command(() => SelectStepIndex(SelectedStepIndex - 1));
        NextStepCommand = new Command(() => SelectStepIndex(SelectedStepIndex + 1));
        ToggleButtonListeningCommand = new Command(ToggleButtonListening);
        ToggleWatchModeCommand = new Command(async () => await ToggleWatchModeAsync());
        RefreshPreviewCommand = new Command(RefreshPreview);
        ExportBundleCommand = new Command(async () => await ExportBundleAsync());
        RunEnvCheckCommand = new Command(async () => await RunEnvCheckOnlyAsync());

        UpdateSummaryPill();
    }

    public void AttachWindowHandle(Func<nint> handleProvider) => _getWindowHandle = handleProvider;

    public ObservableCollection<GuidedStepItem> Steps { get; }
    public ObservableCollection<string> LiveFeed { get; } = [];
    public ObservableCollection<string> ButtonFeed { get; } = [];

    public IReadOnlyList<ButtonPromptOption> ButtonPrompts { get; }
    public ButtonPromptOption SelectedButtonPrompt
    {
        get;
        set
        {
            if (Set(ref field, value) && _buttonSession is not null)
                _buttonSession.ActivePromptId = value.Id;
        }
    }

    public int ActiveTabIndex
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                if (value == 3) RefreshPreview();
                Notify(nameof(IsQuickScanTab));
                Notify(nameof(IsGuidedTab));
                Notify(nameof(IsWatchTab));
                Notify(nameof(IsPreviewTab));
            }
        }
    }

    public bool IsQuickScanTab => ActiveTabIndex == 0;
    public bool IsGuidedTab => ActiveTabIndex == 1;
    public bool IsWatchTab => ActiveTabIndex == 2;
    public bool IsPreviewTab => ActiveTabIndex == 3;

    public bool IncludeTimedLoad { get; set => Set(ref field, value); } = true;
    public bool AllowUacFallback { get; set => Set(ref field, value); } = true;

    public bool IsBusy
    {
        get;
        private set
        {
            if (Set(ref field, value)) Notify(nameof(IsNotBusy));
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string StatusBanner { get; private set => Set(ref field, value); } =
        "100% read-only. Nothing is written to firmware, registers, or controller flash.";

    public string SummaryPill { get; private set => Set(ref field, value); } = "No data collected yet";
    public string ExportMessage { get; private set => Set(ref field, value); } = "";

    // Quick scan summary fields
    public bool HasQuickScan { get; private set => Set(ref field, value); }
    public string QuickIdentityText { get; private set => Set(ref field, value); } = "Not scanned yet.";
    public string QuickFirmwareText { get; private set => Set(ref field, value); } = "Not scanned yet.";
    public string QuickPowerDisplayText { get; private set => Set(ref field, value); } = "Not scanned yet.";
    public string QuickHidText { get; private set => Set(ref field, value); } = "Not scanned yet.";
    public string QuickOemText { get; private set => Set(ref field, value); } = "Not scanned yet.";

    // Guided capture state
    private GuidedStepItem _selectedStep;
    public GuidedStepItem SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (value is null || ReferenceEquals(_selectedStep, value)) return;
            _selectedStep.IsSelected = false;
            if (Set(ref _selectedStep, value))
            {
                _selectedStep.IsSelected = true;
                Notify(nameof(SelectedStepIndex));
                Notify(nameof(CanGoPrevious));
                Notify(nameof(CanGoNext));
                Notify(nameof(SelectedStepDiffPreview));
                Notify(nameof(IsButtonStepSelected));
            }
        }
    }

    public int SelectedStepIndex => Steps.IndexOf(SelectedStep);
    public bool CanGoPrevious => SelectedStepIndex > 0;
    public bool CanGoNext => SelectedStepIndex < Steps.Count - 1;
    public bool IsButtonStepSelected => SelectedStep.IsButtonCaptureStep;

    public int GuidedCapturedCount => Steps.Count(s => s.IsCaptured);
    public bool HasGuidedProgress => GuidedCapturedCount > 0;
    public string GuidedProgressText
    {
        get
        {
            var captured = GuidedCapturedCount;
            var skipped = Steps.Count(s => s.IsSkipped);
            return skipped > 0
                ? $"{captured} / {Steps.Count} captured · {skipped} skipped"
                : $"{captured} / {Steps.Count} captured";
        }
    }

    public string StepUserNote { get; set => Set(ref field, value); } = "";

    public string SelectedStepDiffPreview
    {
        get
        {
            if (SelectedStep.IsButtonCaptureStep)
                return _buttonEvents.Count == 0
                    ? "Start listening and press each dedicated hardware button on the handheld."
                    : $"{_buttonEvents.Count} button event(s) captured across WMI / Keyboard / HID / XInput.";

            if (!_guidedDiffs.TryGetValue(SelectedStep.Id, out var diff))
                return SelectedStep.IsBaselineStep
                    ? "Not captured yet. Set your handheld to the baseline defaults above, then click Capture Step."
                    : "Not captured yet. Follow the steps above, then click Capture Step.";

            return FormatDiffSummary(diff);
        }
    }

    // Button listening & Live Watch state
    public bool IsButtonListening
    {
        get;
        private set
        {
            if (Set(ref field, value)) Notify(nameof(ButtonListenLabel));
        }
    }

    public string ButtonListenLabel => IsButtonListening ? "Stop Listening" : "Start Listening";

    public bool IsWatching
    {
        get;
        private set
        {
            if (Set(ref field, value)) Notify(nameof(WatchButtonLabel));
        }
    }

    public string WatchButtonLabel => IsWatching ? "Stop Watching" : "Start Watching";
    public string WatchStatusText { get; private set => Set(ref field, value); } =
        "Click Start Watching, then freely toggle power modes, fans, charge limit, RGB or press hardware buttons.";

    public string ReportPreviewText { get; private set => Set(ref field, value); } =
        "Run Quick Scan, Guided Capture, or Live Watch to generate the preview report.";

    public ICommand RunQuickScanCommand { get; }
    public ICommand CaptureStepCommand { get; }
    public ICommand SkipStepCommand { get; }
    public ICommand PreviousStepCommand { get; }
    public ICommand NextStepCommand { get; }
    public ICommand ToggleButtonListeningCommand { get; }
    public ICommand ToggleWatchModeCommand { get; }
    public ICommand RefreshPreviewCommand { get; }
    public ICommand ExportBundleCommand { get; }
    public ICommand RunEnvCheckCommand { get; }

    public void SelectStepIndex(int index)
    {
        if (index < 0 || index >= Steps.Count) return;
        StepUserNote = "";
        SelectedStep = Steps[index];
    }

    public async Task RunQuickScanAsync()
    {
        if (IsBusy || _disposed) return;
        if (IsWatching) StopWatchMode();
        IsBusy = true;
        ExportMessage = "";
        StatusBanner = IncludeTimedLoad
            ? "Scanning hardware, WMI/MOF, HID bus, OEM footprint and 3-second load delta (read-only)…"
            : "Scanning hardware, WMI/MOF, HID bus and OEM footprint (read-only)…";

        var cancel = _lifetimeCts.Token;
        try
        {
            var invTask = DeviceSupportExporter.CollectInventoryAsync(_helper, IncludeTimedLoad, AllowUacFallback, cancel);
            var hidTask = Task.Run(() => HidInventory.CaptureAsync(readVendorProfiles: true), cancel);
            var display = DisplayInventory.Capture();

            await Task.WhenAll(invTask, hidTask);
            if (_disposed) return;

            _inventory = await invTask;
            _hid = await hidTask;
            _display = display;
            _envCheck = await DeviceSupportExporter.RunEnvironmentCheckAsync(_helper, _inventory, _hid, cancel);
            if (_disposed) return;

            AbsorbVolatileMask(_inventory);

            // Also seed baseline automatically if not yet captured.
            if (_baseline is null)
            {
                _baseline = new CombinedStateSnapshot
                {
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    Elevated = await DeviceSupportExporter.CollectFastStateAsync(_helper, cancel),
                    Hid = _hid,
                    CurrentRefreshHz = _display.CurrentRefreshHz,
                };
                if (_disposed) return;
            }

            PopulateQuickSummary();
            HasQuickScan = true;
            StatusBanner = _inventory.Elevated
                ? "Quick scan complete (elevated firmware + full HID/WMI/OEM capture)."
                : "Quick scan complete (unelevated fallback — start TsumeHelper service for full EC/WMI reads).";
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception error)
        {
            if (!_disposed) StatusBanner = $"Quick scan failed: {error.Message}";
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                UpdateSummaryPill();
                RefreshPreview();
            }
        }
    }

    private async Task RunEnvCheckOnlyAsync()
    {
        if (IsBusy || _disposed) return;
        if (IsWatching) StopWatchMode();
        IsBusy = true;
        StatusBanner = "Checking helper, CPU boost mode, OEM software, and live read-back…";
        try
        {
            _envCheck = await DeviceSupportExporter.RunEnvironmentCheckAsync(_helper, cancel: _lifetimeCts.Token);
            if (_disposed) return;
            StatusBanner = $"Environment check complete ({_envCheck.Findings.Count} checks). See Report Preview for details.";
            RefreshPreview();
            ActiveTabIndex = 3;
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception error)
        {
            if (!_disposed) StatusBanner = $"Environment check failed: {error.Message}";
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                UpdateSummaryPill();
            }
        }
    }

    private async Task CaptureCurrentStepAsync()
    {
        if (IsBusy || _disposed || SelectedStep.IsButtonCaptureStep) return;
        if (IsWatching) StopWatchMode();
        IsBusy = true;

        var step = SelectedStep;
        var note = string.IsNullOrWhiteSpace(StepUserNote) ? null : StepUserNote.Trim();
        StatusBanner = $"Capturing {step.Title} (read-only)…";

        var cancel = _lifetimeCts.Token;
        try
        {
            // Ensure full inventory ran at least once so volatile bytes & WMI classes are known.
            if (_inventory is null)
            {
                _inventory = await DeviceSupportExporter.CollectInventoryAsync(_helper, includeTimedLoad: true, allowUacFallback: AllowUacFallback, cancel);
                if (_disposed) return;
                AbsorbVolatileMask(_inventory);
                _display ??= DisplayInventory.Capture();
                _hid ??= await Task.Run(() => HidInventory.CaptureAsync(readVendorProfiles: true), cancel);
                if (_disposed) return;
                PopulateQuickSummary();
                HasQuickScan = true;
            }

            var current = await DeviceSupportExporter.CaptureCombinedStateAsync(_helper, readVendorProfiles: true, cancel);
            if (_disposed) return;
            _hid = current.Hid ?? _hid;

            if (step.IsBaselineStep || _baseline is null)
            {
                _baseline = current;
                var entry = new CaptureDiffEntry
                {
                    StepId = step.Id,
                    Title = step.Title,
                    Kind = "baseline",
                    UserNote = note,
                    CapturedAtUtc = current.CapturedAtUtc,
                };
                _guidedDiffs[step.Id] = entry;
                step.IsCaptured = true;
                step.IsSkipped = false;
                step.Status = "Captured";
                step.Detail = current.Elevated is null ? "HID/Display baseline (helper offline)" : "Baseline recorded";
                StatusBanner = "Baseline captured. Proceed to the next step.";
            }
            else
            {
                var diff = DiscoveryDiffEngine.Compute(
                    step.Id,
                    step.Title,
                    kind: "guided",
                    _baseline,
                    current,
                    _volatileFirmwareKeys,
                    _volatileEcOffsets,
                    note);

                _guidedDiffs[step.Id] = diff;
                step.IsCaptured = true;
                step.IsSkipped = false;
                var count = diff.NonVolatileChangeCount;
                step.Status = count > 0 ? $"Captured (+{count})" : "Captured (0 diffs)";
                step.Detail = count > 0 ? $"{count} non-volatile change(s)" : "No non-volatile changes";
                StatusBanner = $"{step.Title}: {count} non-volatile difference(s) recorded.";
            }

            Notify(nameof(SelectedStepDiffPreview));
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception error)
        {
            if (!_disposed) StatusBanner = $"Capture failed: {error.Message}";
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                UpdateSummaryPill();
            }
        }
    }

    private void SkipCurrentStep()
    {
        var step = SelectedStep;
        if (step.IsButtonCaptureStep)
        {
            StopButtonListening();
        }
        else
        {
            _guidedDiffs[step.Id] = new CaptureDiffEntry
            {
                StepId = step.Id,
                Title = step.Title,
                Kind = "skipped",
                UserNote = string.IsNullOrWhiteSpace(StepUserNote) ? null : StepUserNote.Trim(),
            };
        }

        step.IsSkipped = true;
        step.IsCaptured = false;
        step.Status = "Skipped";
        step.Detail = "Skipped";
        Notify(nameof(SelectedStepDiffPreview));
        UpdateSummaryPill();

        if (CanGoNext) SelectStepIndex(SelectedStepIndex + 1);
    }

    private void ToggleButtonListening()
    {
        if (_disposed) return;
        if (IsWatching) StopWatchMode();
        if (IsButtonListening) StopButtonListening();
        else StartButtonListening(SelectedButtonPrompt.Id);
    }

    private void StartButtonListening(string promptId)
    {
        StopButtonListening();

        var wmiEvents = _inventory?.WmiClasses.Where(c => c.IsEvent).Select(c => (c.Namespace, c.Name));
        var vendorPages = _hid?.Collections.Where(c => c.UsagePage >= 0xFF00).Select(c => c.UsagePage);

        _buttonSession = new ButtonCaptureSession(_dispatcher, _getWindowHandle(), wmiEvents, vendorPages)
        {
            ActivePromptId = promptId,
        };
        _buttonSession.EventCaptured += OnButtonEventCaptured;
        IsButtonListening = true;
        StatusBanner = $"Listening for button presses ({promptId}) across WMI, Keyboard, Raw HID & XInput…";
    }

    private void StopButtonListening()
    {
        if (_buttonSession is null) return;
        _buttonSession.EventCaptured -= OnButtonEventCaptured;
        _buttonSession.Dispose();
        _buttonSession = null;
        IsButtonListening = false;
    }

    private void OnButtonEventCaptured(CapturedButtonEvent ev)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;

            if (_buttonEvents.Count >= MaxButtonEvents)
                _buttonEvents.RemoveAt(0);
            _buttonEvents.Add(ev);
            var line = $"[+{ev.ElapsedMs,6} ms] [{ev.PromptId}] [{ev.Source}] {ev.Summary}";

            ButtonFeed.Insert(0, line);
            if (ButtonFeed.Count > 200) ButtonFeed.RemoveAt(ButtonFeed.Count - 1);

            if (IsWatching)
            {
                LiveFeed.Insert(0, line);
                if (LiveFeed.Count > 300) LiveFeed.RemoveAt(LiveFeed.Count - 1);
            }

            var buttonStep = Steps.FirstOrDefault(s => s.IsButtonCaptureStep);
            if (buttonStep is not null)
            {
                buttonStep.IsCaptured = true;
                buttonStep.IsSkipped = false;
                buttonStep.Status = _buttonEvents.Count == 1
                    ? "Captured (1 event)"
                    : $"Captured ({_buttonEvents.Count} events)";
            }

            Notify(nameof(SelectedStepDiffPreview));
            UpdateSummaryPill();
        });
    }

    private async Task ToggleWatchModeAsync()
    {
        if (_disposed) return;
        if (IsWatching)
        {
            StopWatchMode();
            return;
        }

        if (IsBusy) return;
        IsBusy = true;
        WatchStatusText = "Preparing watch baseline…";

        var cancel = _lifetimeCts.Token;
        try
        {
            if (_inventory is null)
            {
                _inventory = await DeviceSupportExporter.CollectInventoryAsync(_helper, includeTimedLoad: true, allowUacFallback: AllowUacFallback, cancel);
                if (_disposed) return;
                AbsorbVolatileMask(_inventory);
                _display ??= DisplayInventory.Capture();
                _hid ??= await Task.Run(() => HidInventory.CaptureAsync(readVendorProfiles: true), cancel);
                if (_disposed) return;
                PopulateQuickSummary();
                HasQuickScan = true;
            }

            _watchPrevious = await DeviceSupportExporter.CaptureCombinedStateAsync(_helper, readVendorProfiles: true, cancel);
            if (_disposed) return;
            _baseline ??= _watchPrevious;

            StartButtonListening("live_watch");

            _watchCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            IsWatching = true;
            WatchStatusText = "Watching live (polling read-only state every 600 ms + capturing all buttons)…";
            StatusBanner = "Live Watch Mode active. Toggle modes/fans/charge/RGB in your OEM app or press hardware buttons.";
            LiveFeed.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Watch Mode started (read-only).");

            IsBusy = false;
            _ = WatchLoopAsync(_watchCts.Token);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                IsBusy = false;
                WatchStatusText = $"Could not start Watch Mode: {error.Message}";
            }
        }
    }

    private async Task WatchLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(600, cancel);
                var current = await DeviceSupportExporter.CaptureCombinedStateAsync(_helper, readVendorProfiles: true, cancel);
                if (cancel.IsCancellationRequested || _disposed) break;

                if (_watchPrevious is not null)
                {
                    var diff = DiscoveryDiffEngine.Compute(
                        $"watch-{_watchChangeIndex + 1:D3}",
                        $"Watch change #{_watchChangeIndex + 1} ({DateTime.Now:HH:mm:ss})",
                        kind: "watch",
                        _watchPrevious,
                        current,
                        _volatileFirmwareKeys,
                        _volatileEcOffsets);

                    if (diff.NonVolatileChangeCount > 0)
                    {
                        _watchChangeIndex++;
                        _watchDiffs.Add(diff);
                        _watchPrevious = current;

                        var summary = FormatSingleLineDiff(diff);
                        await _dispatcher.InvokeAsync(() =>
                        {
                            if (_disposed) return;
                            LiveFeed.Insert(0, $"[{DateTime.Now:HH:mm:ss}] STATE DIFF: {summary}");
                            if (LiveFeed.Count > 300) LiveFeed.RemoveAt(LiveFeed.Count - 1);
                            UpdateSummaryPill();
                        }, DispatcherPriority.Normal, cancel);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Keep polling across transient read hiccups.
            }
        }
    }

    private void StopWatchMode()
    {
        var wasWatching = IsWatching || _watchCts is not null;
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        _watchCts = null;
        if (!wasWatching) return;

        StopButtonListening();
        IsWatching = false;
        if (_disposed) return;

        WatchStatusText = $"Stopped. Recorded {_watchDiffs.Count} state diff(s) and {_buttonEvents.Count} button event(s).";
        StatusBanner = "Live Watch Mode stopped. Ready to export support bundle.";
        LiveFeed.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Watch Mode stopped.");
        UpdateSummaryPill();
    }

    private void RefreshPreview()
    {
        var bundle = BuildCurrentBundle();
        ReportPreviewText = DiscoveryReportBuilder.BuildMarkdown(bundle);
    }

    private async Task ExportBundleAsync()
    {
        if (IsBusy || _disposed) return;
        if (IsWatching) StopWatchMode();
        if (IsButtonListening) StopButtonListening();

        IsBusy = true;
        ExportMessage = "Packaging privacy-scrubbed support bundle…";

        var cancel = _lifetimeCts.Token;
        try
        {
            if (_inventory is null)
            {
                _inventory = await DeviceSupportExporter.CollectInventoryAsync(_helper, includeTimedLoad: true, allowUacFallback: AllowUacFallback, cancel);
                if (_disposed) return;
                AbsorbVolatileMask(_inventory);
                PopulateQuickSummary();
                HasQuickScan = true;
            }

            _hid ??= await Task.Run(() => HidInventory.CaptureAsync(readVendorProfiles: true), cancel);
            if (_disposed) return;
            _display ??= DisplayInventory.Capture();
            _envCheck ??= await DeviceSupportExporter.RunEnvironmentCheckAsync(_helper, _inventory, _hid, cancel);
            if (_disposed) return;

            var bundle = BuildCurrentBundle();
            var zipPath = await DeviceSupportExporter.ExportBundleAsync(_helper, bundle, cancel);
            if (_disposed) return;

            LogBundle.CopyFileToClipboard(zipPath);
            LogBundle.RevealInExplorer(zipPath);

            ExportMessage = $"Saved to {Path.GetFileName(zipPath)} (copied to clipboard & opened in Explorer).";
            StatusBanner = $"Exported: {zipPath}";
            RefreshPreview();
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                ExportMessage = $"Export failed: {error.Message}";
                StatusBanner = ExportMessage;
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                UpdateSummaryPill();
            }
        }
    }

    private DeviceSupportBundle BuildCurrentBundle()
    {
        var allDiffs = new List<CaptureDiffEntry>();
        foreach (var step in Steps)
        {
            if (_guidedDiffs.TryGetValue(step.Id, out var d))
                allDiffs.Add(d);
        }
        allDiffs.AddRange(_watchDiffs);

        return new DeviceSupportBundle
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            TsumeVersion = DeviceSupportExporter.TsumeVersionLabel(),
            Inventory = _inventory,
            Hid = _hid,
            Display = _display,
            EnvironmentCheck = _envCheck,
            Diffs = allDiffs,
            ButtonEvents = [.. _buttonEvents],
        };
    }

    private void AbsorbVolatileMask(ElevatedInventory inv)
    {
        if (inv.TimedDelta is not { } td) return;
        foreach (var v in td.VolatileValues) _volatileFirmwareKeys.Add(v.Key);
        foreach (var b in td.VolatileEcBytes) _volatileEcOffsets.Add(b.Offset);
    }

    private void PopulateQuickSummary()
    {
        if (_inventory is { } inv)
        {
            var id = inv.Identity;
            QuickIdentityText =
                $"{id.BaseBoardManufacturer} {id.BaseBoardProduct}  ·  Model: {id.SystemModel}\n" +
                $"BIOS: {id.BiosVersion}  ·  CPU: {id.CpuName} ({id.CpuCores}C/{id.CpuLogicalProcessors}T)\n" +
                $"GPU: {string.Join(", ", id.Gpus.Select(g => g.Name))}";

            var fwLine = inv.Firmware is { } fw
                ? $"Matched prober: {fw.VendorFamily} ({fw.Values.Count} values, EC dump={(fw.EcMemoryHex is not null ? "256 bytes" : "none")})"
                : "No built-in OEM prober matched (generic WMI MOF + ACPI capture active)";
            var volCount = (inv.TimedDelta?.VolatileEcBytes.Count ?? 0) + (inv.TimedDelta?.VolatileValues.Count ?? 0);
            QuickFirmwareText =
                $"{fwLine}\n" +
                $"Vendor WMI classes: {inv.WmiClasses.Count} ({inv.WmiClasses.Count(c => c.IsEvent)} event classes)  ·  " +
                $"ACPI/PCI power devices: {inv.AcpiPowerDevices.Count}  ·  Volatile telemetry keys: {volCount}";

            var pp = inv.PowerPlan;
            var boostWarn = pp.CpuBoostModeAc == 0 || pp.CpuBoostModeDc == 0 ? "  [!] BOOST DISABLED" : "";
            var dispLine = _display is { } d
                ? $"Display: {d.ActiveWidth}×{d.ActiveHeight} @ {d.CurrentRefreshHz} Hz (signal: [{string.Join(", ", d.SignalRefreshRates)}] Hz)"
                : "Display: n/a";
            var emLine = inv.TimedDelta?.EnergyMeters is { Count: > 0 } ems
                ? $"Energy meters: {string.Join(", ", ems.Select(e => $"{e.InstanceName}={e.LoadWatts?.ToString("F1") ?? "?"}W"))}"
                : "Energy meter: none";
            QuickPowerDisplayText =
                $"Plan: {pp.ActiveSchemeName ?? "?"}  ·  Boost AC/DC: {pp.CpuBoostModeAcLabel ?? "?"}/{pp.CpuBoostModeDcLabel ?? "?"}{boostWarn}\n" +
                $"{dispLine}\n{emLine}";

            QuickOemText =
                $"Processes: {inv.OemFootprint.Processes.Count}  ·  Services: {inv.OemFootprint.Services.Count}  ·  " +
                $"Scheduled tasks: {inv.OemFootprint.ScheduledTasks.Count}  ·  Config registry keys: {inv.OemFootprint.RegistryValues.Count}";
        }

        if (_hid is { } hid)
        {
            var vc = hid.VendorControllers.Count > 0
                ? string.Join(", ", hid.VendorControllers.Select(c => $"{c.Protocol} {c.VendorId:X4}:{c.ProductId:X4} fw {c.FirmwareVersion} ({c.ProfileLength}B)"))
                : "none recognized yet (full HID table captured)";
            QuickHidText =
                $"HID collections: {hid.Collections.Count}  ·  XInput slots: {(hid.XInputActiveSlots.Count == 0 ? "none" : string.Join(", ", hid.XInputActiveSlots))}  ·  LampArray: {hid.LampArrayDevices.Count}\n" +
                $"Vendor controllers: {vc}";
        }
    }

    private void UpdateSummaryPill()
    {
        var scan = HasQuickScan ? "Quick Scan ✓" : "Quick Scan –";
        var guidedCount = _guidedDiffs.Values.Count(d => d.Kind is "guided" or "baseline");
        var watchCount = _watchDiffs.Count;
        var btnCount = _buttonEvents.Count;
        SummaryPill = $"{scan}  ·  {guidedCount} guided  ·  {watchCount} watch diffs  ·  {btnCount} button events  ·  Privacy scrubbed";
        Notify(nameof(GuidedCapturedCount));
        Notify(nameof(HasGuidedProgress));
        Notify(nameof(GuidedProgressText));
    }

    private static string FormatDiffSummary(CaptureDiffEntry diff)
    {
        if (diff.Kind == "baseline")
            return $"Baseline recorded at {diff.CapturedAtUtc.ToLocalTime():HH:mm:ss}. Future steps will diff against this state.";
        if (diff.Kind == "skipped")
            return "Step skipped.";

        var lines = new List<string>();
        var nonVolEc = diff.EcByteChanges.Where(e => !e.IsVolatile).ToList();
        var nonVolFw = diff.FirmwareChanges.Where(f => !f.IsVolatile).ToList();

        if (nonVolEc.Count > 0)
            lines.Add("EC bytes: " + string.Join(", ", nonVolEc.Select(e => $"{e.OffsetHex}: 0x{e.Before:X2}({e.Before})→0x{e.After:X2}({e.After})")));
        if (nonVolFw.Count > 0)
            lines.Add("Firmware: " + string.Join(", ", nonVolFw.Select(f => $"{f.Key}: {f.Before}→{f.After}")));
        if (diff.PowerChanges.Count > 0)
            lines.Add("Windows Power: " + string.Join(", ", diff.PowerChanges.Select(p => $"{p.Key}: {p.Before}→{p.After}")));
        if (diff.RegistryChanges.Count > 0)
            lines.Add("OEM Registry: " + string.Join(", ", diff.RegistryChanges.Select(r => $"{r.Key.Split('\\').Last()}: {r.Before}→{r.After}")));
        if (diff.HidAdded.Count > 0 || diff.HidRemoved.Count > 0)
            lines.Add($"HID Bus: +{diff.HidAdded.Count} added / -{diff.HidRemoved.Count} removed");
        if (diff.XInputBefore != diff.XInputAfter)
            lines.Add($"XInput slots: [{diff.XInputBefore}] → [{diff.XInputAfter}]");
        if (diff.ControllerMetaChanges.Count > 0)
            lines.Add("Controller: " + string.Join(", ", diff.ControllerMetaChanges.Select(c => $"{c.Key}: {c.Before}→{c.After}")));
        if (diff.ControllerProfileChanges.Count > 0)
        {
            var sample = string.Join(", ", diff.ControllerProfileChanges.Take(12).Select(p => $"[{p.Offset}] {p.Before:X2}→{p.After:X2}"));
            var extra = diff.ControllerProfileChanges.Count > 12 ? $" (+{diff.ControllerProfileChanges.Count - 12} more)" : "";
            lines.Add($"Controller Profile ({diff.ControllerProfileChanges.Count}B): {sample}{extra}");
        }

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "No non-volatile differences detected.";
    }

    private static string FormatSingleLineDiff(CaptureDiffEntry diff)
    {
        var parts = new List<string>();
        var ec = diff.EcByteChanges.Where(e => !e.IsVolatile).ToList();
        var fw = diff.FirmwareChanges.Where(f => !f.IsVolatile).ToList();

        if (ec.Count > 0) parts.Add("EC " + string.Join(" ", ec.Take(6).Select(e => $"{e.OffsetHex}:{e.Before:X2}→{e.After:X2}")));
        if (fw.Count > 0) parts.Add("FW " + string.Join(" ", fw.Take(4).Select(f => $"{f.Key}:{f.After}")));
        if (diff.RegistryChanges.Count > 0) parts.Add($"Reg({diff.RegistryChanges.Count})");
        if (diff.ControllerMetaChanges.Count > 0) parts.Add("Ctrl " + string.Join(" ", diff.ControllerMetaChanges.Select(c => $"{c.Key}={c.After}")));
        if (diff.ControllerProfileChanges.Count > 0) parts.Add($"Profile({diff.ControllerProfileChanges.Count}B)");
        if (diff.HidAdded.Count > 0 || diff.HidRemoved.Count > 0) parts.Add($"HID(+{diff.HidAdded.Count}/-{diff.HidRemoved.Count})");

        return parts.Count > 0 ? string.Join(" · ", parts) : "change detected";
    }

    private static SolidColorBrush Swatch(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static List<GuidedStepItem> CreateGuidedSteps() =>
    [
        new()
        {
            Id = "baseline",
            Number = 1,
            Category = "SETUP",
            Glyph = Glyphs.Steady,
            Title = "Set Your Baseline",
            Subtitle = "Put your handheld into its normal default state so Tsume has a clean starting point.",
            IsBaselineStep = true,
            Targets =
            [
                new("Power", "Balanced / Default"),
                new("Fans", "Auto"),
                new("Charge Limit", "Off (100%)"),
                new("Controller", "Gamepad"),
            ],
            ActionSteps =
            [
                new(1, "Open your handheld's built-in control app",
                    "For example: MSI Center M, Armoury Crate, Legion Space, or Ayaneo Space."),
                new(2, "Apply the four default settings shown above",
                    "Set power to `Balanced`, fans to `Auto`, charge limit to `100%` (off), and controller to `Gamepad`."),
                new(3, "Click [[Capture Step]] below",
                    "Tsume saves a clean reference snapshot to compare against every step that follows."),
            ],
            WhyItMatters = "Every change you make in the next steps is compared against this snapshot. Starting from known defaults makes each individual setting stand out cleanly from background sensor noise.",
            NoteHint = "Optional note (e.g., OEM app name and default profile label):",
        },
        new()
        {
            Id = "mode-endurance",
            Number = 2,
            Category = "POWER MODES",
            Glyph = Glyphs.Battery,
            Title = "Quiet / Battery Saver Mode",
            Subtitle = "Switch to your handheld's quietest, lowest-power preset.",
            Targets =
            [
                new("Power Mode", "Silent / Quiet / Super Battery"),
            ],
            ActionSteps =
            [
                new(1, "Switch to the lowest power preset in your OEM app",
                    "Usually labeled `Silent`, `Quiet`, `Endurance`, or `Super Battery`."),
                new(2, "Wait about `2 seconds`",
                    "Gives the firmware a moment to finish switching power profiles."),
                new(3, "Click [[Capture Step]] below"),
            ],
            WhyItMatters = "Shows Tsume how your handheld signals its lowest-power preset, what default wattage cap it uses, and whether it changes the Windows power mode alongside it.",
            NoteHint = "Optional note (e.g., the exact mode name in your OEM app, like \"Super Battery\"):",
        },
        new()
        {
            Id = "mode-performance",
            Number = 3,
            Category = "POWER MODES",
            Glyph = Glyphs.Sustained,
            Title = "Performance / Turbo Mode",
            Subtitle = "Switch to your handheld's highest stock performance preset.",
            Targets =
            [
                new("Power Mode", "Performance / Turbo / Sport"),
            ],
            ActionSteps =
            [
                new(1, "Switch to the highest stock preset in your OEM app",
                    "Usually labeled `Performance`, `Turbo`, `Extreme`, or `Sport`."),
                new(2, "Wait about `2 seconds`",
                    "Gives the firmware time to raise wattage limits and update fan curves."),
                new(3, "Click [[Capture Step]] below"),
            ],
            WhyItMatters = "Shows Tsume the code your handheld uses for its top performance preset, its stock wattage ceiling, and any fan behavior tied to that mode.",
            NoteHint = "Optional note (e.g., the exact preset name in your OEM app, like \"Extreme Performance\"):",
        },
        new()
        {
            Id = "mode-manual-sentinel",
            Number = 4,
            Category = "POWER MODES",
            Glyph = Glyphs.Sliders,
            Title = "Manual Power Limits (17 W / 23 W)",
            Subtitle = "Set two unequal wattages so Tsume can pinpoint sustained vs. boost power.",
            Targets =
            [
                new("Sustained (PL1 / SPL)", "17 W"),
                new("Boost (PL2 / SPPT)", "23 W"),
            ],
            ActionSteps =
            [
                new(1, "Switch to `Manual` / `Custom` power mode in your OEM app"),
                new(2, "Set Sustained power to `17 W` and Boost power to `23 W`",
                    "Only have one slider or different min/max limits? Pick any two unequal numbers and write them in the note box below."),
                new(3, "Wait about `2 seconds`, then click [[Capture Step]] below"),
            ],
            WhyItMatters = "Using two unusual, unequal numbers (17 and 23) makes it unmistakable which hardware setting controls sustained power versus short-term boost power — and how watts are scaled.",
            NoteHint = "Optional note — if you couldn't set 17 W / 23 W, type the exact wattages you used:",
        },
        new()
        {
            Id = "fan-max",
            Number = 5,
            Category = "COOLING",
            Glyph = Glyphs.Fan,
            Title = "Max Fan Speed",
            Subtitle = "Briefly turn on full-speed fan override so Tsume can detect the switch and max RPM.",
            IsOptional = true,
            Targets =
            [
                new("Fan Mode", "Max / Full Speed / Cooler Boost"),
            ],
            ActionSteps =
            [
                new(1, "Turn on `Max Fan` / `Cooler Boost` / `Full Speed` in your OEM app"),
                new(2, "Wait about `3 seconds` for the fans to spin up",
                    "This lets Tsume capture both the override switch and high-RPM fan sensor readings."),
                new(3, "Click [[Capture Step]] below"),
            ],
            AfterCaptureNote = "Switch your fans back to `Auto` after capturing so they don't stay loud for the remaining steps.",
            SkipHint = "No manual or max fan toggle on your handheld? Click [[Skip Step]].",
            WhyItMatters = "Pinpoints the exact switch that forces fans to full speed and records how your handheld reports high fan RPM readings.",
            NoteHint = "Optional note (e.g., OEM toggle name or visible fan RPM):",
        },
        new()
        {
            Id = "charge-limit",
            Number = 6,
            Category = "BATTERY",
            Glyph = Glyphs.Charging,
            Title = "Battery Charge Limit (80%)",
            Subtitle = "Turn on battery care / charge limiting so Tsume can find where the cap is stored.",
            IsOptional = true,
            Targets =
            [
                new("Charge Limit", "On · 80%"),
            ],
            ActionSteps =
            [
                new(1, "Enable `Battery Charge Limit` / `Care Mode` in your OEM app"),
                new(2, "Set the maximum charge cap to `80%`",
                    "If your handheld only offers a fixed cap (like `85%`), enable that and note the percentage below."),
                new(3, "Wait about `2 seconds`, then click [[Capture Step]] below"),
            ],
            SkipHint = "No battery charge limit option in your OEM app? Click [[Skip Step]].",
            WhyItMatters = "Reveals where your handheld stores the charge cap and whether it uses a plain percentage or combines it with a separate on/off flag.",
            NoteHint = "Optional note (e.g., if your handheld caps at a different percentage like 85%):",
        },
        new()
        {
            Id = "controller-mode",
            Number = 7,
            Category = "CONTROLLER",
            Glyph = Glyphs.Cursor,
            Title = "Controller Mode — Desktop / Mouse",
            Subtitle = "Switch built-in controls from Gamepad to Desktop mode to see how switching works.",
            IsOptional = true,
            Targets =
            [
                new("Controller Mode", "Desktop / Mouse"),
            ],
            ActionSteps =
            [
                new(1, "Switch the built-in controller from `Gamepad` to `Desktop / Mouse` mode",
                    "Use your OEM app or your handheld's quick-toggle shortcut."),
                new(2, "Wait about `2 seconds`",
                    "Gives Windows a moment to finish updating USB or XInput devices if the controller reconnects."),
                new(3, "Click [[Capture Step]] below"),
            ],
            AfterCaptureNote = "Switch your controller back to `Gamepad` mode after capturing.",
            SkipHint = "No `Desktop / Mouse` controller mode? Click [[Skip Step]].",
            WhyItMatters = "Shows the exact setting that toggles Gamepad vs. Desktop mode and whether your controller stays connected or briefly reconnects over USB.",
            NoteHint = "Optional note (e.g., OEM mode name or whether Windows played a USB disconnect chime):",
        },
        new()
        {
            Id = "rgb-red-100",
            Number = 8,
            Category = "LIGHTING",
            Glyph = Glyphs.Lighting,
            Title = "RGB Lighting — Solid Red (100%)",
            Subtitle = "Set stick or button LEDs to pure full-brightness Red as the first color test.",
            IsOptional = true,
            Targets =
            [
                new("Effect", "Solid / Static"),
                new("Color", "Pure Red (#FF0000)", Swatch(255, 59, 59)),
                new("Brightness", "100%"),
            ],
            ActionSteps =
            [
                new(1, "Open the RGB / lighting settings in your OEM app"),
                new(2, "Set effect to `Solid (Static)`, color to `#FF0000` (Pure Red), at `100%` brightness",
                    "No hex input? Set `R: 255`, `G: 0`, `B: 0`, or choose the purest solid red preset."),
                new(3, "Click [[Capture Step]] below"),
            ],
            SkipHint = "No RGB lighting on this handheld? Click [[Skip Step]].",
            WhyItMatters = "Lighting only the Red channel at full brightness makes the LED data easy to spot in memory and reveals how many LEDs your handheld has and their color order (RGB vs. GRB).",
            NoteHint = "Optional note (e.g., if your OEM app only offers color presets instead of hex/RGB sliders):",
        },
        new()
        {
            Id = "rgb-blue-50",
            Number = 9,
            Category = "LIGHTING",
            Glyph = Glyphs.Lighting,
            Title = "RGB Lighting — Solid Blue (50%)",
            Subtitle = "Switch LEDs to half-brightness pure Blue to separate color from brightness.",
            IsOptional = true,
            Targets =
            [
                new("Effect", "Solid / Static"),
                new("Color", "Pure Blue (#0000FF)", Swatch(59, 130, 246)),
                new("Brightness", "50%"),
            ],
            ActionSteps =
            [
                new(1, "Keep the lighting effect on `Solid (Static)` in your OEM app"),
                new(2, "Change color to `#0000FF` (Pure Blue) and brightness to `50%`",
                    "No hex input? Set `R: 0`, `G: 0`, `B: 255`, with brightness halfway."),
                new(3, "Click [[Capture Step]] below"),
            ],
            SkipHint = "No RGB lighting on this handheld? Click [[Skip Step]].",
            WhyItMatters = "Comparing 100% Red against 50% Blue separates which bytes control the color from which byte controls brightness — and shows whether brightness scales from 0–100 or 0–255.",
            NoteHint = "Optional note (e.g., if brightness uses fixed levels like Low / Medium / High):",
        },
        new()
        {
            Id = "back-paddles",
            Number = 10,
            Category = "CONTROLLER",
            Glyph = Glyphs.Buttons,
            Title = "Remap Back Paddles",
            Subtitle = "Assign two known actions to your rear buttons so Tsume can locate their mapping.",
            IsOptional = true,
            Targets =
            [
                new("Left Back (M1 / L4)", "F12"),
                new("Right Back (M2 / R4)", "Gamepad A"),
            ],
            ActionSteps =
            [
                new(1, "Open button remapping in your OEM app"),
                new(2, "Map Left back paddle (`M1` / `L4`) to keyboard key `F12`"),
                new(3, "Map Right back paddle (`M2` / `R4`) to `Gamepad A`",
                    "If either option isn't available, pick another distinct key and note what you chose below."),
                new(4, "Click [[Capture Step]] below"),
            ],
            SkipHint = "No rear paddles or extra back buttons? Click [[Skip Step]].",
            WhyItMatters = "Assigning one keyboard key and one gamepad button reveals where each paddle's mapping is stored and how keyboard vs. gamepad actions are encoded.",
            NoteHint = "Optional note (e.g., if you mapped different keys or also have M3 / M4 buttons):",
        },
        new()
        {
            Id = "dedicated-buttons",
            Number = 11,
            Category = "BUTTONS",
            Glyph = Glyphs.Gamepad,
            Title = "Test Hardware Buttons",
            Subtitle = "Press your handheld's extra buttons while Tsume listens so it can identify each signal.",
            IsOptional = true,
            IsButtonCaptureStep = true,
            ActionSteps =
            [
                new(1, "Pick a button from the list below",
                    "Start with `OEM button` or `Quick Settings / Game Bar`."),
                new(2, "Click [[Start Listening]]",
                    "Tsume passively watches WMI events, keyboard chords (like `Win+G` or `F15`), raw HID, and XInput at the same time."),
                new(3, "Press that physical button on your handheld",
                    "Repeat for each extra button you want to test, then click [[Stop Listening]] when done."),
            ],
            SkipHint = "Don't need hardware button capture right now? Click [[Skip Step]].",
            WhyItMatters = "Every handheld sends different signals for its menu, Quick Settings, and rear buttons. Recording each press lets Tsume recognize or remap them cleanly.",
        },
    ];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();
        StopWatchMode();
        StopButtonListening();
        _lifetimeCts.Dispose();
    }
}

public sealed record ButtonPromptOption(string Id, string Label, string CodeTag)
{
    public override string ToString() => Label;
}
