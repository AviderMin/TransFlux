namespace TransFlux.Domain.Media;

/// <summary>A single candidate hardware/software execution path that has been
/// verified for the current device and core. Compiled parameter lists are added
/// by the engine; this record describes availability and identity.</summary>
public sealed record CandidateCapability
{
    public required string CandidateId { get; init; }
    public required string Backend { get; init; }          // e.g. "cpu", "nvidia", "intel", "amd"
    public required string EncoderName { get; init; }      // e.g. "libx264", "h264_nvenc"
    public bool SupportsGpuDecode { get; init; }
    public bool SupportsGpuFilter { get; init; }
    public string? DeviceId { get; init; }
    public int? Direct3DAdapterIndex { get; init; }
    public bool TrialCompleted { get; init; }
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
    public string? DiagnosticDetail { get; init; }
    public IReadOnlyList<string> SupportedPixelFormats { get; init; } = Array.Empty<string>();
    public bool Supports10Bit { get; init; }
}

/// <summary>Result of capability probing for the current machine + core.</summary>
public sealed record CapabilitySnapshot
{
    public required string CapabilityHash { get; init; }
    public string CoreHash { get; init; } = string.Empty;
    public string OsBuild { get; init; } = string.Empty;
    public IReadOnlyList<AdapterInfo> Adapters { get; init; } = Array.Empty<AdapterInfo>();
    public IReadOnlyList<CandidateCapability> Candidates { get; init; } = Array.Empty<CandidateCapability>();
    public DateTimeOffset ProbedUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool HasAvailableGpuEncoder => Candidates.Any(c => c.Available && c.Backend != "cpu");
}

public enum AdapterVendor { Unknown, Nvidia, Intel, Amd, Microsoft }

public sealed record AdapterInfo
{
    public required string AdapterId { get; init; }      // LUID-based stable id
    public required string Name { get; init; }
    public AdapterVendor Vendor { get; init; }
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    public string? DriverVersion { get; init; }
    public long? DedicatedMemoryBytes { get; init; }
    public bool IsSoftware { get; init; }
    public long Luid { get; init; }
    public int DxgiIndex { get; init; }
}
