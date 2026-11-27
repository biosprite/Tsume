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

    private int _activeCount;

    public bool IsRunning => Volatile.Read(ref _activeCount) > 0;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        if (allowReentrancy)
        {
            Interlocked.Increment(ref _activeCount);
        }
        else if (Interlocked.CompareExchange(ref _activeCount, 1, 0) != 0)
        {
            return;
        }

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
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
            Interlocked.Decrement(ref _activeCount);
        }
    }

    internal static void LogError(Exception error) => Errors.Write(error.ToString());
}
