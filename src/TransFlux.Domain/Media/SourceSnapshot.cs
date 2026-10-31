namespace TransFlux.Domain.Media;

/// <summary>Cheap input fingerprint used to detect change before and after execution.
/// Not a cryptographic proof of the whole file.</summary>
public sealed record SourceFingerprint
{
    public required string NormalizedPath { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; }
}

public sealed record MediaFormat
{
    public string FormatName { get; init; } = string.Empty;
    public string? FormatLongName { get; init; }
    public long? DurationUs { get; init; }
    public long? BitRate { get; init; }
    public long? SizeBytes { get; init; }
    public int? ChapterCount { get; init; }
}

/// <summary>Immutable snapshot of a probed source media file.</summary>
public sealed record SourceSnapshot
{
    public required Guid MediaId { get; init; }
    public required string AbsolutePath { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; }
    public int ProbeSchemaVersion { get; init; } = 1;
    public IReadOnlyList<StreamDescriptor> Streams { get; init; } = Array.Empty<StreamDescriptor>();
    public MediaFormat Format { get; init; } = new();
    public long? DurationUs { get; init; }
    public ColorClassification ColorClassification { get; init; } = ColorClassification.Unknown;

    public SourceFingerprint Fingerprint => new()
    {
        NormalizedPath = AbsolutePath,
        SizeBytes = SizeBytes,
        ModifiedUtc = ModifiedUtc,
    };

    public IEnumerable<StreamDescriptor> VideoStreams =>
        Streams.Where(s => s.Kind == MediaKind.Video && !s.IsAttachedPicture);

    public IEnumerable<StreamDescriptor> AttachedPictures => Streams.Where(s => s.IsAttachedPicture);

    public IEnumerable<StreamDescriptor> AudioStreams => Streams.Where(s => s.Kind == MediaKind.Audio);

    public IEnumerable<StreamDescriptor> SubtitleStreams => Streams.Where(s => s.Kind == MediaKind.Subtitle);

    public StreamDescriptor? PrimaryVideo => VideoStreams.FirstOrDefault(s => s.IsDefault) ?? VideoStreams.FirstOrDefault();

    public StreamDescriptor? PrimaryAudio => AudioStreams.FirstOrDefault(s => s.IsDefault) ?? AudioStreams.FirstOrDefault();

    public bool HasVideo => VideoStreams.Any();

    public bool HasAudio => AudioStreams.Any();
}
