namespace Tsume.App.Platform;

/// <summary>
/// Runs slow work off the UI thread for the newest value only: values posted
/// while one is being applied replace each other (e.g. a dragged slider).
/// </summary>
public sealed class LatestOnly<T>(Action<T> work)
{
    private readonly Lock _gate = new();
    private bool _busy;
    private bool _hasPending;
    private T _pending = default!;

    public void Post(T value)
    {
        lock (_gate)
        {
            _pending = value;
            _hasPending = true;

            if (_busy) return;
            _busy = true;
        }

        Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            T value;

            lock (_gate)
            {
                if (!_hasPending)
                {
                    _busy = false;
                    return;
                }

                value = _pending;
                _hasPending = false;
            }

            try { work(value); }
            catch (Exception) { /* A failed hardware call must not kill the worker. */ }
        }
    }
}
