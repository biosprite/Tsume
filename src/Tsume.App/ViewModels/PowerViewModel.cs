using System.Windows.Input;
using Tsume.App.Mvvm;
using Tsume.App.Platform;

namespace Tsume.App.ViewModels;

/// <summary>The power sheet: hibernate, lock, restart, shut down.</summary>
public sealed class PowerViewModel : LayerViewModel
{
    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    /// <param name="beforePowerAction">Optional async hook (e.g. restoring captured gamepad mode) run before leaving the session.</param>
    public PowerViewModel(Func<Task> dismiss, Func<Task>? beforePowerAction = null)
    {
        async Task RunAfterDismiss(Action action)
        {
            await dismiss();
            if (beforePowerAction is not null) await beforePowerAction();
            action();
        }

        Hibernate = new Command(() => _ = RunAfterDismiss(PowerActions.Hibernate));
        Lock      = new Command(() => _ = RunAfterDismiss(PowerActions.Lock));
        Restart   = new Command(() => _ = RunAfterDismiss(PowerActions.Restart));
        ShutDown  = new Command(() => _ = RunAfterDismiss(PowerActions.ShutDown));
    }

    public ICommand Hibernate { get; }
    public ICommand Lock { get; }
    public ICommand Restart { get; }
    public ICommand ShutDown { get; }
}
