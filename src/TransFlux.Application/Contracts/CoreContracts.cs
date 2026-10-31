using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Application.Contracts;

/// <summary>Identity of the pinned media core (ffmpeg/ffprobe).</summary>
public sealed record CoreIdentity
{
    public required string CoreHash { get; init; }
    public string FfmpegVersion { get; init; } = string.Empty;
    public string FfprobePath { get; init; } = string.Empty;
    public string FfmpegPath { get; init; } = string.Empty;
    public string BuildConfiguration { get; init; } = string.Empty;
}

public sealed record FfmpegCapabilityInfo
{
    public IReadOnlyList<string> Encoders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Decoders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Hwaccels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Filters { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PixelFormats { get; init; } = Array.Empty<string>();
}

/// <summary>Engine-owned information about the pinned core, consumed by capability probing.</summary>
public interface IEngineCapabilities
{
    CoreIdentity GetIdentity();
    Task<FfmpegCapabilityInfo> QueryAsync(CancellationToken ct);
    /// <summary>Runs a bounded trial encode of a generated sample on the given encoder.</summary>
    Task<HardwareValidationResult> ValidateEncoderAsync(HardwareValidationRequest request, CancellationToken ct);
}

public sealed record HardwareValidationRequest
{
    public required string EncoderName { get; init; }
    public string? DeviceArgument { get; init; }
    public string? PixelFormat { get; init; }
    public int Width { get; init; } = 640;
    public int Height { get; init; } = 360;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed record HardwareValidationResult
{
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public interface IMediaProbe
{
    Task<Result<SourceSnapshot>> ProbeAsync(SourceReference source, CancellationToken ct);
}

public interface ICapabilityService
{
    Task<CapabilitySnapshot> GetAsync(CapabilityQuery query, CancellationToken ct);
    Task<CapabilitySnapshot> RefreshAsync(CancellationToken ct);
}

public interface IExecutionPlanner
{
    Task<Result<ValidatedPlan>> BuildAsync(
        ConversionRequest request,
        SourceSnapshot source,
        CapabilitySnapshot capabilities,
        CancellationToken ct);
}
