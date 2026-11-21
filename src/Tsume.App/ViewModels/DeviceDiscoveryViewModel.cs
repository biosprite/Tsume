using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.Core.Diagnostics;
using Tsume.Core.Ipc;

namespace Tsume.App.ViewModels;

public sealed class GuidedStepItem : Observable
{
    public required string Id { get; init; }
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Instruction { get; init; }
    public required string WhyItMatters { get; init; }
    public bool IsButtonCaptureStep { get; init; }
    public bool IsBaselineStep { get; init; }

    public string Status { get; set => Set(ref field, value); } = "Pending";
    public string Detail { get; set => Set(ref field, value); } = "";
    public bool IsCaptured { get; set => Set(ref field, value); }
    public bool IsSkipped { get; set => Set(ref field, value); }
    public bool IsSelected { get; set => Set(ref field, value); }
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
            new("oem_short", "OEM button (short press)"),
            new("oem_hold", "OEM button (press & hold)"),
            new("quick_settings_short", "Quick Settings / Game Bar (short press)"),
            new("quick_settings_hold", "Quick Settings / Game Bar (press & hold)"),
            new("back_left", "Back Left paddle (M1 / L4)"),
            new("back_right", "Back Right paddle (M2 / R4)"),
            new("volume_buttons", "Volume Up / Volume Down"),
            new("other_button", "Other dedicated button"),
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
                return "Not captured yet. Make the change in your OEM app (if applicable), then click Capture Step.";

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
                buttonStep.Status = $"{_buttonEvents.Count} events";
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

    private static List<GuidedStepItem> CreateGuidedSteps() =>
    [
        new()
        {
            Id = "baseline",
            Number = 1,
            Title = "Baseline State",
            IsBaselineStep = true,
            Instruction = "In your OEM software (MSI Center M, Armoury Crate, Legion Space, etc.), set:\n• Balanced / Default performance mode\n• Fan on Auto\n• Battery charge limit at 100% (off)\n• Controller in Gamepad mode\n\nThen click Capture Step.",
            WhyItMatters = "Gives Tsume the clean reference snapshot (and live-jitter mask) that every later step compares against.",
        },
        new()
        {
            Id = "mode-endurance",
            Number = 2,
            Title = "Quiet / Endurance Mode",
            Instruction = "Switch your OEM software to its lowest / Quiet / Super Battery / Silent performance mode, wait 2 seconds, then click Capture Step.",
            WhyItMatters = "Identifies the exact mode register code, default low-power wattage limits, and Windows power overlay.",
        },
        new()
        {
            Id = "mode-performance",
            Number = 3,
            Title = "Performance / Turbo Mode",
            Instruction = "Switch your OEM software to its highest preset (Performance / Sport / Turbo), wait 2 seconds, then click Capture Step.",
            WhyItMatters = "Identifies the Performance mode code, stock preset wattage ceiling, and any fan-table differences.",
        },
        new()
        {
            Id = "mode-manual-sentinel",
            Number = 4,
            Title = "Manual Power (17 W / 23 W)",
            Instruction = "Switch to Manual / Custom power mode and set two distinctive, unequal numbers:\n• Sustained (PL1 / SPL / STAPM): 17 W\n• Boost (PL2 / SPPT / FPPT): 23 W\n(If your device only allows one slider or different bounds, pick two distinctive unequal numbers and type them in the note box.)\n\nWait 2 seconds, then click Capture Step.",
            WhyItMatters = "Asymmetric sentinel numbers (0x11 and 0x17) make the exact sustained vs. boost registers, byte order, and watt scaling unmistakable.",
        },
        new()
        {
            Id = "fan-max",
            Number = 5,
            Title = "Fan Max / Cooler Boost",
            Instruction = "Turn Fan Max / Cooler Boost / Full Speed ON in your OEM app. Wait 3 seconds for fans to spin up, then click Capture Step. (Turn it back to Auto afterwards.)",
            WhyItMatters = "Pinpoints the exact fan-override register/bitmask and captures high-RPM tachometer period readings.",
        },
        new()
        {
            Id = "charge-limit",
            Number = 6,
            Title = "Battery Charge Limit (80%)",
            Instruction = "Set Battery Charge Limit / Care Mode to 80% (or your OEM app's battery saver cap) in your OEM app, wait 2 seconds, then click Capture Step.",
            WhyItMatters = "Reveals the exact charge-limit register, enable bit encoding (e.g. 0x80 | 80 vs raw 80), and any OEM registry mirror key.",
        },
        new()
        {
            Id = "controller-mode",
            Number = 7,
            Title = "Controller Mode (Desktop / Cursor)",
            Instruction = "Switch the built-in controller from Gamepad mode to Desktop / Mouse mode in your OEM app, wait 2 seconds, then click Capture Step. (Switch back to Gamepad afterwards.)",
            WhyItMatters = "Shows whether the controller stays on USB or re-enumerates, how XInput responds, and the exact mode byte.",
        },
        new()
        {
            Id = "rgb-red-100",
            Number = 8,
            Title = "RGB Lighting — Solid Red (100%)",
            Instruction = "In your OEM app, set controller stick/button RGB to solid pure Red (#FF0000) at 100% brightness, then click Capture Step. (Skip if no RGB.)",
            WhyItMatters = "Reveals the exact lighting memory offset, LED count, and RGB vs. GRB byte order.",
        },
        new()
        {
            Id = "rgb-blue-50",
            Number = 9,
            Title = "RGB Lighting — Solid Blue (50%)",
            Instruction = "Change controller RGB to solid pure Blue (#0000FF) at 50% brightness, then click Capture Step. (Skip if no RGB.)",
            WhyItMatters = "Separates the color channel bytes from the brightness byte and range (0–100 vs 0–255).",
        },
        new()
        {
            Id = "back-paddles",
            Number = 10,
            Title = "Back Paddles (M1 → F12, M2 → A)",
            Instruction = "In your OEM app, remap the Left back paddle (M1/L4) to keyboard key F12 and the Right back paddle (M2/R4) to gamepad A (or another distinct key), then click Capture Step. (Skip if no rear buttons.)",
            WhyItMatters = "Pinpoints the exact byte offset, block length, and key-code table for rear button remapping.",
        },
        new()
        {
            Id = "dedicated-buttons",
            Number = 11,
            Title = "Dedicated Hardware Buttons",
            IsButtonCaptureStep = true,
            Instruction = "Pick each button below, click Start Listening, and press that physical button on your handheld.\nTsume passively records WMI events, keyboard chords (Win+G / F15 / etc.), Raw Input HID reports, and XInput simultaneously.",
            WhyItMatters = "Captures the exact WMI code, key chord, or HID report for OEM, Quick Settings, and rear buttons so Tsume can intercept or remap them cleanly.",
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

public sealed record ButtonPromptOption(string Id, string Label)
{
    public override string ToString() => Label;
}
