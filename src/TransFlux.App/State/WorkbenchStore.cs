using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;

namespace TransFlux.App.State;

public sealed record WorkbenchSnapshot(
    IReadOnlyList<WorkbenchItem> Items, IReadOnlyList<Guid> Selected,
    bool Paused, bool Loaded, bool Busy, string? Error, bool CanUndo, int UndoCount)
{
    public static readonly WorkbenchSnapshot Empty = new([], [], true, false, false, null, false, 0);
    public IReadOnlyList<WorkbenchItem> Selection => Items.Where(i => Selected.Contains(i.Id)).ToList();
}

/// <summary>
/// Owns selection, durable visible cards, bounded/cancelable imports and job commands.
/// JobId is the explicit binding: source identity is never used to collapse separate conversions.
/// Notifications can be marshalled by the host; this class has no WinUI dependency.
/// </summary>
public sealed class WorkbenchStore : IDisposable
{
    private readonly IJobService _jobs;
    private readonly IMediaProbe _probe;
    private readonly IWorkspacePersistence _persistence;
    private readonly Action<Action> _post;
    private readonly object _gate = new();
    private readonly List<MediaDraft> _drafts = [];
    private readonly Dictionary<Guid, QueueRow> _rows = [];
    private readonly HashSet<Guid> _selected = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _probes = [];
    private readonly SemaphoreSlim _probeSlots;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<MediaDraft> _undo = [];
    private WorkbenchSnapshot _snapshot = WorkbenchSnapshot.Empty;
    private Action? _changed;
    private System.Threading.Timer? _timer;
    private volatile bool _dirty;
    private bool _loaded, _busy, _disposed;
    private string? _error;
    public Task? WatchTask { get; private set; }

    public WorkbenchStore(IJobService jobs, IMediaProbe probe, IWorkspacePersistence persistence,
        Action<Action> post, int maxConcurrentProbes = 2)
    {
        _jobs = jobs; _probe = probe; _persistence = persistence; _post = post;
        _probeSlots = new SemaphoreSlim(Math.Max(1, maxConcurrentProbes));
    }

