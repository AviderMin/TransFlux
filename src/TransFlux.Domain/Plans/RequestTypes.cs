using TransFlux.Domain.Common;
using TransFlux.Domain.Media;

namespace TransFlux.Domain.Plans;

public enum ConversionMode { VideoTranscode = 0, AudioExtract = 1, Remux = 2 }
public enum ContainerFormat { Mp4 = 0, Mkv = 1, M4a = 2, Flac = 3, Wav = 4 }
public enum VideoCodec { H264 = 0, Hevc = 1, Av1 = 2, Copy = 3 }
public enum AudioCodec { Aac = 0, Flac = 1, PcmS16le = 2, Copy = 3 }
public enum QualityTier { SmallerSize = 0, Balanced = 1, HigherQuality = 2 }
public enum SpeedTier { Faster = 0, Balanced = 1, Slower = 2 }
public enum ResizeMode { KeepDisplaySize = 0, Max1080p = 1 }
public enum MetadataPolicy { StripPersonalTags = 0, PreserveTitleAndChapters = 1 }
public enum HardwareBackend { Auto = 0, Cpu = 1, NvidiaNvenc = 2, IntelQsv = 3, AmdAmf = 4 }

public static class ContainerFormats
{
    public static string Extension(this ContainerFormat c) => c switch
    {
        ContainerFormat.Mp4 => ".mp4",
        ContainerFormat.Mkv => ".mkv",
        ContainerFormat.M4a => ".m4a",
        ContainerFormat.Flac => ".flac",
        ContainerFormat.Wav => ".wav",
        _ => throw new DomainException($"Unknown container {c}"),
    };

    public static string DisplayName(this ContainerFormat c) => c switch
    {
        ContainerFormat.Mp4 => "MP4",
        ContainerFormat.Mkv => "MKV",
        ContainerFormat.M4a => "M4A",
        ContainerFormat.Flac => "FLAC",
        ContainerFormat.Wav => "WAV",
        _ => c.ToString(),
    };
}

/// <summary>Single trim segment expressed relative to source timeline.</summary>
public sealed record TrimRange
{
    public required TimeSpan Start { get; init; }
    public required TimeSpan End { get; init; }

    public static TrimRange Create(TimeSpan start, TimeSpan end, TimeSpan? knownDuration)
    {
        DomainGuard.That(start >= TimeSpan.Zero, "Trim start must be >= 0.");
        DomainGuard.That(end > start, "Trim end must be greater than start.");
        if (knownDuration is TimeSpan d)
        {
            DomainGuard.That(end <= d, "Trim end must not exceed duration.");
        }
        return new TrimRange { Start = start, End = end };
    }

    public TimeSpan Duration => End - Start;
}

public sealed record ResizeSpec
{
    public ResizeMode Mode { get; init; } = ResizeMode.KeepDisplaySize;
    public bool AllowUpscale { get; init; }
    public static ResizeSpec KeepDisplay() => new() { Mode = ResizeMode.KeepDisplaySize, AllowUpscale = false };
    public static ResizeSpec Max1080p() => new() { Mode = ResizeMode.Max1080p, AllowUpscale = false };
}

public sealed record AudioSpec
{
    public AudioCodec Codec { get; init; } = AudioCodec.Aac;
    public int? BitRateKbps { get; init; } = 192;
    public bool KeepChannelLayout { get; init; } = true;
}

public sealed record HardwarePreference
{
    public HardwareBackend Backend { get; init; } = HardwareBackend.Auto;
    public string? AdapterId { get; init; }
    public string? EncoderName { get; init; }

    public static HardwarePreference Auto => new() { Backend = HardwareBackend.Auto };
    public static HardwarePreference Cpu => new() { Backend = HardwareBackend.Cpu };
    public static HardwarePreference ForAdapter(string adapterId, string? encoderName = null)
        => new() { Backend = HardwareBackend.Auto, AdapterId = adapterId, EncoderName = encoderName };
}

public sealed record OutputPolicy
{
    public string? OutputDirectory { get; init; }
    public string? FileNameOverride { get; init; }
}

/// <summary>Immutable user request. Versioned separately from presets and plans.</summary>
public sealed record ConversionRequest
{
    public int RequestVersion { get; init; } = 1;
    public required Guid SourceSnapshotId { get; init; }
    public ConversionMode Mode { get; init; } = ConversionMode.VideoTranscode;
    public ContainerFormat Container { get; init; } = ContainerFormat.Mp4;
    public IReadOnlyList<int> SelectedStreamIndices { get; init; } = Array.Empty<int>();
    public QualityTier Quality { get; init; } = QualityTier.Balanced;
    public SpeedTier Speed { get; init; } = SpeedTier.Balanced;
    public ResizeSpec Resize { get; init; } = ResizeSpec.KeepDisplay();
    public TrimRange? Trim { get; init; }
    public AudioSpec Audio { get; init; } = new();
    public MetadataPolicy Metadata { get; init; } = MetadataPolicy.StripPersonalTags;
    public HardwarePreference Hardware { get; init; } = HardwarePreference.Auto;
    public OutputPolicy Output { get; init; } = new();

    public VideoCodec EffectiveVideoCodec => Mode switch
    {
        ConversionMode.VideoTranscode => VideoCodec.H264,
        ConversionMode.Remux => VideoCodec.Copy,
        _ => VideoCodec.Copy,
    };
}
