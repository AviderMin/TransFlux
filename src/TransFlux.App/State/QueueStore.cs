using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;

namespace TransFlux.App.State;

/// <summary>One row in the queue/history projection.</summary>
public sealed record QueueRow(
    Job Job,
    ProgressSnapshot? Progress,
    OutputRecord? Output,
    FailureInfo? Failure,
    Attempt? Attempt)
{
    public JobState State => Job.State;
    public bool IsTerminal => JobStateMachine.IsTerminal(Job.State);
    public bool IsActive => JobStateMachine.IsActive(Job.State);
    public bool CanCancel => JobStateMachine.AllowsCancel(Job.State);
    public bool CanRetry => Job.State is JobState.Failed or JobState.Canceled or JobState.Interrupted or JobState.NeedsAttention;
    public bool CanRemove => Job.State is JobState.Queued;
}

/// <summary>Immutable UI snapshot of the scheduler projection.</summary>
public sealed record QueueSnapshot(
    IReadOnlyList<QueueRow> Rows,
    bool Paused,
    int RunningCount,
    int QueuedCount,
    string? Error)
{
    public static readonly QueueSnapshot Empty = new(Array.Empty<QueueRow>(), false, 0, 0, null);
}

/// <summary>
/// Bridges <see cref="IJobService.WatchAsync"/> into a pull-able snapshot for
/// <c>UseExternalStore</c>. Applies out-of-order/expired progress defensively,
/// coalesces progress notifications to &lt;= 4 Hz, but never drops terminal or
/// state-change events. All notifications are marshalled onto the UI dispatcher.
/// </summary>
public sealed class JobQueueStore : IDisposable
{
    private readonly IJobService _jobs;
    private readonly DispatcherQueue? _dispatcher;
    private readonly object _gate = new();

    private readonly Dictionary<Guid, Job> _jobsById = new();
    private readonly Dictionary<Guid, ProgressSnapshot> _progress = new();
    private readonly Dictionary<Guid, OutputRecord> _outputs = new();
    private readonly Dictionary<Guid, FailureInfo> _failures = new();
    private readonly Dictionary<Guid, Attempt> _attempts = new();

    private QueueSnapshot _snapshot = QueueSnapshot.Empty;
    private Action? _changed;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private System.Threading.Timer? _timer;
    private volatile bool _dirty;
    private volatile bool _paused;
    private volatile int _running;
    private volatile int _queued;
    private string? _error;
    private int _emitting;
    private bool _started;

    public JobQueueStore(IJobService jobs, DispatcherQueue? dispatcher)
    {
        _jobs = jobs;
        _dispatcher = dispatcher;
    }

    public QueueSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    public Action Subscribe(Action onChange)
    {
        _changed += onChange;
        return () => _changed -= onChange;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _cts = new CancellationTokenSource();
        _timer = new System.Threading.Timer(_ => { if (_dirty) Emit(); }, null, 250, 250);
        _loop = Task.Run(() => RunAsync(_cts.Token));
        RefreshControl();
    }

    public void RefreshControl()
    {
        try
        {
            var control = _jobs.GetControl();
            _paused = control.Paused;
            _running = control.RunningCount;
            _queued = control.QueuedCount;
            Emit();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Emit();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var query = new JobWatchQuery { IncludeCompleted = true };
            await foreach (var ev in _jobs.WatchAsync(query, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                Apply(ev);
                if (ev.Kind is JobEventKind.StateChanged or JobEventKind.Failed
                    or JobEventKind.OutputCommitted or JobEventKind.Removed
                    or JobEventKind.Enqueued or JobEventKind.AttemptChanged)
                {
                    Emit();
                }
                // Progress ticks are emitted by the coalescing timer (<= 4 Hz).
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { SetError(ex.Message); }
    }

    private void Apply(JobEvent ev)
    {
        lock (_gate)
        {
            if (ev.Job is { } job) _jobsById[job.JobId] = job;
            if (ev.Attempt is { } attempt) _attempts[attempt.JobId] = attempt;

            if (ev.Progress is { } progress)
            {
                if (!_progress.TryGetValue(progress.JobId, out var previous)
                    || previous.AttemptId != progress.AttemptId
                    || progress.Sequence >= previous.Sequence)
                {
                    _progress[progress.JobId] = progress;
                }
            }

            if (ev.Output is { } output) _outputs[output.JobId] = output;
            if (ev.Failure is { } failure && ev.JobId is Guid failedId) _failures[failedId] = failure;

            if (ev.Kind == JobEventKind.Removed && ev.JobId is Guid removedId)
            {
                _jobsById.Remove(removedId);
                _progress.Remove(removedId);
                _outputs.Remove(removedId);
                _failures.Remove(removedId);
                _attempts.Remove(removedId);
            }

            _dirty = true;
        }
    }

    private void Emit()
    {
        if (Interlocked.Exchange(ref _emitting, 1) == 1) return;
        try
        {
            void EmitCore()
            {
                QueueSnapshot snapshot;
                lock (_gate)
                {
                    _dirty = false;
                    var rows = _jobsById.Values
                        .OrderBy(j => j.EnqueueOrder)
                        .ThenBy(j => j.CreatedUtc)
                        .Select(j => new QueueRow(
                            j,
                            _progress.GetValueOrDefault(j.JobId),
                            _outputs.GetValueOrDefault(j.JobId),
                            _failures.GetValueOrDefault(j.JobId),
                            _attempts.GetValueOrDefault(j.JobId)))
                        .ToList();

                    var running = _running > 0 ? _running : rows.Count(r => r.IsActive);
                    var queued = _queued > 0 ? _queued : rows.Count(r => r.State == JobState.Queued);
                    snapshot = new QueueSnapshot(rows, _paused, running, queued, _error);
                }
                Volatile.Write(ref _snapshot, snapshot);
                _changed?.Invoke();
            }

            if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
                _dispatcher.TryEnqueue(EmitCore);
            else
                EmitCore();
        }
        finally
        {
            Interlocked.Exchange(ref _emitting, 0);
        }
    }

    private void SetError(string message)
    {
        _error = message;
        Emit();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _timer?.Dispose();
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        _timer = null;
    }
}