    public WorkbenchSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);
    public Action Subscribe(Action changed) { _changed += changed; return () => _changed -= changed; }

    public void Start()
    {
        if (WatchTask is not null) return;
        _timer = new System.Threading.Timer(_ => { if (_dirty) Emit(); }, null, 250, 250);
        WatchTask = RestoreAndWatchAsync();
    }

    private async Task RestoreAndWatchAsync()
    {
        try
        {
            IReadOnlyList<WorkspaceEntry>? restored;
            try { restored = await _persistence.LoadAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { restored = null; SetError("工作台恢复失败：" + ex.Message); }
            var saved = restored ?? [];
            lock (_gate)
                foreach (var e in saved.DistinctBy(e => e.MediaId))
                    _drafts.Add(new MediaDraft { MediaId = e.MediaId, Path = e.SourcePath, JobId = e.JobId,
                        Options = ConversionOptions.FromRequest(e.Request), Status = e.JobId is null ? DraftStatus.Probing : DraftStatus.Ready });
            foreach (var draft in saved.Where(e => e.JobId is null)) Probe(draft.MediaId);
            await foreach (var ev in _jobs.WatchAsync(new JobWatchQuery(), _lifetime.Token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (ev.Kind == JobEventKind.SnapshotCompleted)
                    {
                        // A cleared history record must not silently become a new conversion.
                        _drafts.RemoveAll(d => d.JobId is Guid id && !_rows.ContainsKey(id));
                        _loaded = true;
                    }
                    if (ev.Job is { } job)
                    {
                        _rows.TryGetValue(job.JobId, out var old);
                        // Snapshot hydration can race newer events. Never roll a card back a revision.
                        if (old is null || job.Revision >= old.Job.Revision)
                        {
                            _rows[job.JobId] = new QueueRow(job, old?.Progress, ev.Output ?? old?.Output,
                                ev.Failure ?? old?.Failure, ev.Attempt ?? old?.Attempt);
                            var index = _drafts.FindIndex(d => d.JobId == job.JobId);
                            if (index >= 0) _drafts[index] = _drafts[index] with { Options = ConversionOptions.FromRequest(job.RequestSnapshot) };
                            if (index < 0)
                            {
                                index = job.State == JobState.Queued && ev.Kind is JobEventKind.Enqueued or JobEventKind.Snapshot
                                    ? _drafts.FindIndex(d => d.JobId is null && (d.Submitting || ev.Kind == JobEventKind.Snapshot)
                                        && d.MediaId == job.RequestSnapshot.SourceSnapshotId) : -1;
                                if (index >= 0) _drafts[index] = _drafts[index] with { JobId = job.JobId, Submitting = false };
                                else if (ev.Kind == JobEventKind.Enqueued || ev.Kind == JobEventKind.Snapshot
                                    && (job.State == JobState.Queued || restored is null && (!JobStateMachine.IsTerminal(job.State) || job.State == JobState.Interrupted)))
                                    _drafts.Add(new MediaDraft { MediaId = job.JobId, Path = job.SourcePath ?? job.SourceFingerprint.NormalizedPath,
                                        JobId = job.JobId, Options = ConversionOptions.FromRequest(job.RequestSnapshot), Status = DraftStatus.Ready });
                            }
                        }
                    }
                    if (ev.JobId is Guid rowId && _rows.TryGetValue(rowId, out var row))
                    {
                        if (ev.Attempt is { } incoming && (row.Attempt is null || incoming.Ordinal >= row.Attempt.Ordinal)
                            && incoming.AttemptId != row.Attempt?.AttemptId)
                            row = row with { Progress = null, Attempt = incoming };
                        var progress = ev.Progress;
                        if (progress is not null && row.Job.ActiveAttemptId is Guid active && progress.AttemptId != active) progress = null;
                        if (progress is not null && row.Progress is { } previous
                            && previous.AttemptId == progress.AttemptId && previous.Sequence > progress.Sequence) progress = null;
                        _rows[rowId] = row with { Progress = progress ?? row.Progress, Output = ev.Output ?? row.Output,
                            Failure = ev.Failure ?? row.Failure,
                            Attempt = ev.Attempt is { } a && (row.Attempt is null || a.Ordinal >= row.Attempt.Ordinal) ? a : row.Attempt };
                    }
                    _dirty = true;
                }
                if (ev.Kind != JobEventKind.Progress) Emit();
                if (ev.Kind == JobEventKind.SnapshotCompleted)
                {
                    await PersistAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError("任务状态读取失败，请重新打开应用：" + ex.Message); }
    }

    public async Task<int> ImportAsync(IReadOnlyList<string> paths, ConversionOptions defaults)
    {
        List<MediaDraft> added;
        lock (_gate)
        {
            if (!_loaded) return 0;
            var existing = _drafts.Where(d => d.CanEdit).Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            added = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !existing.Contains(p) && File.Exists(p))
                .Select(p => new MediaDraft { MediaId = Guid.NewGuid(), Path = p, Options = defaults }).ToList();
            if (added.Count == 0) return 0;
            _undo = []; _selected.Clear();
            _drafts.AddRange(added);
            foreach (var d in added) _selected.Add(d.MediaId);
        }
        Emit();
        foreach (var d in added) Probe(d.MediaId);
        await PersistAsync().ConfigureAwait(false);
        return added.Count;
    }

    public void Select(Guid id, bool toggle = false)
    {
        lock (_gate)
        {
            if (!_drafts.Any(d => d.MediaId == id)) return;
            if (!toggle) _selected.Clear();
            if (!toggle || !_selected.Remove(id)) _selected.Add(id);
        }
        Emit();
    }
    public void SelectAll(bool all)
    {
        lock (_gate) { _selected.Clear(); if (all) foreach (var d in _drafts) _selected.Add(d.MediaId); }
        Emit();
    }

    public async Task EditAsync(IReadOnlyList<Guid> ids, Func<ConversionOptions, ConversionOptions> mutate)
    {
        lock (_gate)
        {
            // Mixed selections containing execution records cannot accidentally change a subset.
            if (ids.Count == 0 || ids.Any(id => !_drafts.Any(d => d.MediaId == id && d.CanEdit))) return;
            for (var i = 0; i < _drafts.Count; i++)
                if (ids.Contains(_drafts[i].MediaId)) _drafts[i] = _drafts[i] with { Options = mutate(_drafts[i].Options) };
        }
        Emit(); await PersistAsync().ConfigureAwait(false);
    }

    public async Task StartAsync(IReadOnlyList<Guid> ids)
    {
        List<MediaDraft> ready;
        lock (_gate)
        {
            if (_busy || !_loaded) return;
            ready = BuildItems().Where(i => ids.Contains(i.Id) && i.CanStart).Select(i => i.Draft).ToList();
            if (ready.Count == 0) return;
            _busy = true; _error = null;
            foreach (var d in ready) Replace(d.MediaId, x => x with { Submitting = true });
        }
        Emit();
        var wasPaused = _jobs.GetControl().Paused;
        try
        {
            // Keep newly created jobs queued until their visible card bindings are durable.
            // A crash during enqueue then restores a waiting job, never an invisible interrupted one.
            await _jobs.PauseQueueAsync(_lifetime.Token).ConfigureAwait(false);
            foreach (var d in ready)
            {
                try
                {
                    var jobId = await _jobs.EnqueueAsync(new EnqueueRequest
                        { Source = d.Snapshot!, Request = d.Options.ToRequest(d.MediaId) }, _lifetime.Token).ConfigureAwait(false);
                    lock (_gate) Replace(d.MediaId, x => x with { JobId = jobId, Submitting = false });
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { SetError("任务加入失败：" + ex.Message); }
            }
            // Never override a pause the user (or startup recovery) explicitly requested.
            if (!wasPaused && await PersistAsync().ConfigureAwait(false))
                await _jobs.ResumeQueueAsync(_lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                foreach (var d in ready) Replace(d.MediaId, x => x with { Submitting = false });
                _busy = false;
            }
            Emit();
        }
    }

    public async Task TogglePauseAsync()
    {
        if (_jobs.GetControl().Paused) await _jobs.ResumeQueueAsync(_lifetime.Token).ConfigureAwait(false);
        else await _jobs.PauseQueueAsync(_lifetime.Token).ConfigureAwait(false);
        Emit();
    }
    public async Task CancelAsync(Guid id)
    {
        var item = GetSnapshot().Items.FirstOrDefault(i => i.Id == id);
        if (item?.Draft.JobId is not Guid jobId) return;
        var result = await _jobs.CancelAsync(jobId, _lifetime.Token).ConfigureAwait(false);
        if (!result.Accepted) SetError("任务已进入保存阶段或已经结束，无法取消；请等待状态更新。");
        Emit();
    }

    public async Task EditQueuedAsync(Guid id)
    {
        var item = GetSnapshot().Items.FirstOrDefault(i => i.Id == id);
        if (item?.Execution is not { State: JobState.Queued } row) return;
        if (!await _jobs.RemoveQueuedAsync(row.Job.JobId, _lifetime.Token).ConfigureAwait(false))
        { SetError("任务已经开始，无法移出队列；可取消或复制为新任务。"); return; }
        lock (_gate) Replace(id, d => d with { JobId = null, Status = d.Snapshot is null ? DraftStatus.Probing : DraftStatus.Ready });
        Emit(); ProbeIfNeeded(id); await PersistAsync().ConfigureAwait(false);
    }

    public async Task<Guid?> CopyAsync(Guid id)
    {
        MediaDraft? copy;
        lock (_gate)
        {
            var original = _drafts.FirstOrDefault(d => d.MediaId == id);
            if (original is null || original.Submitting) return null;
            var newId = Guid.NewGuid();
            copy = original with { MediaId = newId, JobId = null, Snapshot = null, Failure = null, ErrorText = null,
                Status = DraftStatus.Probing, Submitting = false };
            _drafts.Add(copy); _selected.Clear(); _selected.Add(newId); _undo = [];
        }
        Emit(); Probe(copy.MediaId); await PersistAsync().ConfigureAwait(false);
        return copy.MediaId;
    }

    public async Task RetryAsync(Guid id)
    {
        var item = GetSnapshot().Items.FirstOrDefault(i => i.Id == id);
        if (item is null) return;
        if (item.Execution is null && item.Draft.Status == DraftStatus.Invalid) { Probe(id); return; }
        if (item.Execution?.CanRetry != true) return;
        lock (_gate)
        {
            if (_busy) return;
            _busy = true; Replace(id, d => d with { Submitting = true });
        }
        Emit();
        var wasPaused = _jobs.GetControl().Paused;
        try
        {
            await _jobs.PauseQueueAsync(_lifetime.Token).ConfigureAwait(false);
            var retry = await _jobs.RetryAsync(item.Execution.Job.JobId, _lifetime.Token).ConfigureAwait(false);
            lock (_gate)
            {
                Replace(id, d => d with { JobId = retry });
                // The retry event may precede this return; preserve the original card identity.
                _drafts.RemoveAll(d => d.MediaId != id && d.JobId == retry);
            }
            // Never override a pause the user (or startup recovery) explicitly requested.
            if (!wasPaused && await PersistAsync().ConfigureAwait(false))
                await _jobs.ResumeQueueAsync(_lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) { _busy = false; Replace(id, d => d with { Submitting = false }); }
            Emit();
        }
    }

    public async Task RemoveAsync(IReadOnlyList<Guid> ids)
    {
        lock (_gate)
        {
            var removable = BuildItems().Where(i => ids.Contains(i.Id) && i.CanRemove).Select(i => i.Draft).ToList();
            if (removable.Count == 0) return;
            _undo = removable;
            foreach (var d in removable)
            {
                if (_probes.TryGetValue(d.MediaId, out var cts)) cts.Cancel();
                _drafts.RemoveAll(x => x.MediaId == d.MediaId); _selected.Remove(d.MediaId);
            }
        }
        Emit(); await PersistAsync().ConfigureAwait(false);
    }
    public async Task UndoAsync()
    {
        IReadOnlyList<MediaDraft> restored;
        lock (_gate)
        {
            restored = _undo; _undo = []; _drafts.AddRange(restored);
            _selected.Clear(); foreach (var d in restored) _selected.Add(d.MediaId);
        }
        Emit(); foreach (var d in restored) ProbeIfNeeded(d.MediaId);
        await PersistAsync().ConfigureAwait(false);
    }
    public void DismissUndo()
    {
        lock (_gate)
        {
            if (_undo.Count == 0) return;
            _undo = [];
        }
        Emit();
    }

    private void ProbeIfNeeded(Guid id)
    {
        if (GetSnapshot().Items.Any(i => i.Id == id && i.CanEdit && i.Draft.IsProbing)) Probe(id);
    }
    private void Probe(Guid id)
    {
        CancellationTokenSource cts;
        string path;
        lock (_gate)
        {
            var draft = _drafts.FirstOrDefault(d => d.MediaId == id && d.CanEdit);
            if (draft is null || _disposed) return;
            if (_probes.TryGetValue(id, out var previous)) previous.Cancel();
            cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _probes[id] = cts; path = draft.Path;
            Replace(id, d => d with { Status = DraftStatus.Probing, Failure = null, ErrorText = null });
        }
        Emit();
        _ = Task.Run(async () =>
        {
            var entered = false;
            try
            {
                await _probeSlots.WaitAsync(cts.Token).ConfigureAwait(false); entered = true;
                var result = await _probe.ProbeAsync(new SourceReference { AbsolutePath = path, MediaId = id }, cts.Token).ConfigureAwait(false);
                lock (_gate)
                    if (!cts.IsCancellationRequested)
                        Replace(id, d => d with { Snapshot = result.Value, Failure = result.Failure,
                            Status = result.IsSuccess && result.Value is not null ? DraftStatus.Ready : DraftStatus.Invalid });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                lock (_gate) if (!cts.IsCancellationRequested)
                    Replace(id, d => d with { Status = DraftStatus.Invalid, Failure = FailureInfo.Of(FailureCode.ProbeFailed, "probe.failed", ex.Message) });
            }
            finally
            {
                if (entered) _probeSlots.Release();
                lock (_gate) if (_probes.GetValueOrDefault(id) == cts) _probes.Remove(id);
                cts.Dispose(); Emit();
            }
        });
    }

    private void Replace(Guid id, Func<MediaDraft, MediaDraft> mutate)
    {
        var index = _drafts.FindIndex(d => d.MediaId == id);
        if (index >= 0) _drafts[index] = mutate(_drafts[index]);
    }
    private List<WorkbenchItem> BuildItems() => _drafts.Select(d => new WorkbenchItem(d,
        d.JobId is Guid id ? _rows.GetValueOrDefault(id) : null)).ToList();
    public void SetError(string? error) { lock (_gate) _error = error; Emit(); }
    private void Emit()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _dirty = false;
            var next = new WorkbenchSnapshot(BuildItems(), _selected.ToArray(), _jobs.GetControl().Paused,
                _loaded, _busy, _error, _undo.Count > 0, _undo.Count);
            Volatile.Write(ref _snapshot, next);
        }
        _post(() => { if (!_disposed) _changed?.Invoke(); });
    }
    public async Task<bool> PersistAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            WorkspaceEntry[] saved;
            lock (_gate) saved = _drafts.Select(d => new WorkspaceEntry(d.MediaId, d.Path, d.Options.ToRequest(d.MediaId), d.JobId)).ToArray();
            await _persistence.SaveAsync(saved, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) { SetError("工作台保存失败：" + ex.Message); return false; }
        finally { _saveGate.Release(); }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _lifetime.Cancel(); }
        _timer?.Dispose();
    }
}
