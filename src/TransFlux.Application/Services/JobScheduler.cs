using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TransFlux.Application.Contracts;
using TransFlux.Application.Scheduling;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Application.Services;

/// <summary>
/// Application-level job orchestrator. A single dispatch loop claims queued jobs (conditional
/// store update), runs each attempt through the injected engine ports, publishes coalesced
/// events, and enforces one active attempt per job. Behaviour follows docs/04 and docs/07:
/// bounded concurrency, FIFO+aging, cancel refused at the commit boundary, one automatic
/// capability fallback, queue pause/resume (running work continues), and RemoveQueuedAsync.
/// </summary>
public sealed class JobScheduler : IJobService, IAsyncDisposable
{
    private static readonly JobState[] NonTerminalStates =
    {
        JobState.Queued, JobState.Preflighting, JobState.Running, JobState.Verifying,
        JobState.Committing, JobState.Recovering, JobState.CancelRequested, JobState.NeedsAttention,
    };

    private static readonly IReadOnlyCollection<JobState> NonTerminalStatesReadOnly = NonTerminalStates;

    private readonly IJobStore _store;
    private readonly IExecutionPlanner _planner;
    private readonly IMediaProbe _probe;
    private readonly ICapabilityService _caps;
    private readonly IMediaRunner _runner;
    private readonly IOutputVerifier _verifier;
    private readonly IOutputPathService _outputPaths;
    private readonly IOutputCommitter _committer;
    private readonly IFileSystemService _fs;
    private readonly IClock _clock;
    private readonly SchedulerOptions _options;

    private sealed record ExecutionPolicy(int MaxConcurrent, bool AllowFallback, bool FullVerification);
    private ExecutionPolicy _policy;
    private readonly JobEventHub _hub = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCts = new();
    private readonly ConcurrentDictionary<Guid, Guid> _activeAttempts = new();
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly ConcurrentDictionary<Guid, Task> _workers = new();
    private readonly Channel<bool> _wake;

    private readonly TimeSpan _progressThrottle;
    private CancellationTokenSource? _loopCts;
    private Task? _dispatchTask;
    private int _paused;          // 0 = running, 1 = paused
    private int _activeWorkers;
    private int _queuedCount;
    private int _disposed;

    /// <summary>Optional concrete executable paths surfaced to <see cref="AttemptContext"/>.
    /// The injected runner (constructed with FfmpegCorePaths) normally owns resolution; set these
    /// if the host wants them recorded on each attempt. Empty when unset.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>See <see cref="FfmpegPath"/>.</summary>
    public string? FfprobePath { get; set; }

    public JobScheduler(
        IJobStore store,
        IExecutionPlanner planner,
        IMediaProbe probe,
        ICapabilityService caps,
        IMediaRunner runner,
        IOutputVerifier verifier,
        IOutputPathService outputPaths,
        IOutputCommitter committer,
        IFileSystemService fs,
        IClock clock,
        SchedulerOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _caps = caps ?? throw new ArgumentNullException(nameof(caps));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _outputPaths = outputPaths ?? throw new ArgumentNullException(nameof(outputPaths));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        var maxConcurrent = Math.Max(1, _options.MaxConcurrentTranscodes);
        _policy = new ExecutionPolicy(maxConcurrent, options.AllowAutomaticFallback, options.FullVerification);
        _progressThrottle = _options.ProgressThrottle <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(500)
            : _options.ProgressThrottle;
        _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    // =====================================================================================
    // Lifecycle
    // =====================================================================================

    /// <summary>Starts the background dispatch loop. Idempotent.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_hub)
        {
            if (_dispatchTask is not null)
            {
                return;
            }
            _loopCts = new CancellationTokenSource();
            _dispatchTask = Task.Run(() => DispatchLoopAsync(_loopCts.Token));
        }
    }

