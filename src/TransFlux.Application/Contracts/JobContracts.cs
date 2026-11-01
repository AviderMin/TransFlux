using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;

namespace TransFlux.Application.Contracts;

public sealed record EnqueueRequest
{
    public required ConversionRequest Request { get; init; }
    public required Domain.Media.SourceSnapshot Source { get; init; }
    public Guid? RetryOfJobId { get; init; }
}

public sealed record CancelResult
{
    public bool Accepted { get; init; }
    public string Message { get; init; } = string.Empty;
    public static CancelResult Ok(string msg) => new() { Accepted = true, Message = msg };
    public static CancelResult Refused(string msg) => new() { Accepted = false, Message = msg };
}

public enum JobEventKind { Snapshot, Enqueued, StateChanged, Progress, AttemptChanged, OutputCommitted, Failed, Removed, SnapshotCompleted }

public sealed record JobEvent
{
    public JobEventKind Kind { get; init; }
    public long Sequence { get; init; }
    public Guid? JobId { get; init; }
    public Job? Job { get; init; }
    public Attempt? Attempt { get; init; }
    public ProgressSnapshot? Progress { get; init; }
    public OutputRecord? Output { get; init; }
    public FailureInfo? Failure { get; init; }
}

public sealed record JobWatchQuery
{
    public Guid? JobId { get; init; }
    public bool IncludeCompleted { get; init; } = true;
    public long? AfterSequence { get; init; }
}

public interface IJobService
{
    Task<Guid> EnqueueAsync(EnqueueRequest request, CancellationToken ct);
    Task<CancelResult> CancelAsync(Guid id, CancellationToken ct);
    Task<Guid> RetryAsync(Guid id, CancellationToken ct);
    /// <summary>Moves a queued job by -1 (up) or +1 (down); returns false if not movable.</summary>
    Task<bool> MoveQueuedAsync(Guid id, int direction, CancellationToken ct);
    IAsyncEnumerable<JobEvent> WatchAsync(JobWatchQuery query, CancellationToken ct);
    Task PauseQueueAsync(CancellationToken ct);
    Task ResumeQueueAsync(CancellationToken ct);
    QueueControlSnapshot GetControl();
    /// <summary>Removes a not-yet-started job from the queue (undoable).</summary>
    Task<bool> RemoveQueuedAsync(Guid id, CancellationToken ct);
    /// <summary>Updates limits for future dispatches; existing attempts keep their policy.</summary>
    void ConfigureExecution(int maxConcurrentTranscodes, bool allowAutomaticFallback, bool fullVerification);
}

public sealed record QueueControlSnapshot
{
    public bool Paused { get; init; }
    public int RunningCount { get; init; }
    public int QueuedCount { get; init; }
}
