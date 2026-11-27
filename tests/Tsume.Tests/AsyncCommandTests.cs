using Tsume.App.Mvvm;

namespace Tsume.Tests;

public sealed class AsyncCommandTests
{
    [Fact]
    public async Task Execute_CatchesExceptionsWithoutCrashingAndResetsRunningState()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var command = new AsyncCommand(async () =>
        {
            started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("Simulated failure");
        });

        command.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(command.IsRunning);

        release.SetResult();
        await WaitUntilAsync(() => !command.IsRunning);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task Execute_SwallowsOperationCanceledExceptionAndResetsRunningState()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var command = new AsyncCommand(async () =>
        {
            started.TrySetResult();
            await release.Task;
            throw new OperationCanceledException();
        });

        command.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(command.IsRunning);

        release.SetResult();
        await WaitUntilAsync(() => !command.IsRunning);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task Execute_BlocksReentrancyByDefault()
    {
        var invocations = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var command = new AsyncCommand(async () =>
        {
            Interlocked.Increment(ref invocations);
            started.TrySetResult();
            await release.Task;
        }, allowReentrancy: false);

        command.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second call while still running must be ignored.
        command.Execute(null);
        Assert.Equal(1, Volatile.Read(ref invocations));

        release.SetResult();
        await WaitUntilAsync(() => !command.IsRunning);

        // Once finished, executing again is allowed.
        command.Execute(null);
        await WaitUntilAsync(() => Volatile.Read(ref invocations) == 2 && !command.IsRunning);
        Assert.Equal(2, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task Execute_AllowsConcurrentInvocationsWhenReentrancyEnabled()
    {
        var invocations = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var command = new AsyncCommand(async () =>
        {
            if (Interlocked.Increment(ref invocations) == 2)
                bothStarted.TrySetResult();

            await release.Task;
        }, allowReentrancy: true);

        command.Execute(null);
        command.Execute(null);

        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref invocations));

        release.SetResult();
        await WaitUntilAsync(() => !command.IsRunning);
    }

    [Fact]
    public void CanExecute_RespectsPredicateAndRaisesCanExecuteChanged()
    {
        var allowed = false;
        var executed = false;
        var changedCount = 0;

        var command = new AsyncCommand(
            () =>
            {
                executed = true;
                return Task.CompletedTask;
            },
            canExecute: () => allowed);

        command.CanExecuteChanged += (_, _) => changedCount++;

        Assert.False(command.CanExecute(null));
        command.Execute(null);
        Assert.False(executed);

        allowed = true;
        command.RaiseCanExecuteChanged();
        Assert.Equal(1, changedCount);
        Assert.True(command.CanExecute(null));

        command.Execute(null);
        Assert.True(executed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within timeout.");

            await Task.Delay(10);
        }
    }
}
