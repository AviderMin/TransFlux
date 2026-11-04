using System.Text.Json.Serialization;
using TransFlux.Domain.Common;
using TransFlux.Domain.Media;

namespace TransFlux.Domain.Plans;

public enum ConversionMode { VideoTranscode = 0, AudioExtract = 1, Remux = 2 }
public enum ContainerFormat { Mp4 = 0, Mkv = 1, M4a = 2, Flac = 3, Wav = 4, Mov = 5, Webm = 6, Mp3 = 7, Ogg = 8, Opus = 9, Aac = 10 }
public enum VideoCodec { H264 = 0, Hevc = 1, Av1 = 2, Copy = 3, Vp9 = 4 }
public enum AudioCodec { Aac = 0, Flac = 1, PcmS16le = 2, Copy = 3, Mp3 = 4, Opus = 5, Vorbis = 6, Alac = 7, PcmS24le = 8 }
public enum QualityTier { SmallerSize = 0, Balanced = 1, HigherQuality = 2 }
public enum SpeedTier { Faster = 0, Balanced = 1, Slower = 2 }
public enum ResizeMode { KeepDisplaySize = 0, Max1080p = 1 }
public enum MetadataPolicy { StripPersonalTags = 0, PreserveTitleAndChapters = 1 }
public enum HardwareBackend { Auto = 0, Cpu = 1, NvidiaNvenc = 2, IntelQsv = 3, AmdAmf = 4 }
public enum VideoSelectionMode { Auto = 0, Explicit = 1 }
public enum AudioSelectionMode { Auto = 0, None = 1, Explicit = 2 }
public enum SubtitleSelectionMode { AutoPreserve = 0, None = 1, Explicit = 2 }

public static class ContainerFormats
{
    public static string Extension(this ContainerFormat c) => c switch
    {
        ContainerFormat.Mp4 => ".mp4",
        ContainerFormat.Mkv => ".mkv",
        ContainerFormat.M4a => ".m4a",
        ContainerFormat.Flac => ".flac",
        ContainerFormat.Wav => ".wav",
        ContainerFormat.Mov => ".mov",
        ContainerFormat.Webm => ".webm",
        ContainerFormat.Mp3 => ".mp3",
        ContainerFormat.Ogg => ".ogg",
        ContainerFormat.Opus => ".opus",
        ContainerFormat.Aac => ".aac",
        _ => throw new DomainException($"Unknown container {c}"),
    };

    public static string DisplayName(this ContainerFormat c) => c switch
    {
        ContainerFormat.Mp4 => "MP4",
        ContainerFormat.Mkv => "MKV",
        ContainerFormat.M4a => "M4A",
        ContainerFormat.Flac => "FLAC",
        ContainerFormat.Wav => "WAV",
        ContainerFormat.Mov => "MOV",
        ContainerFormat.Webm => "WebM",
        ContainerFormat.Mp3 => "MP3",
        ContainerFormat.Ogg => "OGG",
        ContainerFormat.Opus => "Opus",
        ContainerFormat.Aac => "AAC / ADTS",
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
    public int? TargetWidth { get; init; }
    public int? TargetHeight { get; init; }
    public static ResizeSpec KeepDisplay() => new() { Mode = ResizeMode.KeepDisplaySize, AllowUpscale = false };
    public static ResizeSpec Max1080p() => new() { Mode = ResizeMode.Max1080p, AllowUpscale = false };
}

public sealed record AudioSpec
{
    public AudioCodec Codec { get; init; } = AudioCodec.Aac;
    public int? BitRateKbps { get; init; } = 192;
    /// <summary>
    /// Preserve the source channel layout. Honored implicitly: the planner only emits
    /// -ac when <see cref="Channels"/> is explicitly set; with Channels null the source
    /// layout flows through untouched. Kept as an explicit user-facing commitment.
    /// </summary>
    public bool KeepChannelLayout { get; init; } = true;
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
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

/// <summary>
/// Explicit single-video selection for RequestVersion 2. Auto resolves to the source's
/// primary video stream; Explicit pins exactly one source stream index.
/// </summary>
public sealed record VideoSelection
{
    public VideoSelectionMode Mode { get; init; } = VideoSelectionMode.Auto;
    public int? SourceStreamIndex { get; init; }

