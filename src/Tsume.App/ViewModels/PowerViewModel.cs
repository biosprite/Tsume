using System.Windows.Input;
using Tsume.App.Mvvm;
using Tsume.App.Platform;

namespace Tsume.App.ViewModels;

/// <summary>The power sheet: hibernate, lock, restart, shut down.</summary>
public sealed class PowerViewModel : LayerViewModel
{
    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    public PowerViewModel(Func<Task> dismiss)
    {
        Hibernate = new Command(async () => { await dismiss(); PowerActions.Hibernate(); });
        Lock     = new Command(async () => { await dismiss(); PowerActions.Lock(); });
        Restart  = new Command(async () => { await dismiss(); PowerActions.Restart(); });
        ShutDown = new Command(async () => { await dismiss(); PowerActions.ShutDown(); });
    }

    public ICommand Hibernate { get; }
    public ICommand Lock { get; }
    public ICommand Restart { get; }
    public ICommand ShutDown { get; }
}
