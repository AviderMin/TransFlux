using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;

namespace TransFlux.App.State;

/// <summary>UI-facing conversion settings. Pure data; converted to a
/// <see cref="ConversionRequest"/> only at enqueue time.</summary>
public sealed record ConversionOptions
{
    public ConversionMode Mode { get; init; } = ConversionMode.VideoTranscode;
    public ContainerFormat Container { get; init; } = ContainerFormat.Mp4;
    public VideoCodec VideoCodec { get; init; } = VideoCodec.H264;
    public QualityTier Quality { get; init; } = QualityTier.Balanced;
    public SpeedTier Speed { get; init; } = SpeedTier.Balanced;
    public ResizeMode ResizeMode { get; init; } = ResizeMode.KeepDisplaySize;
    public bool AllowUpscale { get; init; }
    public AudioCodec AudioCodec { get; init; } = AudioCodec.Aac;
    public int? AudioBitRateKbps { get; init; } = 192;
    public MetadataPolicy MetadataPolicy { get; init; } = MetadataPolicy.StripPersonalTags;
    public HardwareBackend Hardware { get; init; } = HardwareBackend.Auto;
    public IReadOnlyList<int> SelectedStreams { get; init; } = Array.Empty<int>();
    public string? OutputDirectory { get; init; }
    public TimeSpan? TrimStart { get; init; }
    public TimeSpan? TrimEnd { get; init; }
    public int? TargetWidth { get; init; }
    public int? TargetHeight { get; init; }
    public int? CrfOverride { get; init; }
    public int? VideoBitrateKbps { get; init; }
    public double? FrameRateOverride { get; init; }
    public int? AudioSampleRate { get; init; }
    public int? AudioChannels { get; init; }
    public string? OutputFileName { get; init; }

    public static readonly ConversionOptions Default = new();

    public static ConversionOptions FromRequest(ConversionRequest r) => new()
    {
        Mode = r.Mode, Container = r.Container, VideoCodec = r.VideoCodec, Quality = r.Quality, Speed = r.Speed,
        ResizeMode = r.Resize.Mode, AllowUpscale = r.Resize.AllowUpscale,
        TargetWidth = r.Resize.TargetWidth, TargetHeight = r.Resize.TargetHeight,
        AudioCodec = r.Audio.Codec, AudioBitRateKbps = r.Audio.BitRateKbps,
        AudioSampleRate = r.Audio.SampleRate, AudioChannels = r.Audio.Channels,
        MetadataPolicy = r.Metadata, Hardware = r.Hardware.Backend,
        SelectedStreams = r.SelectedStreamIndices, OutputDirectory = r.Output.OutputDirectory,
        OutputFileName = r.Output.FileNameOverride, TrimStart = r.Trim?.Start, TrimEnd = r.Trim?.End,
        CrfOverride = r.CrfOverride, VideoBitrateKbps = r.VideoBitrateKbps, FrameRateOverride = r.FrameRateOverride,
    };

    public static ConversionOptions FromTemplate(PresetRequestTemplate t) => new()
    {
        Mode = t.Mode,
        Container = t.Container,
        VideoCodec = t.VideoCodec,
        Quality = t.Quality,
        Speed = t.Speed,
        ResizeMode = t.ResizeMode,
        AllowUpscale = t.AllowUpscale,
        AudioCodec = t.AudioCodec,
        AudioBitRateKbps = t.AudioBitRateKbps,
        MetadataPolicy = t.MetadataPolicy,
        TargetWidth = t.TargetWidth, TargetHeight = t.TargetHeight,
        CrfOverride = t.CrfOverride, VideoBitrateKbps = t.VideoBitrateKbps, FrameRateOverride = t.FrameRateOverride,
        AudioSampleRate = t.AudioSampleRate, AudioChannels = t.AudioChannels, Hardware = t.Hardware,
    };

    /// <summary>Applies reusable parameters while retaining source-specific edits.</summary>
    public ConversionOptions ApplyPresetTemplate(PresetRequestTemplate t) => FromTemplate(t) with
    {
        OutputDirectory = OutputDirectory,
        OutputFileName = OutputFileName,
        TrimStart = TrimStart,
        TrimEnd = TrimEnd,
        SelectedStreams = SelectedStreams,
    };

    public PresetRequestTemplate ToTemplate() => new()
    {
        Mode = Mode, Container = Container, VideoCodec = VideoCodec, Quality = Quality, Speed = Speed, ResizeMode = ResizeMode,
        AllowUpscale = AllowUpscale, AudioCodec = AudioCodec, AudioBitRateKbps = AudioBitRateKbps,
        MetadataPolicy = MetadataPolicy, TargetWidth = TargetWidth, TargetHeight = TargetHeight,
        CrfOverride = CrfOverride, VideoBitrateKbps = VideoBitrateKbps, FrameRateOverride = FrameRateOverride,
        AudioSampleRate = AudioSampleRate, AudioChannels = AudioChannels, Hardware = Hardware,
    };

    /// <summary>Builds a domain trim range, or null when the inputs are incomplete/inverted.</summary>
    public TrimRange? BuildTrim()
    {
        if (TrimStart is TimeSpan s && TrimEnd is TimeSpan e && e > s)
            return new TrimRange { Start = s, End = e };
        return null;
    }

    public ConversionRequest ToRequest(Guid mediaId, string? outputDirectory = null) => new()
    {
        SourceSnapshotId = mediaId,
        Mode = Mode,
        Container = Container,
        VideoCodec = VideoCodec,
        SelectedStreamIndices = SelectedStreams,
        Quality = Quality,
        Speed = Speed,
        Resize = new ResizeSpec { Mode = ResizeMode, AllowUpscale = AllowUpscale, TargetWidth = TargetWidth, TargetHeight = TargetHeight },
        Trim = BuildTrim(),
        Audio = new AudioSpec { Codec = AudioCodec, BitRateKbps = AudioBitRateKbps, KeepChannelLayout = true, SampleRate = AudioSampleRate, Channels = AudioChannels },
        Metadata = MetadataPolicy,
        Hardware = new HardwarePreference { Backend = Hardware },
        Output = new OutputPolicy { OutputDirectory = outputDirectory ?? OutputDirectory, FileNameOverride = OutputFileName },
        CrfOverride = CrfOverride,
        VideoBitrateKbps = VideoBitrateKbps,
        FrameRateOverride = FrameRateOverride,
    };
}
