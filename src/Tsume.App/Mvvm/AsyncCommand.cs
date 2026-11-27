using System.Windows.Input;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Mvvm;

/// <summary>An asynchronous button press with centralized error logging and optional re-entrancy protection.</summary>
public sealed class AsyncCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    bool allowReentrancy = false) : ICommand
{
    private static readonly Journal Errors = new("errors");

    private bool _running;

    public bool IsRunning => _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        if (!allowReentrancy && _running) return;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        _running = true;
        try
        {
            await execute();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected.
        }
        catch (Exception error)
        {
            LogError(error);
        }
        finally
        {
            _running = false;
        }
    }

    internal static void LogError(Exception error) => Errors.Write(error.ToString());
}
