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
    public bool FastStart { get; init; }
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
    public int PlanSchemaVersion { get; init; } = 1;
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
