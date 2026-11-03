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
    /// <summary>
    /// Legacy audio-track index list kept for old drafts and old callers. New requests are
    /// written as RequestVersion=2 and use <see cref="AudioMode"/>/<see cref="AudioStreamIndices"/>;
    /// this property must never be interpreted as a subtitle selection.
    /// </summary>
    public IReadOnlyList<int> SelectedStreams { get; init; } = Array.Empty<int>();
    public AudioSelectionMode AudioMode { get; init; } = AudioSelectionMode.Auto;
    public IReadOnlyList<int> AudioStreamIndices { get; init; } = Array.Empty<int>();
    public SubtitleSelectionMode SubtitleMode { get; init; } = SubtitleSelectionMode.AutoPreserve;
    public IReadOnlyList<int> SubtitleStreamIndices { get; init; } = Array.Empty<int>();
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

    public static ConversionOptions FromRequest(ConversionRequest r)
    {
        var v2 = r.RequestVersion >= 2;
        return new()
        {
            Mode = r.Mode, Container = r.Container, VideoCodec = r.VideoCodec, Quality = r.Quality, Speed = r.Speed,
            ResizeMode = r.Resize.Mode, AllowUpscale = r.Resize.AllowUpscale,
            TargetWidth = r.Resize.TargetWidth, TargetHeight = r.Resize.TargetHeight,
            AudioCodec = r.Audio.Codec, AudioBitRateKbps = r.Audio.BitRateKbps,
            AudioSampleRate = r.Audio.SampleRate, AudioChannels = r.Audio.Channels,
            MetadataPolicy = r.Metadata, Hardware = r.Hardware.Backend,
            // Old v1 tasks remain displayable: their list only ever meant audio-ish source
            // indices for the UI. Subtitle intent is deliberately left at AutoPreserve so an
            // audio index is never reused as a subtitle index during migration.
            SelectedStreams = v2
                ? (r.AudioTracks.Mode == AudioSelectionMode.Explicit ? r.AudioTracks.SourceStreamIndices : Array.Empty<int>())
                : r.SelectedStreamIndices,
            AudioMode = v2 ? r.AudioTracks.Mode
                : r.SelectedStreamIndices.Count > 0 ? AudioSelectionMode.Explicit : AudioSelectionMode.Auto,
            AudioStreamIndices = v2 ? r.AudioTracks.SourceStreamIndices : r.SelectedStreamIndices,
            SubtitleMode = v2 ? r.Subtitles.Mode : SubtitleSelectionMode.AutoPreserve,
            SubtitleStreamIndices = v2 ? r.Subtitles.SourceStreamIndices : Array.Empty<int>(),
            OutputDirectory = r.Output.OutputDirectory,
            OutputFileName = r.Output.FileNameOverride, TrimStart = r.Trim?.Start, TrimEnd = r.Trim?.End,
            CrfOverride = r.CrfOverride, VideoBitrateKbps = r.VideoBitrateKbps, FrameRateOverride = r.FrameRateOverride,
        };
    }

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
        AudioMode = AudioMode,
        AudioStreamIndices = AudioStreamIndices,
        SubtitleMode = SubtitleMode,
        SubtitleStreamIndices = SubtitleStreamIndices,
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

    public ConversionRequest ToRequest(Guid mediaId, string? outputDirectory = null)
    {
        // Migrate the legacy SelectedStreams draft field to the v2 audio selection without
        // ever feeding it into the subtitle policy.
        var audioMode = AudioMode;
        var audioIndices = AudioStreamIndices;
        if (audioMode == AudioSelectionMode.Auto && audioIndices.Count == 0 && SelectedStreams.Count > 0)
        {
            audioMode = AudioSelectionMode.Explicit;
            audioIndices = SelectedStreams;
        }
        else if (audioMode == AudioSelectionMode.Explicit && audioIndices.Count == 0 && SelectedStreams.Count > 0)
        {
            audioIndices = SelectedStreams;
        }

        return new()
        {
            RequestVersion = 2,
            SourceSnapshotId = mediaId,
            Mode = Mode,
            Container = Container,
            VideoCodec = VideoCodec,
            // New requests never write the legacy combined list; all selection intent is explicit.
            SelectedStreamIndices = Array.Empty<int>(),
            VideoSelect = VideoSelection.Auto,
            AudioTracks = new AudioSelection
            {
                Mode = audioMode,
                SourceStreamIndices = audioMode == AudioSelectionMode.Explicit ? audioIndices : Array.Empty<int>(),
            },
            Subtitles = new SubtitleSelection
            {
                Mode = SubtitleMode,
                SourceStreamIndices = SubtitleMode == SubtitleSelectionMode.Explicit ? SubtitleStreamIndices : Array.Empty<int>(),
            },
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
}
