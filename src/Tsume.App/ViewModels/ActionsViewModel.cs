using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;

namespace Tsume.App.ViewModels;

/// <summary>The grid of quick-action tiles.</summary>
public sealed class ActionsViewModel : Observable
{
    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    public ActionsViewModel(Func<Task> dismiss)
    {
        CycleRefreshRate = new Command(NextRefreshRate);

        // These act on whatever is behind the panel, so the panel leaves first.
        Screenshot  = new Command(async () => { await dismiss(); QuickActions.Screenshot(); });
        Keyboard    = new Command(async () => { await dismiss(); QuickActions.ToggleTouchKeyboard(); });
        TaskManager = new Command(async () => { await dismiss(); QuickActions.TaskManager(); });
        Desktop     = new Command(async () => { await dismiss(); QuickActions.ShowDesktop(); });
    }

    public ICommand CycleRefreshRate { get; }
    public ICommand Screenshot { get; }
    public ICommand Keyboard { get; }
    public ICommand TaskManager { get; }
    public ICommand Desktop { get; }

    public int RefreshRate
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(RefreshRateDetail));
            Notify(nameof(IsHighRefreshRate));
        }
    }

    public string RefreshRateDetail => RefreshRate > 0 ? Strings.RefreshRateValue(RefreshRate) : "";

    public bool IsHighRefreshRate => RefreshRate > 60;

    public void Refresh() => RefreshRate = DisplayMode.CurrentRefreshRate();

    /// <summary>Steps to the next rate the display offers, wrapping back to the lowest.</summary>
    private void NextRefreshRate()
    {
        var rates = DisplayMode.AvailableRefreshRates();
        if (rates.Count < 2) return;

        var next = rates.FirstOrDefault(rate => rate > RefreshRate, rates[0]);
        if (DisplayMode.SetRefreshRate(next)) RefreshRate = next;
    }
}
