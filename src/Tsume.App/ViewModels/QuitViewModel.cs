using System.Windows.Input;
using Tsume.App.Mvvm;

namespace Tsume.App.ViewModels;

/// <summary>The "Quit Tsume?" sheet: quitting takes a second, deliberate press.</summary>
public sealed class QuitViewModel : LayerViewModel
{
    /// <param name="quit">Exits Tsume.</param>
    public QuitViewModel(Action quit) => Confirm = new Command(quit);

    public ICommand Confirm { get; }
}
