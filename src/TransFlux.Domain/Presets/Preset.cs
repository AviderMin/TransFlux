using TransFlux.Domain.Plans;

namespace TransFlux.Domain.Presets;

/// <summary>Portable preset: describes intent, never machine paths or device LUIDs.</summary>
public sealed record PresetRequestTemplate
{
    public ConversionMode Mode { get; init; } = ConversionMode.VideoTranscode;
    public ContainerFormat Container { get; init; } = ContainerFormat.Mp4;
    public QualityTier Quality { get; init; } = QualityTier.Balanced;
    public SpeedTier Speed { get; init; } = SpeedTier.Balanced;
    public ResizeMode ResizeMode { get; init; } = ResizeMode.KeepDisplaySize;
    public bool AllowUpscale { get; init; }
    public AudioCodec AudioCodec { get; init; } = AudioCodec.Aac;
    public int? AudioBitRateKbps { get; init; } = 192;
    public MetadataPolicy MetadataPolicy { get; init; } = MetadataPolicy.StripPersonalTags;
}

public sealed record Preset
{
    public required Guid PresetId { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; } = 1;
    public required string Name { get; init; }
    public bool BuiltIn { get; init; }
    public required PresetRequestTemplate Template { get; init; }
}
