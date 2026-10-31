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
