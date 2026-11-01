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

    // HDR static metadata (structured from mastering-display / content-light side data).
    // MasterDisplay uses the x265 -master-display syntax: G(x,y)B(x,y)R(x,y)WP(x,y)L(max,min).
    public string? MasterDisplay { get; init; }
    /// <summary>MaxCLL in cd/m2 (content light level max_content).</summary>
    public int? MaxCLL { get; init; }
    /// <summary>MaxFALL in cd/m2 (content light level max_average).</summary>
    public int? MaxFALL { get; init; }

    // Dolby Vision configuration record (dvhe/dvh1 streams).
    /// <summary>DV profile (5/7/8...), null when no DOVI configuration record was found.</summary>
    public int? DvProfile { get; init; }
    /// <summary>BL signal compatibility id (1=HDR10, 2=SDR, 4=HLG for profile 8); 0/absent = no compatible base layer.</summary>
    public int? DvBlSignalCompatibilityId { get; init; }

    // Audio-specific
    public int? Channels { get; init; }
    public string? ChannelLayout { get; init; }
    public int? SampleRate { get; init; }

    // Generic
    public long? BitRate { get; init; }
    public long? DurationUs { get; init; }
    public long? StartUs { get; init; }
    public Rational? TimeBase { get; init; }
    public string? SideDataSummary { get; init; }

    // Attachment-specific (font/data streams; filename+mimetype are structural for Matroska).
    public string? AttachmentFileName { get; init; }
    public string? AttachmentMimeType { get; init; }

    public bool IsTextSubtitle => Kind == MediaKind.Subtitle &&
        (Codec is "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text" or "text");

    public bool IsBitmapSubtitle => Kind == MediaKind.Subtitle && !IsTextSubtitle;
}
