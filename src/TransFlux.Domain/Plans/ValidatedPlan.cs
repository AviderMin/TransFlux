using TransFlux.Domain.Media;

namespace TransFlux.Domain.Plans;

public sealed record ExecutionCandidate
{
    public required string CandidateId { get; init; }
    public required HardwareBackend Backend { get; init; }
    public required string EncoderName { get; init; }
    public bool UsesGpuDecode { get; init; }
    public bool UsesGpuFilter { get; init; }
    public string? DeviceId { get; init; }
    /// <summary>Fully structured ffmpeg argument list (excluding executable path).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string Rationale { get; init; } = string.Empty;
}

/// <summary>
/// Expected signature of one output video/audio track, in output mapping order. Null
/// members are not asserted, so a plan only fails verification on data it actually knows.
/// </summary>
public sealed record ExpectedStreamSignature
{
    public required MediaKind Kind { get; init; }
    /// <summary>Expected ffprobe codec_name; null = not asserted.</summary>
    public string? Codec { get; init; }
    /// <summary>Expected audio channel count; null = not asserted.</summary>
    public int? Channels { get; init; }
    /// <summary>Expected default disposition; null = not asserted.</summary>
    public bool? IsDefault { get; init; }
    /// <summary>Expected forced disposition; null = not asserted.</summary>
    public bool? IsForced { get; init; }
}

/// <summary>Dolby Vision retention expectation derived from the selected source video.</summary>
public sealed record DolbyVisionExpectation
{
    /// <summary>DOVI configuration record profile (5/7/8).</summary>
    public required int Profile { get; init; }
    /// <summary>Expected BL signal compatibility id; null = not asserted.</summary>
    public int? BlSignalCompatibilityId { get; init; }
    /// <summary>True when the source DOVI record declared an RPU; the output must declare one too.</summary>
    public bool RpuRequired { get; init; }
    /// <summary>True when the source DOVI record declared an enhancement layer; the output must too.</summary>
    public bool ElRequired { get; init; }
    /// <summary>True when the source DOVI record declared a base layer; the output must too.</summary>
    public bool BlRequired { get; init; }
}

public sealed record ExpectedOutput
{
    public ContainerFormat Container { get; init; }
    public VideoCodec? VideoCodec { get; init; }
    public AudioCodec? AudioCodec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? PixelFormat { get; init; }
    public long? DurationUs { get; init; }
    public int StreamCount { get; init; }
    public int VideoStreamCount { get; init; }
    public int AudioStreamCount { get; init; }
    public int AttachmentStreamCount { get; init; }
    public bool FastStart { get; init; }
    // Colour passthrough expectations (null = not asserted).
    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorRange { get; init; }
    /// <summary>Per-output-track language tags in mapping order ("und"/empty = not asserted).</summary>
    public IReadOnlyList<string> AudioLanguages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SubtitleLanguages { get; init; } = Array.Empty<string>();
    /// <summary>Per-track video/audio signatures in output mapping order; empty = not asserted.</summary>
    public IReadOnlyList<ExpectedStreamSignature> TrackSignatures { get; init; } = Array.Empty<ExpectedStreamSignature>();
    /// <summary>Dolby Vision DOVI configuration retention expectation; null = not asserted.</summary>
    public DolbyVisionExpectation? DolbyVision { get; init; }
}

public sealed record ResourceNeeds
{
    public bool NeedsGpuEncoder { get; init; }
    public string? AdapterId { get; init; }
    public bool NeedsCpuHeavy { get; init; }
    public bool NeedsVerification { get; init; } = true;
}

/// <summary>Validated, explainable execution plan. Compiled but not yet executed.</summary>
public sealed record ValidatedPlan
{
    public required Guid PlanId { get; init; }
    public int PlanSchemaVersion { get; init; } = 2;
    public required string RequestHash { get; init; }
    public required string CoreHash { get; init; }
    public required string CapabilityHash { get; init; }
    public IReadOnlyList<ExecutionCandidate> CandidatePaths { get; init; } = Array.Empty<ExecutionCandidate>();
    public required ExpectedOutput ExpectedOutput { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ParameterPlan { get; init; } = Array.Empty<string>();
    public ResourceNeeds ResourceNeeds { get; init; } = new();
    public required string PlanHash { get; init; }
}
