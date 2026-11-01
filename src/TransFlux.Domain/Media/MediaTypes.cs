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
/// Streams with no explicit colour tags are classified by a documented heuristic
/// (plain 8-bit 4:2:0 is treated as SDR BT.709); genuinely unknown or conflicting
/// values are never forced into an SDR class.</summary>
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
    /// <summary>HLG (ARIB STD-B67) transfer. HDR; only stream-copy remux is allowed.</summary>
    Hlg = 9,
}
