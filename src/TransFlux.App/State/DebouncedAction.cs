namespace TransFlux.App.State;

/// <summary>
/// Coalesces rapid triggers (for example per-keystroke settings edits) into one delayed
/// save. FlushAsync awaits the pending run so closing the window never drops the last
/// change (UI review fix round).
/// </summary>
public sealed class DebouncedAction : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task _pending = Task.CompletedTask;

    public DebouncedAction(TimeSpan delay) => _delay = delay;

    public void Schedule(Func<Task> action)
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _pending = RunAsync(action, _cts.Token);
        }
    }

    private async Task RunAsync(Func<Task> action, CancellationToken token)
    {
        try { await Task.Delay(_delay, token).ConfigureAwait(false); await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { /* Best-effort debounce: the scheduled action reports its own failures. */ }
    }

    public async Task FlushAsync()
    {
        Task pending;
        lock (_gate) pending = _pending;
        await pending.ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
