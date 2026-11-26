namespace Tsume.App.Input;

/// <summary>
/// Tracks when a text input control inside the overlay is actively taking keyboard input, so
/// low-level controller isolation and dedicated-action keyboard hooks pass keys through untouched.
/// </summary>
public static class TextInputMode
{
    private static int _depth;

    public static bool IsActive => Volatile.Read(ref _depth) > 0;

    public static IDisposable Enter()
    {
        Interlocked.Increment(ref _depth);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _depth);
        }
    }
}
