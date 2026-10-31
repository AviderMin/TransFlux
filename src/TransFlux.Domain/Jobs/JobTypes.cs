using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Domain.Jobs;

public enum JobState
{
    Queued = 0,
    Preflighting = 1,
    Running = 2,
    Verifying = 3,
    Committing = 4,
    Succeeded = 5,
    Failed = 6,
    CancelRequested = 7,
    Canceled = 8,
    NeedsAttention = 9,
    Recovering = 10,
    Interrupted = 11,
}

public enum AttemptState
{
    Created = 0,
    Running = 1,
    Verifying = 2,
    Committing = 3,
    Succeeded = 4,
    Failed = 5,
    Canceled = 6,
    Interrupted = 7,
}

public enum JobStage { Preflight = 0, Encoding = 1, Verifying = 2, Committing = 3, Finalizing = 4 }

public enum FailureCode
{
    None = 0,
    CoreMissing = 1,
    CoreIntegrityFailed = 2,
    InputChanged = 3,
    ProbeFailed = 4,
    UnsupportedMedia = 5,
    PlanInvalid = 6,
    OutputConflict = 7,
    DiskFull = 8,
    AccessDenied = 9,
    DeviceUnavailable = 10,
    EncoderInitFailed = 11,
    ProcessFailed = 12,
    VerifyFailed = 13,
    CommitFailed = 14,
    DbFailed = 15,
    Interrupted = 16,
    Canceled = 17,
    Unknown = 99,
}

public sealed record FailureInfo
{
    public required FailureCode Code { get; init; }
    public required string UserMessageKey { get; init; }
    public string TechnicalDetail { get; init; } = string.Empty;
    public bool Retryable { get; init; }
    public IReadOnlyList<string> SuggestedActions { get; init; } = Array.Empty<string>();

    public static FailureInfo Of(FailureCode code, string userMessageKey, string technical = "",
        bool retryable = false, params string[] actions)
        => new() { Code = code, UserMessageKey = userMessageKey, TechnicalDetail = technical,
            Retryable = retryable, SuggestedActions = actions };
}

public sealed record Job
{
    public required Guid JobId { get; init; }
    public required ConversionRequest RequestSnapshot { get; init; }
    public required SourceFingerprint SourceFingerprint { get; init; }
    public long EnqueueOrder { get; init; }
    public JobState State { get; init; } = JobState.Queued;
    public long Revision { get; init; }
    public Guid? ActiveAttemptId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public Guid? RetryOfJobId { get; init; }
    public string RequestHash { get; init; } = string.Empty;
    public string? SourcePath { get; init; }
}

public sealed record Attempt
{
    public required Guid AttemptId { get; init; }
    public required Guid JobId { get; init; }
    public int Ordinal { get; init; }
    public required ValidatedPlan PlanSnapshot { get; init; }
    public string CoreHash { get; init; } = string.Empty;
    public string? DeviceIdentity { get; init; }
    public required string TempPath { get; init; }
    public required string ReservedFinalPath { get; init; }
    public AttemptState State { get; init; } = AttemptState.Created;
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public int? ExitCode { get; init; }
    public FailureInfo? Failure { get; init; }
    public string? FallbackReason { get; init; }
    public string? StderrTail { get; init; }
}

public sealed record ProgressSnapshot
{
    public required Guid JobId { get; init; }
    public required Guid AttemptId { get; init; }
    public long Sequence { get; init; }
    public JobStage Stage { get; init; }
    public long? ProcessedUs { get; init; }
    public double? Fraction { get; init; }
    public double? Speed { get; init; }
    public TimeSpan? Eta { get; init; }
    public long? BytesWritten { get; init; }
    public DateTimeOffset SampledUtc { get; init; }
}

public sealed record OutputRecord
{
    public required Guid JobId { get; init; }
    public required Guid AttemptId { get; init; }
    public required string FinalPath { get; init; }
    public long Size { get; init; }
    public string VerificationSummary { get; init; } = string.Empty;
    public DateTimeOffset CommittedUtc { get; init; }
    public required string CommitToken { get; init; }
}

public enum CommitPhase { Prepared = 0, Renamed = 1, Published = 2, Failed = 3 }

public sealed record CommitJournalEntry
{
    public required string CommitToken { get; init; }
    public required Guid AttemptId { get; init; }
    public required Guid JobId { get; init; }
    public CommitPhase Phase { get; init; }
    public required string TempPath { get; init; }
    public required string FinalPath { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}
