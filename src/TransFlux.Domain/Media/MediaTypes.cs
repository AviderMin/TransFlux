namespace TransFlux.Domain.Media;

public enum MediaKind
{
    Unknown = 0,
    Video = 1,
    Audio = 2,
    Subtitle = 3,
    Attachment = 4,
    Data = 5,
}

/// <summary>Colour classification derived from transfer/bit-depth/side data.
/// Unknown or conflicting values must never be treated as SDR.</summary>
public enum ColorClassification
{
    Unknown = 0,
    SdrBt709 = 1,
    SdrBt601 = 2,
    SdrOther = 3,
    Hdr10 = 4,
    Hdr10Plus = 5,
    DolbyVision = 6,
    WideGamut = 7,
    Conflicting = 8,
}