    private void Wake() => _wake.Writer.TryWrite(true);

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Volatile.Read(ref _paused) != 0)
                {
                    consecutiveFailures = 0;
                    await WaitForWakeAsync(ct).ConfigureAwait(false);
                    continue;
                }

                await RecountAsync(ct).ConfigureAwait(false);

                if (_inFlight.Count >= Volatile.Read(ref _policy).MaxConcurrent)
                {
                    await WaitForWakeAsync(ct).ConfigureAwait(false);
                    continue;
                }

                var queued = await _store.GetJobsAsync(new[] { JobState.Queued }, ct).ConfigureAwait(false);
                var candidate = QueueOrdering
                    .Order(queued.Where(j => !_inFlight.ContainsKey(j.JobId)), _clock.UtcNow, QueueOrdering.DefaultAgingQuantum)
                    .FirstOrDefault();

                if (candidate is null)
                {
                    consecutiveFailures = 0;
                    await WaitForWakeAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (!_inFlight.TryAdd(candidate.JobId, 1))
                {
                    continue;
                }

                if (Volatile.Read(ref _paused) != 0)
                {
                    _inFlight.TryRemove(candidate.JobId, out _);
                    continue;
                }

                // Re-read before dispatch (may have been removed/canceled/claimed elsewhere).
                Job? fresh;
                try
                {
                    fresh = await _store.GetJobAsync(candidate.JobId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _inFlight.TryRemove(candidate.JobId, out _);
                    throw;
                }

                if (fresh is null || fresh.State != JobState.Queued)
                {
                    _inFlight.TryRemove(candidate.JobId, out _);
                    continue;
                }

                if (Volatile.Read(ref _paused) != 0 || _inFlight.Count > Volatile.Read(ref _policy).MaxConcurrent)
                {
                    _inFlight.TryRemove(candidate.JobId, out _);
                    continue;
                }

                consecutiveFailures = 0;
                SpawnWorker(candidate.JobId, ct);
            }
            catch (OperationCanceledException)
            {
                break; // Normal shutdown.
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var delayMs = Math.Min(2_000, 250 * consecutiveFailures);
                System.Diagnostics.Trace.TraceError(
                    $"TransFlux dispatch iteration failed; retrying in {delayMs} ms. {ex}");
                try
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void SpawnWorker(Guid jobId, CancellationToken stopToken)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await ProcessJobAsync(jobId, stopToken).ConfigureAwait(false);
            }
            catch
            {
                // ProcessJobAsync handles its own failures; never let a worker fault escape.
            }
            finally
            {
                _inFlight.TryRemove(jobId, out _);
                _workers.TryRemove(jobId, out _);
                Wake();
            }
        }, CancellationToken.None);
        _workers[jobId] = task;
    }

    private async Task WaitForWakeAsync(CancellationToken ct)
    {
        try
        {
            await _wake.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Shutdown.
        }
    }

    private async Task RecountAsync(CancellationToken ct)
    {
        try
        {
            var queued = await _store.GetJobsAsync(new[] { JobState.Queued }, ct).ConfigureAwait(false);
            Volatile.Write(ref _queuedCount, queued.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Counting is best-effort; never fail dispatch because of it.
        }
    }

    // =====================================================================================
    // IJobService
    // =====================================================================================

    public async Task<Guid> EnqueueAsync(EnqueueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _clock.UtcNow;
        var order = await _store.NextEnqueueOrderAsync(ct).ConfigureAwait(false);
        var job = new Job
        {
            JobId = Guid.NewGuid(),
            RequestSnapshot = request.Request,
            SourceFingerprint = request.Source.Fingerprint,
            EnqueueOrder = order,
            State = JobState.Queued,
            Revision = 0,
            CreatedUtc = now,
            UpdatedUtc = now,
            RetryOfJobId = request.RetryOfJobId,
            RequestHash = CanonicalJson.Hash(request.Request),
            SourcePath = request.Source.AbsolutePath,
        };

        await _store.CreateJobAsync(job, ct).ConfigureAwait(false);
        Publish(new JobEvent { Kind = JobEventKind.Enqueued, JobId = job.JobId, Job = job });
        await RecountAsync(CancellationToken.None).ConfigureAwait(false);
        Wake();
        return job.JobId;
    }

    public async Task<CancelResult> CancelAsync(Guid id, CancellationToken ct)
    {
        var job = await _store.GetJobAsync(id, ct).ConfigureAwait(false);
        if (job is null)
        {
            return CancelResult.Refused("Job not found.");
        }

        if (JobStateMachine.IsTerminal(job.State))
        {
            return CancelResult.Refused("Job already finished.");
        }

        if (job.State == JobState.CancelRequested)
        {
            return CancelResult.Ok("Cancellation already requested.");
        }

        if (job.State == JobState.Queued)
        {
            if (await TryTransitionAsync(id, JobState.Canceled, CancellationToken.None).ConfigureAwait(false))
            {
                await RecountAsync(CancellationToken.None).ConfigureAwait(false);
                Wake();
                return CancelResult.Ok("Queued job canceled.");
            }
            // Lost the race to a worker that just claimed it; fall through to cancellation request.
            job = await _store.GetJobAsync(id, ct).ConfigureAwait(false);
            if (job is null || JobStateMachine.IsTerminal(job.State))
            {
                return CancelResult.Refused("Job already finished.");
            }
        }

        // Commit boundary: cancellation is refused once the job is committing (atomic output step).
        if (!JobStateMachine.AllowsCancel(job.State))
        {
            return CancelResult.Refused("Job is saving output and can no longer be canceled.");
        }

        if (!await TryTransitionAsync(id, JobState.CancelRequested, CancellationToken.None).ConfigureAwait(false))
        {
            var latest = await _store.GetJobAsync(id, ct).ConfigureAwait(false);
            if (latest is not null && !JobStateMachine.AllowsCancel(latest.State))
            {
                return CancelResult.Refused("Job is saving output and can no longer be canceled.");
            }
            return CancelResult.Refused("Cancel could not be applied.");
        }

        if (_jobCts.TryGetValue(id, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        Wake();
        return CancelResult.Ok("Cancellation requested.");
    }

    public async Task<Guid> RetryAsync(Guid id, CancellationToken ct)
    {
        var original = await _store.GetJobAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {id} not found.");
        if (!JobStateMachine.IsTerminal(original.State) && original.State != JobState.NeedsAttention)
        {
            throw new InvalidOperationException($"Job {id} is not retryable in state {original.State}.");
        }

        if (original.State == JobState.NeedsAttention)
        {
            throw new InvalidOperationException(
                $"Job {id} requires manual output verification and cannot be retried directly.");
        }

        var now = _clock.UtcNow;
        var order = await _store.NextEnqueueOrderAsync(ct).ConfigureAwait(false);
        var retry = new Job
        {
            JobId = Guid.NewGuid(),
            RequestSnapshot = original.RequestSnapshot,
            SourceFingerprint = original.SourceFingerprint,
            EnqueueOrder = order,
            State = JobState.Queued,
            Revision = 0,
            CreatedUtc = now,
            UpdatedUtc = now,
            RetryOfJobId = original.JobId,
            RequestHash = original.RequestHash,
            SourcePath = original.SourcePath,
        };

        await _store.CreateJobAsync(retry, ct).ConfigureAwait(false);
        Publish(new JobEvent { Kind = JobEventKind.Enqueued, JobId = retry.JobId, Job = retry });
        await RecountAsync(CancellationToken.None).ConfigureAwait(false);
        Wake();
        return retry.JobId;
    }

    public async IAsyncEnumerable<JobEvent> WatchAsync(
        JobWatchQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var handle = _hub.Subscribe();
        try
        {
            if (query.AfterSequence is null)
            {
                IReadOnlyCollection<JobState>? states = query.IncludeCompleted ? null : NonTerminalStatesReadOnly;
                IReadOnlyList<Job> snapshot;
                try
                {
                    snapshot = await _store.GetJobsAsync(states, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                foreach (var job in snapshot.OrderBy(j => j.EnqueueOrder))
                {
                    if (query.JobId is Guid only && job.JobId != only)
                    {
                        continue;
                    }
                    var attempts = await _store.GetAttemptsForJobAsync(job.JobId, ct).ConfigureAwait(false);
                    var outputs = await _store.GetOutputsForJobAsync(job.JobId, ct).ConfigureAwait(false);
                    var latestAttempt = attempts.OrderByDescending(a => a.Ordinal).FirstOrDefault();
                    yield return new JobEvent
                    {
                        Kind = JobEventKind.Snapshot,
                        Sequence = handle.StartSequence,
                        JobId = job.JobId,
                        Job = job,
                        Attempt = latestAttempt,
                        Output = outputs.OrderByDescending(o => o.CommittedUtc).FirstOrDefault(),
                        Failure = latestAttempt?.Failure,
                    };
                }
                yield return new JobEvent { Kind = JobEventKind.SnapshotCompleted, Sequence = handle.StartSequence };
            }

            await foreach (var evt in handle.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (evt.Sequence <= (query.AfterSequence ?? handle.StartSequence))
                {
                    continue;
                }
                if (!Matches(query, evt))
                {
                    continue;
                }
                yield return evt;
            }
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static bool Matches(JobWatchQuery query, JobEvent evt)
    {
        if (query.JobId is Guid only && evt.JobId is Guid jobId && jobId != only)
        {
            return false;
        }
        if (!query.IncludeCompleted && evt.Job is Job job && JobStateMachine.IsTerminal(job.State))
        {
            return false;
        }
        return true;
    }

    public Task PauseQueueAsync(CancellationToken ct)
    {
        Volatile.Write(ref _paused, 1);
        return Task.CompletedTask;
    }

    public void ConfigureExecution(int maxConcurrentTranscodes, bool allowAutomaticFallback, bool fullVerification)
    {
        var limit = Math.Clamp(maxConcurrentTranscodes, 1, 4);
        Volatile.Write(ref _policy, new ExecutionPolicy(limit, allowAutomaticFallback, fullVerification));
        _options.MaxConcurrentTranscodes = limit;
        _options.AllowAutomaticFallback = allowAutomaticFallback;
        _options.FullVerification = fullVerification;
        Wake();
    }

    public Task ResumeQueueAsync(CancellationToken ct)
    {
        Volatile.Write(ref _paused, 0);
        Wake();
        return Task.CompletedTask;
    }

    public QueueControlSnapshot GetControl() => new()
    {
        Paused = Volatile.Read(ref _paused) != 0,
        RunningCount = Volatile.Read(ref _activeWorkers),
        QueuedCount = Volatile.Read(ref _queuedCount),
    };

    public async Task<bool> MoveQueuedAsync(Guid id, int direction, CancellationToken ct)
    {
        if (direction is not (-1 or 1) || _inFlight.ContainsKey(id)) return false;
        var queued = await _store.GetJobsAsync(new[] { JobState.Queued }, ct).ConfigureAwait(false);
        var ordered = queued.OrderBy(j => j.EnqueueOrder).ThenBy(j => j.JobId).ToList();
        var index = ordered.FindIndex(j => j.JobId == id);
        if (index < 0) return false;
        var targetIndex = Math.Clamp(index + direction, 0, ordered.Count - 1);
        if (targetIndex == index) return false;

        var moved = await _store.ReorderQueuedAsync(id, ordered[targetIndex].JobId,
            placeBefore: direction < 0, ct).ConfigureAwait(false);
        if (!moved) return false;

        foreach (var job in await _store.GetJobsAsync(new[] { JobState.Queued }, ct).ConfigureAwait(false))
            Publish(new JobEvent { Kind = JobEventKind.StateChanged, JobId = job.JobId, Job = job });
        Wake();
        return true;
    }

    public async Task<bool> RemoveQueuedAsync(Guid id, CancellationToken ct)
    {
        var job = await _store.GetJobAsync(id, ct).ConfigureAwait(false);
        if (job is null || job.State != JobState.Queued)
        {
            return false;
        }

        if (!await TryTransitionAsync(id, JobState.Canceled, CancellationToken.None).ConfigureAwait(false))
        {
            return false;
        }

        Publish(new JobEvent { Kind = JobEventKind.Removed, JobId = id });
        await RecountAsync(CancellationToken.None).ConfigureAwait(false);
        Wake();
        return true;
    }

    /// <summary>
    /// Abandons a NeedsAttention job: deletes the commit journal left by an unresolved/failed
    /// commit, releases the output naming reservation, removes the attempt work directory, and
    /// marks the job Canceled so the visible card can be dropped without orphaning disk state.
    /// Only NeedsAttention is eligible; active jobs and terminal successes are refused, and the
    /// user's final output file is never deleted.
    /// </summary>
    public async Task<bool> AbandonAsync(Guid id, CancellationToken ct)
    {
        var job = await _store.GetJobAsync(id, ct).ConfigureAwait(false);
        if (job is null || job.State != JobState.NeedsAttention)
        {
            return false;
        }

        var journals = (await _store.GetOpenJournalsAsync(ct).ConfigureAwait(false))
            .Where(j => j.JobId == id)
            .OrderByDescending(j => j.UpdatedUtc)
            .ToList();
        var attempts = await _store.GetAttemptsForJobAsync(id, ct).ConfigureAwait(false);

        // Claim the job with the terminal CAS first. If another actor changed its state we do not
        // destroy any staging evidence; cleanup below only runs for the state we actually own.
        if (!await TryTransitionAsync(id, JobState.Canceled, ct).ConfigureAwait(false))
        {
            return false;
        }
        Publish(new JobEvent { Kind = JobEventKind.Removed, JobId = id });

        CleanupAbandonedAttempt(journals.Count > 0 ? journals[0] : null, attempts);

        // A Failed/Published row left behind by a delete failure is harmless: recovery closes it
        // on the next start (the job is terminal, so it will not be re-opened as Succeeded).
        foreach (var journal in journals)
        {
            try { await _store.DeleteJournalAsync(journal.CommitToken, ct).ConfigureAwait(false); }
            catch { /* best effort; the journal is idempotently cleaned by recovery */ }
        }

        return true;
    }

    /// <summary>Releases the reservation lock and deletes the attempt work directory reconstructed
    /// from the journal or the latest attempt. The work directory must look like the scheduler's
    /// .transflux-work staging area; otherwise deletion is skipped to protect arbitrary folders.</summary>
    private void CleanupAbandonedAttempt(CommitJournalEntry? journal, IReadOnlyList<Attempt> attempts)
    {
        string? tempPath = journal?.TempPath;
        string? finalPath = journal?.FinalPath;
        var commitToken = journal?.CommitToken;
        if (string.IsNullOrWhiteSpace(tempPath) || string.IsNullOrWhiteSpace(finalPath))
        {
            var attempt = attempts.OrderByDescending(a => a.Ordinal)
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.TempPath));
            if (attempt is null)
            {
                return;
            }
            tempPath = attempt.TempPath;
            finalPath = attempt.ReservedFinalPath;
            try { commitToken ??= Path.GetFileName(Path.GetDirectoryName(attempt.TempPath)); } catch { }
        }
        if (string.IsNullOrWhiteSpace(finalPath))
        {
            return;
        }

        var workDirectory = OutputStagingLayout.GetWorkDirectory(tempPath);

        var reservation = new ReservedOutput
        {
            FinalPath = finalPath!,
            WorkDirectory = workDirectory ?? string.Empty,
            CommitToken = commitToken ?? string.Empty,
            Extension = Path.GetExtension(tempPath) ?? string.Empty,
        };

        try { _outputPaths.ReleaseReservation(reservation); } catch { }
        if (workDirectory is not null)
        {
            try { _outputPaths.DeleteWorkDirectory(reservation); } catch { }
        }
    }

    // =====================================================================================
    // Per-job execution
    // =====================================================================================

    private async Task ProcessJobAsync(Guid jobId, CancellationToken stopToken)
    {
        var policy = Volatile.Read(ref _policy);
        Interlocked.Increment(ref _activeWorkers);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _jobCts[jobId] = cts;
        ReservedOutput? reserved = null;
        // Once the job enters Committing, the output may exist on disk even if the database or
        // journal write fails. Every cleanup path must then preserve staging and reservation for
        // RecoveryService/manual inspection instead of deleting the only verified output.
        var preserveAttemptArtifacts = false;
        try
        {
            if (!await TryTransitionAsync(jobId, JobState.Preflighting, cts.Token).ConfigureAwait(false))
            {
                return; // not queued anymore / lost the claim
            }
            await RecountAsync(CancellationToken.None).ConfigureAwait(false);

            var job = await _store.GetJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (job is null)
            {
                return;
            }

            var ordinal = 0;
            var fallbackUsed = 0;
            var fallbackCap = Math.Max(0, _options.MaxAutoFallbackAttemptsPerJob);

            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (job.State == JobState.CancelRequested)
                {
                    await FinishCanceledAsync(jobId).ConfigureAwait(false);
                    return;
                }

                ordinal++;

                // ---- Preflight: re-probe, re-plan, reserve output ----
                var sourceRef = new SourceReference
                {
                    AbsolutePath = job.SourcePath ?? job.SourceFingerprint.NormalizedPath,
                };

                Result<SourceSnapshot> probeResult;
                try
                {
                    probeResult = await _probe.ProbeAsync(sourceRef, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await FinishCanceledAsync(jobId).ConfigureAwait(false);
                    return;
                }

                if (!probeResult.IsSuccess || probeResult.Value is null)
                {
                    await FailFromCurrentAsync(jobId,
                        probeResult.Failure ?? FailureInfo.Of(FailureCode.ProbeFailed, "error.probeFailed"),
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                var source = probeResult.Value;
                if (!SameInput(job.SourceFingerprint, source))
                {
                    await TryNeedsAttentionAsync(jobId,
                        FailureInfo.Of(FailureCode.InputChanged, "error.inputChanged", "Source changed since enqueue.", true),
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                CapabilitySnapshot caps;
                try
                {
                    caps = await _caps.GetAsync(new CapabilityQuery { IncludeTrialRun = true }, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await FinishCanceledAsync(jobId).ConfigureAwait(false);
                    return;
                }

                Result<ValidatedPlan> planResult;
                try
                {
                    planResult = await _planner.BuildAsync(job.RequestSnapshot, source, caps, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await FinishCanceledAsync(jobId).ConfigureAwait(false);
                    return;
                }

                if (!planResult.IsSuccess || planResult.Value is null)
                {
                    await FailFromCurrentAsync(jobId,
                        planResult.Failure ?? FailureInfo.Of(FailureCode.PlanInvalid, "error.planInvalid"),
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                var plan = planResult.Value;
                if (plan.CandidatePaths.Count == 0)
                {
                    await FailFromCurrentAsync(jobId,
                        FailureInfo.Of(FailureCode.PlanInvalid, "error.noCandidatePath"), CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                if (fallbackUsed >= plan.CandidatePaths.Count)
                {
                    await FailFromCurrentAsync(jobId,
                        FailureInfo.Of(FailureCode.EncoderInitFailed, "error.noMoreCandidates"), CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                var candidate = plan.CandidatePaths[fallbackUsed];

                Result<ReservedOutput> reserveResult;
                try
                {
                    reserveResult = await _outputPaths.ReserveAsync(new OutputReservationRequest
                    {
                        SourcePath = source.AbsolutePath,
                        Container = plan.ExpectedOutput.Container,
                        RequestedFileName = job.RequestSnapshot.Output.FileNameOverride,
                        PreferredDirectory = job.RequestSnapshot.Output.OutputDirectory,
                    }, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await FinishCanceledAsync(jobId).ConfigureAwait(false);
                    return;
                }

                if (!reserveResult.IsSuccess || reserveResult.Value is null)
                {
                    await FailFromCurrentAsync(jobId,
                        reserveResult.Failure ?? FailureInfo.Of(FailureCode.OutputConflict, "error.outputReserve"),
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                reserved = reserveResult.Value;
                var reservedOutput = reserved;
                var tempPath = Path.Combine(reserved.WorkDirectory, $"attempt-{ordinal}-{Guid.NewGuid():N}{reserved.Extension}");

                var attempt = new Attempt
                {
                    AttemptId = Guid.NewGuid(),
                    JobId = jobId,
                    Ordinal = ordinal,
                    PlanSnapshot = plan,
                    CoreHash = plan.CoreHash,
                    DeviceIdentity = candidate.DeviceId,
                    // The candidate actually being executed, not necessarily CandidatePaths[0]
                    // after automatic fallback; diagnostics must report the real path.
                    ExecutedCandidateId = candidate.CandidateId,
                    TempPath = tempPath,
                    ReservedFinalPath = reserved.FinalPath,
                    State = AttemptState.Created,
                    StartedUtc = _clock.UtcNow,
                };
                await _store.CreateAttemptAsync(attempt, cts.Token).ConfigureAwait(false);
                Publish(new JobEvent { Kind = JobEventKind.AttemptChanged, JobId = jobId, Attempt = attempt });
                _activeAttempts[jobId] = attempt.AttemptId;

                // Preflighting -> Running (single active attempt)
                if (!await TryTransitionAsync(jobId, JobState.Running, cts.Token,
                        j => j with { ActiveAttemptId = attempt.AttemptId }).ConfigureAwait(false))
                {
                    _activeAttempts.TryRemove(jobId, out _);
                    await CleanupAsync(reservedOutput, tempPath).ConfigureAwait(false);
                    reserved = null;
                    await FinishCanceledAsync(jobId).ConfigureAwait(false);
                    return;
                }

                var runningAttempt = attempt with { State = AttemptState.Running };
                await _store.UpdateAttemptAsync(runningAttempt, CancellationToken.None).ConfigureAwait(false);
                Publish(new JobEvent { Kind = JobEventKind.AttemptChanged, JobId = jobId, Attempt = runningAttempt });

                var attemptCtx = new AttemptContext
                {
                    JobId = jobId,
                    AttemptId = attempt.AttemptId,
                    Candidate = candidate,
                    Source = source,
                    Request = job.RequestSnapshot,
                    TempPath = tempPath,
                    FfmpegPath = FfmpegPath ?? string.Empty,
                    FfprobePath = FfprobePath ?? string.Empty,
                    EffectiveDurationUs = plan.ExpectedOutput.DurationUs ?? source.DurationUs,
                };

                var coalescer = new ProgressCoalescer(
                    jobId,
                    attempt.AttemptId,
                    _progressThrottle,
                    _clock,
                    ps => Publish(new JobEvent { Kind = JobEventKind.Progress, JobId = jobId, Progress = ps }));
                IProgress<EngineProgress> progress = new SynchronousProgress(p =>
                {
                    if (IsAttemptActive(jobId, attempt.AttemptId))
                    {
                        coalescer.Report(p);
                    }
                });

                RunResult runResult;
                try
                {
                    runResult = await _runner.RunAsync(attemptCtx, progress, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    runResult = RunResult.CanceledResult(TimeSpan.Zero);
                }

                if (runResult.Canceled || cts.IsCancellationRequested)
                {
                    _activeAttempts.TryRemove(jobId, out _);
                    await MarkAttemptTerminalAsync(attempt.AttemptId, AttemptState.Canceled, null, runResult.ExitCode, runResult.StderrTail, null).ConfigureAwait(false);
                    await CleanupAsync(reservedOutput, tempPath).ConfigureAwait(false);
                    reserved = null;
                    await FinishCanceledAsync(jobId).ConfigureAwait(false);
                    return;
                }

                if (!runResult.Succeeded)
                {
                    var failure = runResult.Failure
                        ?? FailureInfo.Of(FailureCode.ProcessFailed, "error.processFailed", runResult.StderrTail ?? string.Empty, true);
                    _activeAttempts.TryRemove(jobId, out _);

                    var canFallback = policy.AllowFallback
                        && job.RequestSnapshot.Hardware.Backend == HardwareBackend.Auto
                        && IsCapabilityFailure(failure)
                        && fallbackUsed < fallbackCap
                        && fallbackUsed + 1 < plan.CandidatePaths.Count;

                    await MarkAttemptTerminalAsync(attempt.AttemptId, AttemptState.Failed, failure,
                        runResult.ExitCode, runResult.StderrTail, canFallback ? failure.Code.ToString() : null).ConfigureAwait(false);
                    await CleanupAsync(reservedOutput, tempPath).ConfigureAwait(false);
                    reserved = null;

                    if (canFallback)
                    {
                        fallbackUsed++;
                        if (await TryTransitionAsync(jobId, JobState.Recovering, CancellationToken.None).ConfigureAwait(false)
                            && await TryTransitionAsync(jobId, JobState.Preflighting, CancellationToken.None).ConfigureAwait(false))
                        {
                            job = await _store.GetJobAsync(jobId, CancellationToken.None).ConfigureAwait(false) ?? job;
                            continue;
                        }
                    }

                    await FailFromCurrentAsync(jobId, failure, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                // ---- Verify ----
                if (!await TryTransitionAsync(jobId, JobState.Verifying, cts.Token).ConfigureAwait(false))
                {
                    _activeAttempts.TryRemove(jobId, out _);
                    await CleanupAsync(reservedOutput, tempPath).ConfigureAwait(false);
                    reserved = null;
                    await FinishCanceledAsync(jobId).ConfigureAwait(false);
                    return;
                }
                await _store.UpdateAttemptAsync(attempt with { State = AttemptState.Verifying }, CancellationToken.None).ConfigureAwait(false);

                VerificationResult verification;
                try
                {
                    verification = await _verifier.VerifyAsync(new VerificationRequest
                    {
                        Plan = plan,
                        OutputPath = tempPath,
                        Source = source,
                        FullDecode = policy.FullVerification,
                    }, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await MarkAttemptTerminalAsync(attempt.AttemptId, AttemptState.Canceled, null, null, null, null).ConfigureAwait(false);
                    _activeAttempts.TryRemove(jobId, out _);
                    await CleanupAsync(reservedOutput, tempPath).ConfigureAwait(false);
                    reserved = null;
                    await FinishCanceledAsync(jobId).ConfigureAwait(false);
                    return;
                }

                if (!verification.Passed)
                {
                    var failure = verification.Failure
                        ?? FailureInfo.Of(FailureCode.VerifyFailed, "error.verifyFailed", verification.Summary);
                    _activeAttempts.TryRemove(jobId, out _);
                    await MarkAttemptTerminalAsync(attempt.AttemptId, AttemptState.Failed, failure, null, null, null).ConfigureAwait(false);
                    await CleanupAsync(reservedOutput, tempPath).ConfigureAwait(false);
                    reserved = null;
                    await FailFromCurrentAsync(jobId, failure, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                // The source may have changed while encoding/verifying. Re-probe before crossing
                // the commit boundary so unstable input can never be published as a valid result.
                Result<SourceSnapshot> postVerificationProbe;
                try
                {
                    postVerificationProbe = await _probe.ProbeAsync(sourceRef, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    postVerificationProbe = Result<SourceSnapshot>.Canceled();
                }

                if (postVerificationProbe.IsCanceled || cts.IsCancellationRequested)
                {
                    await MarkAttemptTerminalAsync(attempt.AttemptId, AttemptState.Canceled, null, null, null, null).ConfigureAwait(false);
                    _activeAttempts.TryRemove(jobId, out _);
                    await CleanupAsync(reservedOutput, tempPath).ConfigureAwait(false);
                    reserved = null;
                    await FinishCanceledAsync(jobId).ConfigureAwait(false);
                    return;
                }

                if (!postVerificationProbe.IsSuccess || postVerificationProbe.Value is null
                    || !SameInput(job.SourceFingerprint, postVerificationProbe.Value))
                {
                    var failure = postVerificationProbe.IsSuccess && postVerificationProbe.Value is not null
                        ? FailureInfo.Of(FailureCode.InputChanged, "error.inputChanged",
                            "Source changed while the output was being produced.", true)
                        : postVerificationProbe.Failure
                            ?? FailureInfo.Of(FailureCode.ProbeFailed, "error.probeFailed",
                                "Source could not be rechecked before commit.");
                    _activeAttempts.TryRemove(jobId, out _);
                    await MarkAttemptTerminalAsync(attempt.AttemptId, AttemptState.Failed, failure, null, null, null).ConfigureAwait(false);
                    await CleanupAsync(reservedOutput, tempPath).ConfigureAwait(false);
                    reserved = null;
                    await FailFromCurrentAsync(jobId, failure, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                // ---- Commit boundary: conditional transition is the atomic gate ----
                if (!await TryTransitionAsync(jobId, JobState.Committing, cts.Token).ConfigureAwait(false))
                {
                    _activeAttempts.TryRemove(jobId, out _);
                    await CleanupAsync(reservedOutput, tempPath).ConfigureAwait(false);
                    reserved = null;
                    await FinishCanceledAsync(jobId).ConfigureAwait(false);
                    return;
                }
                preserveAttemptArtifacts = true;
                await _store.UpdateAttemptAsync(attempt with { State = AttemptState.Committing }, CancellationToken.None).ConfigureAwait(false);

                CommitResult commit;
                try
                {
                    commit = await _committer.CommitAsync(new CommitRequest
                    {
                        JobId = jobId,
                        AttemptId = attempt.AttemptId,
                        TempPath = tempPath,
                        FinalPath = reserved.FinalPath,
                        ExpectedSize = runResult.OutputBytes ?? 0,
                        VerificationSummary = verification.Summary,
                    }, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    // Cancellation at the commit boundary is not a failed commit: rethrow so the
                    // outer canceled path records Canceled semantics instead of NeedsAttention.
                    throw;
                }
                catch (Exception ex)
                {
                    commit = new CommitResult
                    {
                        Success = false,
                        Failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitFailed", ex.Message),
                    };
                }

                if (!commit.Success)
                {
                    var failure = commit.Failure ?? FailureInfo.Of(FailureCode.CommitFailed, "error.commitFailed");
                    _activeAttempts.TryRemove(jobId, out _);
                    await MarkAttemptTerminalAsync(attempt.AttemptId, AttemptState.Failed, failure, null, null, null).ConfigureAwait(false);
                    // Committing -> NeedsAttention (never Failed after the commit boundary).
                    await TryNeedsAttentionAsync(jobId, failure, CancellationToken.None).ConfigureAwait(false);
                    // Keep the reservation, work directory, staged temp and journal: the commit may
                    // have crossed rename/publish before failing, and this may be the only copy.
                    return;
                }

                var output = new OutputRecord
                {
                    JobId = jobId,
                    AttemptId = attempt.AttemptId,
                    FinalPath = commit.FinalPath ?? reserved.FinalPath,
                    Size = runResult.OutputBytes ?? 0,
                    VerificationSummary = verification.Summary,
                    CommittedUtc = _clock.UtcNow,
                    CommitToken = commit.CommitToken ?? reserved.CommitToken,
                };

                // Persist the output record if the committer did not (Succeeded is only emitted
                // after the record exists).
                await EnsureOutputRecordAsync(output).ConfigureAwait(false);
                await MarkAttemptTerminalAsync(attempt.AttemptId, AttemptState.Succeeded, null, runResult.ExitCode, null, null).ConfigureAwait(false);
                _activeAttempts.TryRemove(jobId, out _);

                await TryTransitionAsync(jobId, JobState.Succeeded, CancellationToken.None,
                    j => j with { ActiveAttemptId = null }).ConfigureAwait(false);
                Publish(new JobEvent { Kind = JobEventKind.OutputCommitted, JobId = jobId, Output = output });
                ReleaseQuietly(reservedOutput);
                reserved = null;
                DeleteTempQuietly(tempPath);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            _activeAttempts.TryRemove(jobId, out _);
            if (!preserveAttemptArtifacts)
            {
                ReleaseQuietly(reserved);
            }
            await FinishCanceledAsync(jobId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _activeAttempts.TryRemove(jobId, out _);
            if (!preserveAttemptArtifacts)
            {
                ReleaseQuietly(reserved);
            }
            await FailFromCurrentAsync(jobId,
                FailureInfo.Of(FailureCode.Unknown, "error.unexpected", ex.Message), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _jobCts.TryRemove(jobId, out _);
            cts.Dispose();
            Interlocked.Decrement(ref _activeWorkers);
        }
    }

    // =====================================================================================
    // Transition / cleanup helpers
    // =====================================================================================

    /// <summary>
    /// CAS transition keyed on the job's current revision. On a revision/state mismatch (lost
    /// race) it re-reads and retries if the target is still legal, otherwise returns false.
    /// </summary>
    private async Task<bool> TryTransitionAsync(Guid jobId, JobState target, CancellationToken ct, Func<Job, Job>? mutate = null)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var job = await _store.GetJobAsync(jobId, ct).ConfigureAwait(false);
            if (job is null || !JobStateMachine.CanTransition(job.State, target))
            {
                return false;
            }

            var transitioned = (mutate is null ? job : mutate(job)) with
            {
                State = target,
                Revision = job.Revision + 1,
                UpdatedUtc = _clock.UtcNow,
            };

            try
            {
                await _store.UpdateJobAsync(transitioned, job.Revision, ct).ConfigureAwait(false);
                Publish(new JobEvent { Kind = JobEventKind.StateChanged, JobId = jobId, Job = transitioned });
                return true;
            }
            catch (Exception)
            {
                var fresh = await _store.GetJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                if (fresh is null || fresh.Revision != job.Revision || fresh.State != job.State)
                {
                    continue; // lost the race; re-read and re-evaluate
                }
                throw;
            }
        }
        return false;
    }

    private async Task FailFromCurrentAsync(Guid jobId, FailureInfo failure, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var job = await _store.GetJobAsync(jobId, ct).ConfigureAwait(false);
            if (job is null || JobStateMachine.IsTerminal(job.State))
            {
                return;
            }

            var target = JobStateMachine.CanTransition(job.State, JobState.Failed)
                ? JobState.Failed
                : JobState.NeedsAttention;
            if (!JobStateMachine.CanTransition(job.State, target))
            {
                // Cancellation can win while a fallback failure is being recorded. Do not leave
                // the job suspended in CancelRequested without a worker.
                await FinishCanceledIfRequestedAsync(jobId).ConfigureAwait(false);
                return;
            }

            if (await TryTransitionAsync(jobId, target, ct, j => j with { ActiveAttemptId = null }).ConfigureAwait(false))
            {
                var latest = await _store.GetJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                Publish(new JobEvent { Kind = JobEventKind.Failed, JobId = jobId, Failure = failure, Job = latest });
                return;
            }
        }

        await FinishCanceledIfRequestedAsync(jobId).ConfigureAwait(false);
    }

    private async Task FinishCanceledIfRequestedAsync(Guid jobId)
    {
        var job = await _store.GetJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        if (job is { State: JobState.CancelRequested })
        {
            await FinishCanceledAsync(jobId).ConfigureAwait(false);
        }
    }

    private async Task TryNeedsAttentionAsync(Guid jobId, FailureInfo failure, CancellationToken ct)
    {
        if (await TryTransitionAsync(jobId, JobState.NeedsAttention, ct, j => j with { ActiveAttemptId = null }).ConfigureAwait(false))
        {
            return;
        }
        await FailFromCurrentAsync(jobId, failure, ct).ConfigureAwait(false);
    }

    private async Task FinishCanceledAsync(Guid jobId)
    {
        // Mark any attempt left in a running state as canceled.
        try
        {
            var attempts = await _store.GetAttemptsForJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            foreach (var a in attempts)
            {
                if (a.State is AttemptState.Created or AttemptState.Running or AttemptState.Verifying or AttemptState.Committing)
                {
                    await _store.UpdateAttemptAsync(a with
                    {
                        State = AttemptState.Canceled,
                        EndedUtc = _clock.UtcNow,
                    }, CancellationToken.None).ConfigureAwait(false);
                    Publish(new JobEvent { Kind = JobEventKind.AttemptChanged, JobId = jobId, Attempt = a with { State = AttemptState.Canceled } });
                }
            }
        }
        catch
        {
            // best-effort
        }

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var job = await _store.GetJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (job is null || job.State == JobState.Canceled)
            {
                return;
            }

            JobState next;
            if (job.State == JobState.CancelRequested)
            {
                next = JobState.Canceled;
            }
            else if (JobStateMachine.AllowsCancel(job.State))
            {
                next = JobState.CancelRequested;
            }
            else
            {
                return; // terminal or commit boundary; nothing to do
            }

            if (await TryTransitionAsync(jobId, next, CancellationToken.None, j => j with { ActiveAttemptId = null }).ConfigureAwait(false)
                && next == JobState.Canceled)
            {
                return;
            }
        }
    }

    private async Task MarkAttemptTerminalAsync(
        Guid attemptId, AttemptState state, FailureInfo? failure, int? exitCode, string? stderr, string? fallbackReason)
    {
        var attempt = await _store.GetAttemptAsync(attemptId, CancellationToken.None).ConfigureAwait(false);
        if (attempt is null)
        {
            return;
        }
        var updated = attempt with
        {
            State = state,
            EndedUtc = _clock.UtcNow,
            Failure = failure,
            ExitCode = exitCode ?? attempt.ExitCode,
            StderrTail = stderr ?? attempt.StderrTail,
            FallbackReason = fallbackReason ?? attempt.FallbackReason,
        };
        await _store.UpdateAttemptAsync(updated, CancellationToken.None).ConfigureAwait(false);
        Publish(new JobEvent { Kind = JobEventKind.AttemptChanged, JobId = updated.JobId, Attempt = updated });
    }

    private async Task EnsureOutputRecordAsync(OutputRecord output)
    {
        try
        {
            var existing = await _store.GetOutputsForJobAsync(output.JobId, CancellationToken.None).ConfigureAwait(false);
            if (!existing.Any(o => o.AttemptId == output.AttemptId))
            {
                await _store.SaveOutputAsync(output, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // The committer is the primary writer; failure to double-check is non-fatal here.
        }
    }

    private async Task CleanupAsync(ReservedOutput reserved, string tempPath)
    {
        DeleteTempQuietly(tempPath);
        await Task.Yield();
        ReleaseQuietly(reserved);
    }

    /// <summary>Combined cleanup for an attempt that never crossed the commit boundary.
    /// Releases the naming reservation and deletes the attempt work directory.</summary>
    private void ReleaseQuietly(ReservedOutput? reserved)
    {
        if (reserved is null)
        {
            return;
        }
        try { _outputPaths.ReleaseReservation(reserved); } catch { }
        try { _outputPaths.DeleteWorkDirectory(reserved); } catch { }
    }

    private void DeleteTempQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            if (_fs.FileExists(path))
            {
                _fs.DeleteFile(path);
            }
        }
        catch { }
    }

    private static bool SameInput(SourceFingerprint fingerprint, SourceSnapshot snapshot) =>
        fingerprint.SizeBytes == snapshot.SizeBytes && fingerprint.ModifiedUtc == snapshot.ModifiedUtc;

    private static bool IsCapabilityFailure(FailureInfo failure) =>
        failure.Code is FailureCode.DeviceUnavailable or FailureCode.EncoderInitFailed;

    private bool IsAttemptActive(Guid jobId, Guid attemptId) =>
        _activeAttempts.TryGetValue(jobId, out var active) && active == attemptId;

    private void Publish(JobEvent evt) => _hub.Publish(evt);

    private sealed class SynchronousProgress : IProgress<EngineProgress>
    {
        private readonly Action<EngineProgress> _report;

        public SynchronousProgress(Action<EngineProgress> report) => _report = report;

        public void Report(EngineProgress value) => _report(value);
    }

    // =====================================================================================
    // IAsyncDisposable
    // =====================================================================================

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _loopCts?.Cancel(); } catch { }
        foreach (var entry in _jobCts)
        {
            try { entry.Value.Cancel(); } catch { }
        }

        if (_dispatchTask is not null)
        {
            try { await _dispatchTask.ConfigureAwait(false); } catch { }
        }

        var pending = _workers.Values.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        }

        _hub.CompleteAll();
        _wake.Writer.TryComplete();
        _loopCts?.Dispose();
    }
}
