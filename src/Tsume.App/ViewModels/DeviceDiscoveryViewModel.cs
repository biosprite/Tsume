using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
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

public sealed record QuickScanRow(
    string Label,
    string Value,
    bool IsFirst = false,
    bool IsWarning = false,
    bool IsMuted = false);

public sealed record QuickScanSection(
    string Title,
    string Glyph,
    IReadOnlyList<QuickScanRow> Rows,
    string? BadgeText = null,
    bool IsWarningBadge = false)
{
    public bool HasBadge => !string.IsNullOrWhiteSpace(BadgeText);
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
    public string? AfterCaptureNote { get; init; }
    public string? SkipHint { get; init; }
    public required string WhyItMatters { get; init; }
    public string NoteHint { get; init; } = "Optional note:";
    public bool IsButtonCaptureStep { get; init; }
    public bool IsBaselineStep { get; init; }
    public bool IsOptional { get; init; }

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

    public string UserNote { get; set => Set(ref field, value); } = "";

    public bool IsCaptured
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Notify(nameof(SidebarStatus));
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
    private const int MaxHistoryEntries = 50;
    private const string LiveWatchPromptId = "live_watch";

    private sealed record GuidedStepState(
        bool IsCaptured,
        bool IsSkipped,
        string Status,
        string Detail,
        string UserNote);

    private sealed record GuidedHistoryEntry(
        string Description,
        int SelectedStepIndex,
        CombinedStateSnapshot? Baseline,
        Dictionary<string, CombinedStateSnapshot> GuidedSnapshots,
        Dictionary<string, CaptureDiffEntry> GuidedDiffs,
        Dictionary<string, GuidedStepState> StepStates,
        List<CapturedButtonEvent> GuidedButtonEvents);

    private readonly HelperClient _helper;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Func<nint> _getWindowHandle = () => 0;

    private ElevatedInventory? _inventory;
    private HidTopologySnapshot? _hid;
    private DisplayInventorySnapshot? _display;
    private HardwareEnvironmentCheck? _envCheck;
    private CombinedStateSnapshot? _baseline;
    private CombinedStateSnapshot? _fallbackBaseline;
    private CombinedStateSnapshot? _watchPrevious;

    private readonly HashSet<string> _volatileFirmwareKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _volatileEcOffsets = [];
    private readonly Dictionary<string, CombinedStateSnapshot> _guidedSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CaptureDiffEntry> _guidedDiffs = new(StringComparer.Ordinal);
    private readonly List<CaptureDiffEntry> _watchDiffs = [];
    private readonly List<CapturedButtonEvent> _buttonEvents = [];
    private readonly List<GuidedHistoryEntry> _undoStack = [];
    private readonly List<GuidedHistoryEntry> _redoStack = [];

    private ButtonCaptureSession? _buttonSession;
    private CancellationTokenSource? _watchCts;
    private int _watchChangeIndex;
    private bool _listeningUndoRecorded;
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
        ClearStepCommand = new Command(ClearCurrentStep);
        SkipStepCommand = new Command(SkipCurrentStep);
        UndoGuidedCommand = new Command(UndoGuided);
        RedoGuidedCommand = new Command(RedoGuided);
        UndoLastButtonEventCommand = new Command(UndoLastButtonEvent);
        ClearSelectedButtonPromptCommand = new Command(ClearSelectedButtonPrompt);
        PreviousStepCommand = new Command(() => SelectStepIndex(SelectedStepIndex - 1));
        NextStepCommand = new Command(() => SelectStepIndex(SelectedStepIndex + 1));
        ToggleButtonListeningCommand = new Command(ToggleButtonListening);
        ToggleWatchModeCommand = new Command(async () => await ToggleWatchModeAsync());
        RefreshPreviewCommand = new Command(RefreshPreview);
        ExportBundleCommand = new Command(async () => await ExportBundleAsync());

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
            if (value is null) return;
            if (Set(ref field, value))
            {
                if (_buttonSession is not null && !IsWatching)
                {
                    _buttonSession.ActivePromptId = value.Id;
                    _listeningUndoRecorded = false;
                }
                NotifyButtonActionState();
            }
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

    public bool IncludeTimedLoad
    {
        get;
        set
        {
            if (Set(ref field, value)) Notify(nameof(QuickScanRunningDetail));
        }
    } = true;

    public bool AllowUacFallback { get; set => Set(ref field, value); } = true;

    public bool IsBusy
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Notify(nameof(IsNotBusy));
                NotifyGuidedActionState();
                NotifyHistoryState();
                NotifyButtonActionState();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string StatusBanner { get; private set => Set(ref field, value); } =
        "100% read-only. Nothing is written to firmware, registers, or controller flash.";

    public int SummaryGuidedCount { get; private set => Set(ref field, value); }
    public bool HasSummaryGuided => SummaryGuidedCount > 0;
    public int SummaryWatchCount { get; private set => Set(ref field, value); }
    public bool HasSummaryWatch => SummaryWatchCount > 0;
    public string SummaryWatchLabel => SummaryWatchCount == 1 ? "watch diff" : "watch diffs";
    public int SummaryButtonCount { get; private set => Set(ref field, value); }
    public bool HasSummaryButtons => SummaryButtonCount > 0;
    public string SummaryButtonLabel => SummaryButtonCount == 1 ? "button event" : "button events";

    public string ExportMessage
    {
        get;
        private set
        {
            if (Set(ref field, value)) Notify(nameof(HasExportMessage));
        }
    } = "";

    public bool HasExportMessage => !string.IsNullOrWhiteSpace(ExportMessage);

    // Quick scan summary fields
    public bool HasQuickScan
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Notify(nameof(QuickScanButtonLabel));
                Notify(nameof(ShowQuickScanEmptyState));
                Notify(nameof(ShowQuickScanFullLoader));
                Notify(nameof(ShowQuickScanRefreshBanner));
            }
        }
    }

    public bool IsQuickScanning
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Notify(nameof(QuickScanButtonLabel));
                Notify(nameof(ShowQuickScanEmptyState));
                Notify(nameof(ShowQuickScanFullLoader));
                Notify(nameof(ShowQuickScanRefreshBanner));
            }
        }
    }

    public bool ShowQuickScanEmptyState => !HasQuickScan && !IsQuickScanning;
    public bool ShowQuickScanFullLoader => !HasQuickScan && IsQuickScanning;
    public bool ShowQuickScanRefreshBanner => HasQuickScan && IsQuickScanning;

    public string QuickScanButtonLabel => IsQuickScanning
        ? "Scanning…"
        : HasQuickScan ? "Scan Again" : "Run Quick Scan";

    public string QuickScanRunningDetail => IncludeTimedLoad
        ? "Reading device specs, display, power settings, controllers, and running the 3-second CPU load test (100% read-only)."
        : "Reading device specs, display, power settings, controllers, and OEM software (100% read-only).";

    public ObservableCollection<QuickScanSection> QuickScanSections { get; } = [];

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
                Notify(nameof(StepUserNote));
                NotifyGuidedActionState();
                NotifyButtonActionState();
            }
        }
    }

    public int SelectedStepIndex => Steps.IndexOf(SelectedStep);
    public bool CanGoPrevious => SelectedStepIndex > 0;
    public bool CanGoNext => SelectedStepIndex < Steps.Count - 1;
    public bool IsButtonStepSelected => SelectedStep.IsButtonCaptureStep;

    public string CaptureButtonLabel => SelectedStep.IsCaptured ? "Recapture Step" : "Capture Step";
    public string CaptureButtonTooltip => SelectedStep.IsCaptured
        ? "Re-run read-only capture for this step and replace its previous snapshot"
        : "Capture read-only hardware state for this step";

    public bool ShowClearSelectedStep => SelectedStep.IsCaptured || SelectedStep.IsSkipped;
    public bool CanClearSelectedStep => IsNotBusy && ShowClearSelectedStep;
    public string ClearStepButtonLabel => SelectedStep.IsSkipped ? "Undo Skip" : "Clear Capture";

    public bool CanUndoGuided => IsNotBusy && _undoStack.Count > 0;
    public bool CanRedoGuided => IsNotBusy && _redoStack.Count > 0;
    public string UndoGuidedTooltip => _undoStack.Count > 0
        ? $"Undo {_undoStack[^1].Description} (Ctrl+Z)"
        : "Nothing to undo (Ctrl+Z)";
    public string RedoGuidedTooltip => _redoStack.Count > 0
        ? $"Redo {_redoStack[^1].Description} (Ctrl+Y)"
        : "Nothing to redo (Ctrl+Y)";

    private int GuidedButtonEventCount => _buttonEvents.Count(e => !IsLiveWatchEvent(e));
    public bool CanUndoLastButtonEvent => IsNotBusy && GuidedButtonEventCount > 0;
    public bool CanClearSelectedButtonPrompt => IsNotBusy && SelectedButtonPrompt.CapturedCount > 0;
    public string ClearSelectedPromptLabel => SelectedButtonPrompt.CapturedCount > 0
        ? $"Clear Selected ({SelectedButtonPrompt.CapturedCount})"
        : "Clear Selected";

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

    public string StepUserNote
    {
        get => SelectedStep.UserNote;
        set
        {
            if (SelectedStep.UserNote == value) return;
            SelectedStep.UserNote = value;
            Notify(nameof(StepUserNote));

            if (!SelectedStep.IsButtonCaptureStep && _guidedDiffs.TryGetValue(SelectedStep.Id, out var existing))
            {
                var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (!string.Equals(existing.UserNote, trimmed, StringComparison.Ordinal))
                    _guidedDiffs[SelectedStep.Id] = existing with { UserNote = trimmed };
            }
        }
    }

    public string SelectedStepDiffPreview
    {
        get
        {
            if (SelectedStep.IsButtonCaptureStep)
            {
                var guidedCount = GuidedButtonEventCount;
                if (guidedCount == 0)
                    return SelectedStep.IsSkipped
                        ? "Step skipped."
                        : "Start listening and press each dedicated hardware button on the handheld.";

                var breakdown = ButtonPrompts
                    .Where(p => p.CapturedCount > 0)
                    .Select(p => $"{p.Label} ({p.CodeTag}): {p.CapturedCount}");
                return $"{guidedCount} button event(s) captured across WMI / Keyboard / HID / XInput."
                    + Environment.NewLine
                    + string.Join(" · ", breakdown);
            }

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
        "Click Start Watching, then freely toggle power modes, manual power sliders (PL1/PL2 or SPL/SPPT/FPPT), fans, charge limit, RGB or press hardware buttons.";

    public string ReportPreviewText { get; private set => Set(ref field, value); } =
        "Run Quick Scan, Guided Capture, or Live Watch to generate the preview report.";

    public ICommand RunQuickScanCommand { get; }
    public ICommand CaptureStepCommand { get; }
    public ICommand ClearStepCommand { get; }
    public ICommand SkipStepCommand { get; }
    public ICommand UndoGuidedCommand { get; }
    public ICommand RedoGuidedCommand { get; }
    public ICommand UndoLastButtonEventCommand { get; }
    public ICommand ClearSelectedButtonPromptCommand { get; }
    public ICommand PreviousStepCommand { get; }
    public ICommand NextStepCommand { get; }
    public ICommand ToggleButtonListeningCommand { get; }
    public ICommand ToggleWatchModeCommand { get; }
    public ICommand RefreshPreviewCommand { get; }
    public ICommand ExportBundleCommand { get; }

    public void SelectStepIndex(int index)
    {
        if (index < 0 || index >= Steps.Count) return;
        SelectedStep = Steps[index];
    }

    public async Task RunQuickScanAsync()
    {
        if (IsBusy || _disposed) return;
        if (IsWatching) StopWatchMode();
        IsBusy = true;
        IsQuickScanning = true;
        ExportMessage = "";
        StatusBanner = IncludeTimedLoad
            ? "Scanning your handheld (including 3-second load test, read-only)…"
            : "Scanning your handheld (read-only)…";

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

            // Also seed fallback baseline automatically if not yet captured.
            if (_fallbackBaseline is null || _baseline is null)
            {
                var quickBaseline = new CombinedStateSnapshot
                {
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    Elevated = await DeviceSupportExporter.CollectFastStateAsync(_helper, cancel),
                    Hid = _hid,
                    CurrentRefreshHz = _display.CurrentRefreshHz,
                };
                if (_disposed) return;
                _fallbackBaseline ??= quickBaseline;
                _baseline ??= quickBaseline;
            }

            PopulateQuickSummary();
            HasQuickScan = true;
            StatusBanner = _inventory.Elevated
                ? "Quick scan complete. Continue to Guided Capture or export the support bundle."
                : "Quick scan complete (limited mode — start Tsume Helper for full hardware access).";
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
                IsQuickScanning = false;
                IsBusy = false;
                UpdateSummaryPill();
                RefreshPreview();
            }
        }
    }

    private async Task CaptureCurrentStepAsync()
    {
        if (IsBusy || _disposed || SelectedStep.IsButtonCaptureStep) return;
        if (IsWatching) StopWatchMode();
        IsBusy = true;

        var step = SelectedStep;
        var wasCaptured = step.IsCaptured;
        var note = string.IsNullOrWhiteSpace(StepUserNote) ? null : StepUserNote.Trim();
        StatusBanner = wasCaptured
            ? $"Recapturing {step.Title} (read-only)…"
            : $"Capturing {step.Title} (read-only)…";

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

            PushUndoState(wasCaptured ? $"recapture {step.Title}" : $"capture {step.Title}");
            _guidedSnapshots[step.Id] = current;

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

                var recomputed = step.IsBaselineStep ? RecomputeGuidedDiffsAgainstBaseline() : 0;
                StatusBanner = recomputed > 0
                    ? $"Baseline {(wasCaptured ? "recaptured" : "captured")}. Recomputed diffs for {recomputed} other captured step(s)."
                    : wasCaptured
                        ? "Baseline recaptured. Proceed to the next step."
                        : "Baseline captured. Proceed to the next step.";
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
                StatusBanner = wasCaptured
                    ? $"{step.Title} recaptured: {count} non-volatile difference(s) recorded."
                    : $"{step.Title}: {count} non-volatile difference(s) recorded.";
            }

            NotifyGuidedActionState();
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

    private void ClearCurrentStep()
    {
        if (IsBusy || _disposed) return;
        var step = SelectedStep;
        if (!step.IsCaptured && !step.IsSkipped) return;

        var wasSkipped = step.IsSkipped;
        if (step.IsButtonCaptureStep)
        {
            StopButtonListening();
            PushUndoState(wasSkipped ? $"clear skip for {step.Title}" : $"clear {step.Title}");
            var removed = _buttonEvents.RemoveAll(e => !IsLiveWatchEvent(e));
            step.IsCaptured = false;
            step.IsSkipped = false;
            step.Status = "Pending";
            step.Detail = "";
            RebuildButtonFeedAndCounts();
            StatusBanner = wasSkipped
                ? $"Cleared skip for {step.Title}."
                : $"Cleared {removed} guided button event(s).";
        }
        else
        {
            PushUndoState(wasSkipped ? $"clear skip for {step.Title}" : $"clear {step.Title}");
            _guidedDiffs.Remove(step.Id);
            var hadSnapshot = _guidedSnapshots.Remove(step.Id, out var removedSnapshot);
            step.IsCaptured = false;
            step.IsSkipped = false;
            step.Status = "Pending";
            step.Detail = "";
            step.UserNote = "";
            Notify(nameof(StepUserNote));

            if (step.IsBaselineStep || (hadSnapshot && ReferenceEquals(_baseline, removedSnapshot)))
            {
                _baseline = _fallbackBaseline;
                var recomputed = _baseline is not null ? RecomputeGuidedDiffsAgainstBaseline() : 0;
                StatusBanner = _baseline is null && _guidedSnapshots.Count > 0
                    ? "Baseline cleared. Recapture Step 1 to re-diff your other captured steps."
                    : recomputed > 0
                        ? $"Baseline cleared. Recomputed {recomputed} step(s) against Quick Scan baseline."
                        : wasSkipped
                            ? $"Cleared skip for {step.Title}."
                            : $"Cleared capture for {step.Title}.";
            }
            else
            {
                StatusBanner = wasSkipped
                    ? $"Cleared skip for {step.Title}."
                    : $"Cleared capture for {step.Title}.";
            }
        }

        NotifyGuidedActionState();
        NotifyButtonActionState();
        Notify(nameof(SelectedStepDiffPreview));
        UpdateSummaryPill();
    }

    private void SkipCurrentStep()
    {
        if (IsBusy || _disposed) return;
        var step = SelectedStep;
        PushUndoState($"skip {step.Title}");

        if (step.IsButtonCaptureStep)
        {
            StopButtonListening();
            _buttonEvents.RemoveAll(e => !IsLiveWatchEvent(e));
            RebuildButtonFeedAndCounts();
        }
        else
        {
            var hadSnapshot = _guidedSnapshots.Remove(step.Id, out var removedSnapshot);
            _guidedDiffs[step.Id] = new CaptureDiffEntry
            {
                StepId = step.Id,
                Title = step.Title,
                Kind = "skipped",
                UserNote = string.IsNullOrWhiteSpace(StepUserNote) ? null : StepUserNote.Trim(),
            };

            if (step.IsBaselineStep || (hadSnapshot && ReferenceEquals(_baseline, removedSnapshot)))
            {
                _baseline = _fallbackBaseline;
                if (_baseline is not null) RecomputeGuidedDiffsAgainstBaseline();
            }
        }

        step.IsSkipped = true;
        step.IsCaptured = false;
        step.Status = "Skipped";
        step.Detail = "Skipped";
        NotifyGuidedActionState();
        NotifyButtonActionState();
        Notify(nameof(SelectedStepDiffPreview));
        UpdateSummaryPill();
        StatusBanner = $"Skipped {step.Title}.";

        if (CanGoNext) SelectStepIndex(SelectedStepIndex + 1);
    }

    private void UndoGuided()
    {
        if (IsBusy || _disposed || _undoStack.Count == 0) return;
        if (IsButtonListening) StopButtonListening();

        var previous = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        _redoStack.Add(CaptureHistoryEntry(previous.Description));
        if (_redoStack.Count > MaxHistoryEntries) _redoStack.RemoveAt(0);

        RestoreHistoryEntry(previous);
        StatusBanner = $"Undid {previous.Description}.";
    }

    private void RedoGuided()
    {
        if (IsBusy || _disposed || _redoStack.Count == 0) return;
        if (IsButtonListening) StopButtonListening();

        var next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        _undoStack.Add(CaptureHistoryEntry(next.Description));
        if (_undoStack.Count > MaxHistoryEntries) _undoStack.RemoveAt(0);

        RestoreHistoryEntry(next);
        StatusBanner = $"Redid {next.Description}.";
    }

    private void UndoLastButtonEvent()
    {
        if (IsBusy || _disposed) return;

        var lastIndex = _buttonEvents.FindLastIndex(e => !IsLiveWatchEvent(e));
        if (lastIndex < 0) return;

        var removed = _buttonEvents[lastIndex];
        PushUndoState($"remove button event ({removed.PromptId})");
        _buttonEvents.RemoveAt(lastIndex);
        _listeningUndoRecorded = false;

        RebuildButtonFeedAndCounts();
        SyncButtonStepStatus();
        NotifyGuidedActionState();
        NotifyButtonActionState();
        Notify(nameof(SelectedStepDiffPreview));
        UpdateSummaryPill();
        StatusBanner = $"Removed last button event ({removed.PromptId} · {removed.Source}).";
    }

    private void ClearSelectedButtonPrompt()
    {
        if (IsBusy || _disposed) return;
        var prompt = SelectedButtonPrompt;
        if (prompt.CapturedCount == 0) return;

        PushUndoState($"clear {prompt.Label} ({prompt.CodeTag})");
        var removed = _buttonEvents.RemoveAll(e => string.Equals(e.PromptId, prompt.Id, StringComparison.Ordinal));
        _listeningUndoRecorded = false;

        RebuildButtonFeedAndCounts();
        SyncButtonStepStatus();
        NotifyGuidedActionState();
        NotifyButtonActionState();
        Notify(nameof(SelectedStepDiffPreview));
        UpdateSummaryPill();
        StatusBanner = $"Cleared {removed} event(s) for {prompt.Label} ({prompt.CodeTag}).";
    }

    private int RecomputeGuidedDiffsAgainstBaseline()
    {
        if (_baseline is null) return 0;

        var recomputed = 0;
        foreach (var step in Steps)
        {
            if (step.IsBaselineStep || step.IsButtonCaptureStep) continue;
            if (!_guidedSnapshots.TryGetValue(step.Id, out var snap)) continue;

            var note = string.IsNullOrWhiteSpace(step.UserNote) ? null : step.UserNote.Trim();
            var diff = DiscoveryDiffEngine.Compute(
                step.Id,
                step.Title,
                kind: "guided",
                _baseline,
                snap,
                _volatileFirmwareKeys,
                _volatileEcOffsets,
                note);

            _guidedDiffs[step.Id] = diff;
            step.IsCaptured = true;
            step.IsSkipped = false;
            var count = diff.NonVolatileChangeCount;
            step.Status = count > 0 ? $"Captured (+{count})" : "Captured (0 diffs)";
            step.Detail = count > 0 ? $"{count} non-volatile change(s)" : "No non-volatile changes";
            recomputed++;
        }

        return recomputed;
    }

    private GuidedHistoryEntry CaptureHistoryEntry(string description)
    {
        var stepStates = new Dictionary<string, GuidedStepState>(Steps.Count, StringComparer.Ordinal);
        foreach (var step in Steps)
        {
            stepStates[step.Id] = new GuidedStepState(
                step.IsCaptured,
                step.IsSkipped,
                step.Status,
                step.Detail,
                step.UserNote);
        }

        return new GuidedHistoryEntry(
            description,
            SelectedStepIndex,
            _baseline,
            new Dictionary<string, CombinedStateSnapshot>(_guidedSnapshots, StringComparer.Ordinal),
            new Dictionary<string, CaptureDiffEntry>(_guidedDiffs, StringComparer.Ordinal),
            stepStates,
            _buttonEvents.Where(e => !IsLiveWatchEvent(e)).ToList());
    }

    private void PushUndoState(string description)
    {
        _undoStack.Add(CaptureHistoryEntry(description));
        if (_undoStack.Count > MaxHistoryEntries)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
        NotifyHistoryState();
    }

    private void RestoreHistoryEntry(GuidedHistoryEntry entry)
    {
        _baseline = entry.Baseline;

        _guidedSnapshots.Clear();
        foreach (var kv in entry.GuidedSnapshots) _guidedSnapshots[kv.Key] = kv.Value;

        _guidedDiffs.Clear();
        foreach (var kv in entry.GuidedDiffs) _guidedDiffs[kv.Key] = kv.Value;

        foreach (var step in Steps)
        {
            if (!entry.StepStates.TryGetValue(step.Id, out var st)) continue;
            step.IsCaptured = st.IsCaptured;
            step.IsSkipped = st.IsSkipped;
            step.Status = st.Status;
            step.Detail = st.Detail;
            step.UserNote = st.UserNote;
        }

        var watchEvents = _buttonEvents.Where(IsLiveWatchEvent).ToList();
        _buttonEvents.Clear();
        _buttonEvents.AddRange(watchEvents);
        _buttonEvents.AddRange(entry.GuidedButtonEvents);
        _buttonEvents.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));

        RebuildButtonFeedAndCounts();

        if (entry.SelectedStepIndex >= 0 && entry.SelectedStepIndex < Steps.Count)
            SelectedStep = Steps[entry.SelectedStepIndex];

        Notify(nameof(StepUserNote));
        Notify(nameof(SelectedStepDiffPreview));
        NotifyGuidedActionState();
        NotifyButtonActionState();
        NotifyHistoryState();
        UpdateSummaryPill();
    }

    private void RebuildButtonFeedAndCounts()
    {
        ButtonFeed.Clear();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var guidedEvents = _buttonEvents.Where(e => !IsLiveWatchEvent(e)).ToList();

        foreach (var ev in guidedEvents)
            counts[ev.PromptId] = counts.TryGetValue(ev.PromptId, out var c) ? c + 1 : 1;

        for (var i = guidedEvents.Count - 1; i >= 0 && ButtonFeed.Count < 200; i--)
            ButtonFeed.Add(FormatButtonEventLine(guidedEvents[i]));

        foreach (var prompt in ButtonPrompts)
            prompt.CapturedCount = counts.TryGetValue(prompt.Id, out var c) ? c : 0;
    }

    private void SyncButtonStepStatus()
    {
        var buttonStep = Steps.FirstOrDefault(s => s.IsButtonCaptureStep);
        if (buttonStep is null) return;

        var count = GuidedButtonEventCount;
        if (count == 0)
        {
            buttonStep.IsCaptured = false;
            if (!buttonStep.IsSkipped)
            {
                buttonStep.Status = "Pending";
                buttonStep.Detail = "";
            }
        }
        else
        {
            buttonStep.IsCaptured = true;
            buttonStep.IsSkipped = false;
            buttonStep.Status = count == 1 ? "Captured (1 event)" : $"Captured ({count} events)";
            buttonStep.Detail = $"{count} event(s)";
        }
    }

    private void NotifyGuidedActionState()
    {
        Notify(nameof(CaptureButtonLabel));
        Notify(nameof(CaptureButtonTooltip));
        Notify(nameof(ShowClearSelectedStep));
        Notify(nameof(CanClearSelectedStep));
        Notify(nameof(ClearStepButtonLabel));
    }

    private void NotifyHistoryState()
    {
        Notify(nameof(CanUndoGuided));
        Notify(nameof(CanRedoGuided));
        Notify(nameof(UndoGuidedTooltip));
        Notify(nameof(RedoGuidedTooltip));
    }

    private void NotifyButtonActionState()
    {
        Notify(nameof(CanUndoLastButtonEvent));
        Notify(nameof(CanClearSelectedButtonPrompt));
        Notify(nameof(ClearSelectedPromptLabel));
    }

    private static bool IsLiveWatchEvent(CapturedButtonEvent ev) =>
        string.Equals(ev.PromptId, LiveWatchPromptId, StringComparison.Ordinal);

    private static string FormatButtonEventLine(CapturedButtonEvent ev) =>
        $"[+{ev.ElapsedMs,6} ms] [{ev.PromptId}] [{ev.Source}] {ev.Summary}";

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
        _listeningUndoRecorded = false;

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
        _listeningUndoRecorded = false;
        IsButtonListening = false;
    }

    private void OnButtonEventCaptured(CapturedButtonEvent ev)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;

            var isWatch = IsLiveWatchEvent(ev);
            if (!isWatch && !_listeningUndoRecorded)
            {
                var prompt = ButtonPrompts.FirstOrDefault(p => string.Equals(p.Id, ev.PromptId, StringComparison.Ordinal));
                var label = prompt is null ? ev.PromptId : $"{prompt.Label} ({prompt.CodeTag})";
                PushUndoState($"button capture ({label})");
                _listeningUndoRecorded = true;
            }

            if (_buttonEvents.Count >= MaxButtonEvents)
            {
                var evicted = _buttonEvents[0];
                _buttonEvents.RemoveAt(0);
                if (!IsLiveWatchEvent(evicted))
                {
                    var evictedPrompt = ButtonPrompts.FirstOrDefault(p => string.Equals(p.Id, evicted.PromptId, StringComparison.Ordinal));
                    if (evictedPrompt is not null && evictedPrompt.CapturedCount > 0)
                        evictedPrompt.CapturedCount--;
                }
            }

            _buttonEvents.Add(ev);
            var line = FormatButtonEventLine(ev);

            if (isWatch)
            {
                LiveFeed.Insert(0, line);
                if (LiveFeed.Count > 300) LiveFeed.RemoveAt(LiveFeed.Count - 1);
            }
            else
            {
                ButtonFeed.Insert(0, line);
                if (ButtonFeed.Count > 200) ButtonFeed.RemoveAt(ButtonFeed.Count - 1);

                var prompt = ButtonPrompts.FirstOrDefault(p => string.Equals(p.Id, ev.PromptId, StringComparison.Ordinal));
                if (prompt is not null) prompt.CapturedCount++;

                SyncButtonStepStatus();
                NotifyGuidedActionState();
                NotifyButtonActionState();
                Notify(nameof(SelectedStepDiffPreview));
            }

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
            _fallbackBaseline ??= _watchPrevious;
            _baseline ??= _watchPrevious;

            StartButtonListening(LiveWatchPromptId);

            _watchCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            IsWatching = true;
            WatchStatusText = "Watching live (polling read-only state every 600 ms + capturing all buttons)…";
            StatusBanner = "Live Watch Mode active. Toggle power modes, manual power sliders (PL1/PL2 or SPL/SPPT/FPPT), fans, charge limit, or RGB in your OEM app, or press hardware buttons.";
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
            }

            _hid ??= await Task.Run(() => HidInventory.CaptureAsync(readVendorProfiles: true), cancel);
            if (_disposed) return;
            _display ??= DisplayInventory.Capture();
            _envCheck ??= await DeviceSupportExporter.RunEnvironmentCheckAsync(_helper, _inventory, _hid, cancel);
            if (_disposed) return;

            if (!HasQuickScan)
            {
                PopulateQuickSummary();
                HasQuickScan = true;
            }

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
        QuickScanSections.Clear();

        if (_inventory is { } inv)
        {
            var id = inv.Identity;
            var model = FirstNonEmpty(id.SystemModel, id.ProductName, id.BaseBoardProduct) ?? "Unknown";
            var board = JoinNonEmpty(" · ", CleanSpaces(id.BaseBoardManufacturer), CleanSpaces(id.BaseBoardProduct));
            var cpuName = CleanSpaces(id.CpuName);
            var cpu = string.IsNullOrWhiteSpace(cpuName)
                ? "Unknown"
                : id.CpuCores > 0
                    ? $"{cpuName} · {CountLabel(id.CpuCores, "core")}, {CountLabel(id.CpuLogicalProcessors, "thread")}"
                    : cpuName;
            var gpus = string.Join(", ", id.Gpus.Select(g => CleanSpaces(g.Name)).Where(n => !string.IsNullOrWhiteSpace(n)));
            var bios = CleanSpaces(id.BiosVersion);

            QuickScanSections.Add(new QuickScanSection(
                "DEVICE",
                Glyphs.Chip,
                BuildRows(
                    ("Model", model, false, model == "Unknown"),
                    ("Motherboard", string.IsNullOrWhiteSpace(board) ? "Unknown" : board, false, string.IsNullOrWhiteSpace(board)),
                    ("Processor", cpu, false, cpu == "Unknown"),
                    ("Graphics", string.IsNullOrWhiteSpace(gpus) ? "Unknown" : gpus, false, string.IsNullOrWhiteSpace(gpus)),
                    ("BIOS", string.IsNullOrWhiteSpace(bios) ? "Unknown" : bios, false, string.IsNullOrWhiteSpace(bios)))));

            var pp = inv.PowerPlan;
            var boostDisabled = pp.CpuBoostModeAc == 0 || pp.CpuBoostModeDc == 0;
            var displayText = FormatDisplaySummary(_display);
            var planText = string.IsNullOrWhiteSpace(pp.ActiveSchemeName) ? "Unknown" : pp.ActiveSchemeName;
            var boostText = FormatBoostSummary(pp, boostDisabled);
            var (powerText, powerMuted) = FormatPowerSensorsSummary(inv.TimedDelta);

            QuickScanSections.Add(new QuickScanSection(
                "DISPLAY & POWER",
                Glyphs.Desktop,
                BuildRows(
                    ("Display", displayText, false, displayText == "Unavailable"),
                    ("Power plan", planText, false, planText == "Unknown"),
                    ("CPU boost", boostText, boostDisabled, boostText == "Unknown"),
                    ("Power sensors", powerText, false, powerMuted)),
                BadgeText: boostDisabled ? "Boost disabled" : null,
                IsWarningBadge: boostDisabled));
        }

        if (_hid is { } hid)
        {
            var controllerText = hid.VendorControllers.Count > 0
                ? string.Join(", ", hid.VendorControllers.Select(FormatVendorController))
                : "Generic USB/HID (full device table logged)";
            var gamepadText = hid.XInputActiveSlots.Count > 0
                ? $"{CountLabel(hid.XInputActiveSlots.Count, "Xbox gamepad")} ({string.Join(", ", hid.XInputActiveSlots.Select(s => $"Player {s + 1}"))})"
                : "None active right now";
            var hidText = $"{CountLabel(hid.Collections.Count, "interface")} logged";
            var lightingText = hid.LampArrayDevices.Count > 0
                ? $"{CountLabel(hid.LampArrayDevices.Count, "Dynamic Lighting device")} logged"
                : "None detected";

            QuickScanSections.Add(new QuickScanSection(
                "CONTROLLERS & LIGHTING",
                Glyphs.Gamepad,
                BuildRows(
                    ("Built-in controller", controllerText, false, hid.VendorControllers.Count == 0),
                    ("Active gamepads", gamepadText, false, hid.XInputActiveSlots.Count == 0),
                    ("USB / HID devices", hidText, false, hid.Collections.Count == 0),
                    ("Windows lighting", lightingText, false, hid.LampArrayDevices.Count == 0))));
        }

        if (_inventory is { } invLogs)
        {
            var fwText = invLogs.Firmware is { } fw
                ? $"{FormatVendorFamily(fw.VendorFamily)} detected ({CountLabel(fw.Values.Count, "reading")}{(fw.EcMemoryHex is not null ? " + EC snapshot" : "")} logged)"
                : "Generic scan (vendor WMI & ACPI tables logged)";

            var interfacesText =
                $"{CountLabel(invLogs.WmiClasses.Count, "vendor interface")} · " +
                $"{CountLabel(invLogs.AcpiPowerDevices.Count, "power device")} logged";

            var volCount = (invLogs.TimedDelta?.VolatileEcBytes.Count ?? 0) + (invLogs.TimedDelta?.VolatileValues.Count ?? 0);
            var (telemetryText, telemetryMuted) = invLogs.TimedDelta is null
                ? ("Load test skipped", true)
                : volCount > 0
                    ? ($"{CountLabel(volCount, "active sensor reading")} identified", false)
                    : ("No changing sensor values detected", true);

            var (oemText, oemMuted) = FormatOemSummary(invLogs.OemFootprint);

            QuickScanSections.Add(new QuickScanSection(
                "LOGGED FOR SUPPORT",
                Glyphs.Inspect,
                BuildRows(
                    ("Firmware", fwText, false, invLogs.Firmware is null),
                    ("System interfaces", interfacesText, false, false),
                    ("Live telemetry", telemetryText, false, telemetryMuted),
                    ("OEM software", oemText, false, oemMuted)),
                BadgeText: invLogs.Elevated ? "Full access" : "Limited access",
                IsWarningBadge: !invLogs.Elevated));
        }
    }

    private static IReadOnlyList<QuickScanRow> BuildRows(params (string Label, string Value, bool IsWarning, bool IsMuted)[] items)
    {
        var rows = new List<QuickScanRow>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var (label, value, isWarning, isMuted) = items[i];
            rows.Add(new QuickScanRow(label, value, IsFirst: i == 0, IsWarning: isWarning, IsMuted: isMuted));
        }
        return rows;
    }

    private static string FormatDisplaySummary(DisplayInventorySnapshot? display)
    {
        if (display is not { ActiveWidth: > 0, ActiveHeight: > 0 }) return "Unavailable";
        var rates = display.SignalRefreshRates.Distinct().Order().ToList();
        var baseText = $"{display.ActiveWidth} × {display.ActiveHeight} at {display.CurrentRefreshHz} Hz";
        return rates.Count > 1
            ? $"{baseText} (supports {string.Join(" / ", rates)} Hz)"
            : baseText;
    }

    private static string FormatBoostSummary(PowerPlanSnapshot pp, bool boostDisabled)
    {
        var ac = pp.CpuBoostModeAcLabel;
        var dc = pp.CpuBoostModeDcLabel;
        if (string.IsNullOrWhiteSpace(ac) && string.IsNullOrWhiteSpace(dc)) return "Unknown";

        ac ??= "Unknown";
        dc ??= "Unknown";
        var summary = string.Equals(ac, dc, StringComparison.OrdinalIgnoreCase)
            ? $"{ac} (plugged in & battery)"
            : $"{ac} (plugged in) · {dc} (battery)";

        return boostDisabled ? $"{summary} — may cap power limits" : summary;
    }

    private static (string Text, bool IsMuted) FormatPowerSensorsSummary(TimedDeltaSnapshot? td)
    {
        if (td is null) return ("Load test skipped", true);

        var valid = td.EnergyMeters.Where(e => e.LoadWatts.HasValue || e.IdleWatts.HasValue).ToList();
        if (valid.Count == 0)
        {
            if (td.AcOnline == false && td.BatteryRateIdleMw is { } idleMw && td.BatteryRateLoadMw is { } loadMw
                && (idleMw != 0 || loadMw != 0))
            {
                return ($"No Windows energy meter · Battery draw {Math.Abs(idleMw) / 1000.0:F1} W → {Math.Abs(loadMw) / 1000.0:F1} W", false);
            }

            return ("Not exposed by Windows", true);
        }

        var pkg = valid.FirstOrDefault(e => e.InstanceName.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase))
                  ?? valid.FirstOrDefault(e =>
                      e.InstanceName.Contains("Package", StringComparison.OrdinalIgnoreCase)
                      && !e.InstanceName.Contains("DRAM", StringComparison.OrdinalIgnoreCase)
                      && !e.InstanceName.Contains("PP0", StringComparison.OrdinalIgnoreCase)
                      && !e.InstanceName.Contains("PP1", StringComparison.OrdinalIgnoreCase))
                  ?? valid.FirstOrDefault(e =>
                      (e.InstanceName.Contains("Socket", StringComparison.OrdinalIgnoreCase)
                       || e.InstanceName.Contains("APU", StringComparison.OrdinalIgnoreCase)
                       || e.InstanceName.Contains("Total", StringComparison.OrdinalIgnoreCase))
                      && !e.InstanceName.Contains("DRAM", StringComparison.OrdinalIgnoreCase))
                  ?? valid.FirstOrDefault(e => e.LoadWatts.HasValue);

        var countNote = $"{CountLabel(valid.Count, "sensor")} logged";
        if (pkg?.LoadWatts is { } loadW)
        {
            return pkg.IdleWatts is { } idleW
                ? ($"Idle {idleW:F1} W → Load {loadW:F1} W ({countNote})", false)
                : ($"{loadW:F1} W under load ({countNote})", false);
        }

        return ($"{CountLabel(valid.Count, "power sensor")} logged", false);
    }

    private static string FormatVendorController(VendorControllerSnapshot controller)
    {
        var name = controller.Protocol switch
        {
            "msi-hid-v1" => "MSI controller",
            _ => controller.Protocol,
        };
        var fw = CleanSpaces(controller.FirmwareVersion);
        return string.IsNullOrWhiteSpace(fw) ? name : $"{name} (firmware {fw})";
    }

    private static string FormatVendorFamily(string family) => family switch
    {
        "msi-acpi" => "MSI ACPI",
        "asus-atk-wmi" => "ASUS WMI",
        "lenovo-wmi" => "Lenovo WMI",
        _ => family,
    };

    private static (string Text, bool IsMuted) FormatOemSummary(OemFootprintSnapshot oem)
    {
        var parts = new List<string>();
        if (oem.Processes.Count > 0) parts.Add(CountLabel(oem.Processes.Count, "running app"));
        if (oem.Services.Count > 0) parts.Add(CountLabel(oem.Services.Count, "service"));
        if (oem.ScheduledTasks.Count > 0) parts.Add(CountLabel(oem.ScheduledTasks.Count, "scheduled task"));
        if (oem.RegistryValues.Count > 0) parts.Add(CountLabel(oem.RegistryValues.Count, "config setting"));

        return parts.Count > 0
            ? ($"{string.Join(" · ", parts)} logged", false)
            : ("No OEM software detected", true);
    }

    private static string CountLabel(int count, string singular, string? plural = null) =>
        count == 1 ? $"1 {singular}" : $"{count} {plural ?? singular + "s"}";

    private static string CleanSpaces(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var clean = CleanSpaces(value);
            if (!string.IsNullOrWhiteSpace(clean)) return clean;
        }
        return null;
    }

    private static string JoinNonEmpty(string separator, params string[] values) =>
        string.Join(separator, values.Where(v => !string.IsNullOrWhiteSpace(v)));

    private void UpdateSummaryPill()
    {
        SummaryGuidedCount = _guidedDiffs.Values.Count(d => d.Kind is "guided" or "baseline");
        SummaryWatchCount = _watchDiffs.Count;
        SummaryButtonCount = _buttonEvents.Count;
        Notify(nameof(HasSummaryGuided));
        Notify(nameof(HasSummaryWatch));
        Notify(nameof(SummaryWatchLabel));
        Notify(nameof(HasSummaryButtons));
        Notify(nameof(SummaryButtonLabel));
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
        if (diff.PowerChanges.Count > 0) parts.Add("Power " + string.Join(" ", diff.PowerChanges.Select(p => $"{p.Key}={p.After}")));
        if (diff.RegistryChanges.Count > 0)
        {
            var sample = string.Join(", ", diff.RegistryChanges.Take(3).Select(r => $"{r.Key.Split('\\').Last()}={r.After}"));
            var extra = diff.RegistryChanges.Count > 3 ? $" +{diff.RegistryChanges.Count - 3}" : "";
            parts.Add($"Reg({sample}{extra})");
        }
        if (diff.ControllerMetaChanges.Count > 0) parts.Add("Ctrl " + string.Join(" ", diff.ControllerMetaChanges.Select(c => $"{c.Key}={c.After}")));
        if (diff.ControllerProfileChanges.Count > 0) parts.Add($"Profile({diff.ControllerProfileChanges.Count}B)");
        if (diff.HidAdded.Count > 0 || diff.HidRemoved.Count > 0) parts.Add($"HID(+{diff.HidAdded.Count}/-{diff.HidRemoved.Count})");

        return parts.Count > 0 ? string.Join(" · ", parts) : "change detected";
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
            ActionSteps =
            [
                new(1, "Open your handheld's built-in control app",
                    "For example: MSI Center M, Armoury Crate, Legion Space, or Ayaneo Space."),
                new(2, "Follow the default settings below",
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
            ActionSteps =
            [
                new(1, "Switch to the lowest power preset in your OEM app",
                    "Usually labeled `Silent`, `Quiet`, `Endurance`, or `Super Battery`."),
                new(2, "Wait about `2 seconds`",
                    "Gives the firmware a moment to finish switching power profiles."),
                new(3, "Click [[Capture Step]] below"),
            ],
            WhyItMatters = "Shows Tsume how your handheld signals its lowest-power preset, what default wattage limits it uses, and whether it changes the Windows power mode alongside it.",
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
            ActionSteps =
            [
                new(1, "Switch to the highest stock preset in your OEM app",
                    "Usually labeled `Performance`, `Turbo`, `Extreme`, or `Sport`."),
                new(2, "Wait about `2 seconds`",
                    "Gives the firmware time to raise wattage limits and update fan curves."),
                new(3, "Click [[Capture Step]] below"),
            ],
            WhyItMatters = "Shows Tsume the code your handheld uses for its top performance preset, its stock wattage limits, and any fan behavior tied to that mode.",
            NoteHint = "Optional note (e.g., the exact preset name in your OEM app, like \"Extreme Performance\"):",
        },
        new()
        {
            Id = "mode-manual-sentinel",
            Number = 4,
            Category = "POWER MODES",
            Glyph = Glyphs.Sliders,
            Title = "Manual Power Limits (17 / 23 / 29 W)",
            Subtitle = "Set distinct wattages on every power slider so Tsume can pinpoint each limit (2 sliders on Intel, 3 on AMD).",
            ActionSteps =
            [
                new(1, "Switch to `Manual` / `Custom` power mode in your OEM app"),
                new(2, "Set `17 W` / `23 W` (2 sliders) or `17 W` / `23 W` / `29 W` (3 sliders)",
                    "Intel (2 sliders): `PL1` (Sustained) to `17 W`, `PL2` (Boost) to `23 W`.\n" +
                    "AMD (3 sliders — e.g. MSI Claw A8, ROG Ally, Legion Go): `SPL` (Sustained) to `17 W`, `SPPT` (Slow Boost) to `23 W`, `FPPT` (Fast Boost) to `29 W`.\n" +
                    "Only 1 slider or different min/max limits? Pick distinct ascending values and write them in the note box below."),
                new(3, "Wait about `2 seconds`, then click [[Capture Step]] below"),
            ],
            WhyItMatters = "Using unusual, unequal numbers (17 / 23 W on 2-slider Intel chips, or 17 / 23 / 29 W on 3-slider AMD chips) makes it unmistakable which hardware register or setting controls Sustained (PL1 / SPL), Slow Boost (PL2 / SPPT), and Fast Boost (FPPT) power, and how watts are scaled.",
            NoteHint = "Optional note (type the exact wattages you set — especially if you used 1 slider or values other than 17 / 23 W or 17 / 23 / 29 W):",
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
            Title = "Controller Mode: Desktop / Mouse",
            Subtitle = "Switch built-in controls from Gamepad to Desktop mode to see how switching works.",
            IsOptional = true,
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
            Title = "RGB Lighting: Solid Red (100%)",
            Subtitle = "Set stick or button LEDs to pure full-brightness Red as the first color test.",
            IsOptional = true,
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
            Title = "RGB Lighting: Solid Blue (50%)",
            Subtitle = "Switch LEDs to half-brightness pure Blue to separate color from brightness.",
            IsOptional = true,
            ActionSteps =
            [
                new(1, "Keep the lighting effect on `Solid (Static)` in your OEM app"),
                new(2, "Change color to `#0000FF` (Pure Blue) and brightness to `50%`",
                    "No hex input? Set `R: 0`, `G: 0`, `B: 255`, with brightness halfway."),
                new(3, "Click [[Capture Step]] below"),
            ],
            SkipHint = "No RGB lighting on this handheld? Click [[Skip Step]].",
            WhyItMatters = "Comparing 100% Red against 50% Blue separates which bytes control the color from which byte controls brightness, and shows whether brightness scales from 0 to 100 or 0 to 255.",
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

public sealed class ButtonPromptOption(string id, string label, string codeTag) : Observable
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string CodeTag { get; } = codeTag;

    public int CapturedCount
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Notify(nameof(HasCaptured));
                Notify(nameof(CountText));
            }
        }
    }

    public bool HasCaptured => CapturedCount > 0;
    public string CountText => CapturedCount.ToString();

    public override string ToString() => Label;
}
