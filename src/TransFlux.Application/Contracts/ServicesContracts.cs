using TransFlux.Domain.Presets;

namespace TransFlux.Application.Contracts;

/// <summary>User-facing application settings (persisted as JSON, schema-versioned).</summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string Theme { get; init; } = "System";          // System | Light | Dark
    public string Language { get; init; } = "system";      // system | zh-Hans | en
    public string? DefaultOutputDirectory { get; init; }
    public int MaxConcurrentTranscodes { get; init; } = 1;
    public bool AllowAutomaticFallback { get; init; } = true;
    public bool FullVerification { get; init; }
    public bool PreserveTitleAndChaptersByDefault { get; init; }
    public int LogRetentionDays { get; init; } = 14;
    public string? LastTargetFormatKey { get; init; }
    public string? LastQuality { get; init; }
}

public interface IPresetService
{
    Task<IReadOnlyList<Preset>> GetAllAsync(CancellationToken ct);
    Task SaveAsync(Preset preset, CancellationToken ct);
    Task DeleteAsync(Guid presetId, CancellationToken ct);
}

public interface ISettingsService
{
    Task<AppSettings> GetAsync(CancellationToken ct);
    Task SaveAsync(AppSettings settings, CancellationToken ct);
}
/// <summary>One persisted conversion history row (outputs survive history clearing).</summary>
public sealed record HistoryEntry
{
    public required Guid JobId { get; init; }
    public string? SourcePath { get; init; }
    public string? OutputPath { get; init; }
    public TransFlux.Domain.Jobs.JobState State { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public long? OutputSizeBytes { get; init; }
    public TransFlux.Domain.Jobs.FailureCode? FailureCode { get; init; }
    public Guid? RetryOfJobId { get; init; }
}

public interface IHistoryService
{
    Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int limit, CancellationToken ct);
    /// <summary>Clears history records only; never deletes output files.</summary>
    Task ClearAsync(CancellationToken ct);
}
