using System.Windows.Input;

namespace Tsume.App.Mvvm;

/// <summary>A button press, as the UI sees it.</summary>
public sealed class Command(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
