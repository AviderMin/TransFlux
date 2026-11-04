using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Domain.Tests;

/// <summary>Small builders shared by the Domain rule tests so each test reads as input -> expectation.</summary>
internal static class TestData
{
    public static StreamDescriptor Video(
        int index = 0,
        string codec = "h264",
        int? width = 1920,
        int? height = 1080,
        string? pixelFormat = "yuv420p",
        int? bitDepth = 8,
        bool isDefault = true,
        int? rotationDegrees = null,
        string? colorSpace = "bt709",
        string? colorTransfer = "bt709",
        string? colorPrimaries = "bt709",
        int? dvProfile = null,
        int? dvBlCompat = null,
        ColorClassification? color = null)
        => new()
        {
            SourceIndex = index,
            Kind = MediaKind.Video,
            Codec = codec,
            Width = width,
            Height = height,
            PixelFormat = pixelFormat,
            BitDepth = bitDepth,
            IsDefault = isDefault,
            RotationDegrees = rotationDegrees,
            ColorSpace = colorSpace,
            ColorTransfer = colorTransfer,
            ColorPrimaries = colorPrimaries,
            DvProfile = dvProfile,
            DvBlSignalCompatibilityId = dvBlCompat,
            ColorClassification = color,
        };

    public static StreamDescriptor Audio(
        int index = 1,
        string codec = "aac",
        int? channels = 2,
        string? channelLayout = "stereo",
        int? sampleRate = 48000,
        bool isDefault = true,
        string? language = null,
        bool isForced = false)
        => new()
        {
            SourceIndex = index,
            Kind = MediaKind.Audio,
            Codec = codec,
            Channels = channels,
            ChannelLayout = channelLayout,
            SampleRate = sampleRate,
            IsDefault = isDefault,
            Language = language,
            IsForced = isForced,
        };

    public static StreamDescriptor Subtitle(int index = 2, string codec = "subrip", bool isDefault = false,
        string? language = null, bool isForced = false)
        => new()
        {
            SourceIndex = index,
            Kind = MediaKind.Subtitle,
            Codec = codec,
            IsDefault = isDefault,
            Language = language,
            IsForced = isForced,
        };

    public static SourceSnapshot Source(
        IReadOnlyList<StreamDescriptor> streams,
        ColorClassification color = ColorClassification.SdrBt709,
        long? durationUs = 5_000_000,
        string path = @"C:\media\sample.mp4",
        string formatName = "")
        => new()
        {
            MediaId = Guid.NewGuid(),
            AbsolutePath = path,
            Streams = streams,
            DurationUs = durationUs,
            Format = new MediaFormat { FormatName = formatName, DurationUs = durationUs },
            ColorClassification = color,
        };

    public static ConversionRequest Request(
        ConversionMode mode = ConversionMode.VideoTranscode,
        ContainerFormat container = ContainerFormat.Mp4,
        AudioCodec audioCodec = AudioCodec.Aac,
        TrimRange? trim = null,
        IReadOnlyList<int>? selected = null,
        int requestVersion = 1,
        VideoSelection? video = null,
        AudioSelection? audioTracks = null,
        SubtitleSelection? subtitles = null)
        => new()
        {
            RequestVersion = requestVersion,
            SourceSnapshotId = Guid.NewGuid(),
            Mode = mode,
            Container = container,
            Audio = new AudioSpec { Codec = audioCodec },
            Trim = trim,
            SelectedStreamIndices = selected ?? Array.Empty<int>(),
            VideoSelect = video ?? VideoSelection.Auto,
            AudioTracks = audioTracks ?? AudioSelection.Auto,
            Subtitles = subtitles ?? SubtitleSelection.AutoPreserve,
        };
}