    public static VideoSelection Auto => new();
    public static VideoSelection Stream(int sourceStreamIndex) => new()
    {
        Mode = VideoSelectionMode.Explicit,
        SourceStreamIndex = sourceStreamIndex,
    };
}

/// <summary>
/// RequestVersion 2 audio policy. Auto keeps every source audio track; None deliberately
/// produces no audio; Explicit keeps exactly the listed source audio stream indices.
/// </summary>
public sealed record AudioSelection
{
    public AudioSelectionMode Mode { get; init; } = AudioSelectionMode.Auto;
    public IReadOnlyList<int> SourceStreamIndices { get; init; } = Array.Empty<int>();

    public static AudioSelection Auto => new();
    public static AudioSelection None => new() { Mode = AudioSelectionMode.None };
    public static AudioSelection Tracks(params int[] sourceStreamIndices) => new()
    {
        Mode = AudioSelectionMode.Explicit,
        SourceStreamIndices = sourceStreamIndices,
    };
}

/// <summary>
/// RequestVersion 2 subtitle policy. AutoPreserve keeps every source subtitle the target
/// container can carry; None deliberately emits no subtitles; Explicit keeps exactly the
/// listed source subtitle stream indices. Burn-in is intentionally not part of this model.
/// </summary>
public sealed record SubtitleSelection
{
    public SubtitleSelectionMode Mode { get; init; } = SubtitleSelectionMode.AutoPreserve;
    public IReadOnlyList<int> SourceStreamIndices { get; init; } = Array.Empty<int>();

    public static SubtitleSelection AutoPreserve => new();
    public static SubtitleSelection None => new() { Mode = SubtitleSelectionMode.None };
    public static SubtitleSelection Tracks(params int[] sourceStreamIndices) => new()
    {
        Mode = SubtitleSelectionMode.Explicit,
        SourceStreamIndices = sourceStreamIndices,
    };
}

/// <summary>Immutable user request. Versioned separately from presets and plans.</summary>
public sealed record ConversionRequest
{
    public ConversionRequest()
    {
    }

    /// <summary>
    /// JSON deserialization path. A request persisted by a legacy build has no
    /// <c>requestVersion</c> member at all; treating a missing member as version 1 keeps its
    /// SelectedStreamIndices meaning intact. The parameterless constructor above still defaults
    /// newly created requests to version 2.
    /// </summary>
    [JsonConstructor]
    public ConversionRequest(int requestVersion = 1)
    {
        RequestVersion = requestVersion;
    }

    /// <summary>
    /// Request schema version. Version 1 is the legacy model where SelectedStreamIndices
    /// filters video, audio and subtitles together; version 2 uses the explicit selections
    /// below and ignores SelectedStreamIndices. New requests default to version 2; only
    /// persisted legacy requests may carry version 1.
    /// </summary>
    public int RequestVersion { get; init; } = 2;
    public required Guid SourceSnapshotId { get; init; }
    public ConversionMode Mode { get; init; } = ConversionMode.VideoTranscode;
    public ContainerFormat Container { get; init; } = ContainerFormat.Mp4;
    public VideoCodec VideoCodec { get; init; } = VideoCodec.H264;
    /// <summary>Legacy request-v1 selection; kept verbatim so old snapshots keep their meaning.</summary>
    public IReadOnlyList<int> SelectedStreamIndices { get; init; } = Array.Empty<int>();
    public VideoSelection VideoSelect { get; init; } = new();
    public AudioSelection AudioTracks { get; init; } = new();
    public SubtitleSelection Subtitles { get; init; } = new();
    public QualityTier Quality { get; init; } = QualityTier.Balanced;
    public SpeedTier Speed { get; init; } = SpeedTier.Balanced;
    public ResizeSpec Resize { get; init; } = ResizeSpec.KeepDisplay();
    public TrimRange? Trim { get; init; }
    public AudioSpec Audio { get; init; } = new();
    public MetadataPolicy Metadata { get; init; } = MetadataPolicy.StripPersonalTags;
    public HardwarePreference Hardware { get; init; } = HardwarePreference.Auto;
    public OutputPolicy Output { get; init; } = new();
    // Explicit user overrides (null = derive from tier / source).
    public int? CrfOverride { get; init; }
    public int? VideoBitrateKbps { get; init; }
    public double? FrameRateOverride { get; init; }

    public VideoCodec EffectiveVideoCodec => Mode switch
    {
        ConversionMode.VideoTranscode => VideoCodec,
        ConversionMode.Remux => VideoCodec.Copy,
        _ => VideoCodec.Copy,
    };
}
