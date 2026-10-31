using TransFlux.Domain.Common;

namespace TransFlux.Domain.Media;

/// <summary>Normalized description of a single media stream from ffprobe.</summary>
public sealed record StreamDescriptor
{
    public required int SourceIndex { get; init; }
    public MediaKind Kind { get; init; } = MediaKind.Unknown;
    public string Codec { get; init; } = string.Empty;
    public string? CodecLongName { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public bool IsAttachedPicture { get; init; }

    // Video-specific
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? RotationDegrees { get; init; }
    public string? PixelFormat { get; init; }
    public int? BitDepth { get; init; }
    public Rational? SampleAspectRatio { get; init; }
    public Rational? DisplayAspectRatio { get; init; }
    public Rational? FrameRate { get; init; }
    public Rational? AverageFrameRate { get; init; }
    public string? ColorRange { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorTransfer { get; init; }
    public string? ColorPrimaries { get; init; }
    public bool HasHdrSideData { get; init; }

    // Audio-specific
    public int? Channels { get; init; }
    public string? ChannelLayout { get; init; }
    public int? SampleRate { get; init; }

    // Generic
    public long? BitRate { get; init; }
    public long? DurationUs { get; init; }
    public string? SideDataSummary { get; init; }

    public bool IsTextSubtitle => Kind == MediaKind.Subtitle &&
        (Codec is "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text" or "text");

    public bool IsBitmapSubtitle => Kind == MediaKind.Subtitle && !IsTextSubtitle;
}
