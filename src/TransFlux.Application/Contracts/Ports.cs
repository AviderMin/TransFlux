using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;

namespace TransFlux.Application.Contracts;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed record FileIdentity(string FullPath, long SizeBytes, DateTimeOffset ModifiedUtc, string? VolumeSerial, string? FileId);

public interface IFileSystemService
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    long GetAvailableFreeSpace(string path);
    void DeleteFile(string path);
    string NormalizePath(string path);
    bool PathsReferToSameFile(string a, string b);
    Task<FileIdentity?> GetIdentityAsync(string path, CancellationToken ct);
    FileIdentity GetIdentity(string path);
    IReadOnlyList<string> EnumerateFiles(string directory, bool recursive);
    string GetFileNameWithoutExtension(string path);
    string GetExtension(string path);
    long GetFileLength(string path);
    DateTimeOffset GetLastWriteTimeUtc(string path);
    /// <summary>Atomically moves temp to final without replacing an existing file.
    /// Throws <see cref="IOException"/> if the destination exists.</summary>
    void MoveNoReplace(string source, string destination);
}

public interface IJobStore
{
    Task InitializeAsync(CancellationToken ct);
    Task<long> NextEnqueueOrderAsync(CancellationToken ct);
    /// <summary>Atomically moves one queued job before/after another queued job and normalizes order.</summary>
    Task<bool> ReorderQueuedAsync(Guid jobId, Guid targetJobId, bool placeBefore, CancellationToken ct);
    Task CreateJobAsync(Job job, CancellationToken ct);
    /// <summary>Conditional update keyed on revision; throws on mismatch.</summary>
    Task UpdateJobAsync(Job job, long expectedRevision, CancellationToken ct);
    Task<Job?> GetJobAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Job>> GetJobsAsync(IReadOnlyCollection<JobState>? states, CancellationToken ct);
    Task CreateAttemptAsync(Attempt attempt, CancellationToken ct);
    Task UpdateAttemptAsync(Attempt attempt, CancellationToken ct);
    Task<Attempt?> GetAttemptAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Attempt>> GetAttemptsForJobAsync(Guid jobId, CancellationToken ct);
    Task SaveOutputAsync(OutputRecord record, CancellationToken ct);
    Task<IReadOnlyList<OutputRecord>> GetOutputsAsync(CancellationToken ct);
    Task<IReadOnlyList<OutputRecord>> GetOutputsForJobAsync(Guid jobId, CancellationToken ct);
    Task UpsertJournalAsync(CommitJournalEntry entry, CancellationToken ct);
    Task<CommitJournalEntry?> GetJournalAsync(string token, CancellationToken ct);
    /// <summary>Journals recovery must reconcile: Prepared, Published and Failed rows. Failed
    /// rows are closed by the reconciler so a crash between marking a failure and deleting the
    /// journal cannot leak rows forever.</summary>
    Task<IReadOnlyList<CommitJournalEntry>> GetOpenJournalsAsync(CancellationToken ct);
    Task DeleteJournalAsync(string token, CancellationToken ct);
    Task<IReadOnlyList<Preset>> GetPresetsAsync(CancellationToken ct);
    Task UpsertPresetAsync(Preset preset, CancellationToken ct);
    Task DeletePresetAsync(Guid presetId, CancellationToken ct);
    Task SaveCapabilityAsync(string cacheKey, string payloadJson, DateTimeOffset testedUtc, CancellationToken ct);
    Task<(string PayloadJson, DateTimeOffset TestedUtc)?> GetCapabilityAsync(string cacheKey, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct);
    Task SaveSettingAsync(string key, string valueJson, CancellationToken ct);
    Task<IReadOnlyList<Job>> MarkActiveJobsInterruptedAsync(CancellationToken ct);
}

public sealed record OutputReservationRequest
{
    public required string SourcePath { get; init; }
    public required ContainerFormat Container { get; init; }
    public string? RequestedFileName { get; init; }
    public string? PreferredDirectory { get; init; }
}

public sealed record ReservedOutput
{
    public required string FinalPath { get; init; }
    public required string WorkDirectory { get; init; }
    public required string CommitToken { get; init; }
    public required string Extension { get; init; }
}

/// <summary>Releases only the naming/scheduling reservation (the lock file) for a final path.
/// The attempt work directory and any staged output remain untouched.</summary>
public interface IOutputReservationReleaser
{
    void ReleaseReservation(ReservedOutput reserved);
}

/// <summary>Deletes an attempt work directory (staging area). Callers must only use this after
/// the attempt no longer needs to be recovered; unresolved commits keep their staging files.</summary>
public interface IOutputWorkDirectoryCleaner
{
    void DeleteWorkDirectory(ReservedOutput reserved);
}

public interface IOutputPathService : IOutputReservationReleaser, IOutputWorkDirectoryCleaner
{
    /// <summary>Preflights output permissions/space and reserves a non-existing final path
    /// plus an attempt work directory on the same volume.</summary>
    Task<Result<ReservedOutput>> ReserveAsync(OutputReservationRequest request, CancellationToken ct);
    /// <summary>Legacy combined cleanup: releases the reservation and deletes the work directory.
    /// Do not use after a failed or unresolved commit; use the narrow interfaces instead.</summary>
    void Release(ReservedOutput reserved);
}

public sealed record CommitRequest
{
    public required Guid JobId { get; init; }
    public required Guid AttemptId { get; init; }
    public required string TempPath { get; init; }
    public required string FinalPath { get; init; }
    public long ExpectedSize { get; init; }
    /// <summary>Human-readable verification result persisted with the output record.</summary>
    public string VerificationSummary { get; init; } = string.Empty;
}

public sealed record CommitResult
{
    public bool Success { get; init; }
    public string? CommitToken { get; init; }
    public string? FinalPath { get; init; }
    public FailureInfo? Failure { get; init; }
}

public interface IOutputCommitter
{
    Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken ct);
}
