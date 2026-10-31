using TransFlux.Domain.Media;

namespace TransFlux.Engine.FFmpeg.Probe;

/// <summary>
/// Classifies a video stream's colour handling from probe fields. The first release only
/// transcodes verified SDR BT.709 8-bit 4:2:0. Anything HDR (PQ/HLG), Dolby Vision, 10-bit,
/// wide-gamut (BT.2020) or ambiguous is classified as non-SDR so the domain compatibility
/// matrix blocks transcoding. Unknown is never treated as SDR.
/// </summary>
public static class ColorClassifier
{
    public static ColorClassification Classify(StreamDescriptor? video)
    {
        if (video is null) return ColorClassification.Unknown;

        var pix = (video.PixelFormat ?? string.Empty).ToLowerInvariant();
        var transfer = Normalize(video.ColorTransfer);
        var primaries = Normalize(video.ColorPrimaries);
        var space = Normalize(video.ColorSpace);
        var side = (video.SideDataSummary ?? string.Empty).ToLowerInvariant();
        var bitDepth = video.BitDepth ?? InferBitDepth(pix);
        var hasHdrSide = video.HasHdrSideData;

        // Dolby Vision: primary DV side data or smpte432 primaries.
        if (side.Contains("dolby vision") || side.Contains("dovi") || side.Contains("dv configuration")
            || primaries == "smpte432")
            return ColorClassification.DolbyVision;

        // HDR10+ dynamic metadata.
        if (side.Contains("hdr10+") || side.Contains("hdr dynamic metadata")
            || side.Contains("dynamic metadata") || side.Contains("hdr10plus"))
            return ColorClassification.Hdr10Plus;

        var isPq = transfer is "smpte2084" or "smpte-st-2084" or "pq";
        var isHlg = transfer is "arib-std-b67" or "arib_std_b67" or "hlg";
        var isBt2020 = primaries == "bt2020" || space is "bt2020nc" or "bt2020c" or "bt2020";

        if (isPq) return ColorClassification.Hdr10;
        if (isHlg || isBt2020) return ColorClassification.WideGamut;
        if (hasHdrSide) return ColorClassification.Hdr10;

        // Explicit 10-bit (or higher) SDR is still blocked from the transcode path.
        if (bitDepth > 8) return ColorClassification.WideGamut;

        var is709 = transfer is "bt709" || primaries == "bt709" || space == "bt709";
        var is601 = transfer is "smpte170m" or "bt470bg" or "bt470m"
            || primaries is "smpte170m" or "bt470bg" or "bt470m"
            || space is "smpte170m" or "bt470bg";
        var anyExplicit = transfer is not null || primaries is not null || space is not null;

        // Conflict: explicit BT.709 and a BT.601 signal mixed.
        if (is709 && is601) return ColorClassification.Conflicting;

        if (is601) return ColorClassification.SdrBt601;

        // Plain 8-bit 4:2:0 with no explicit non-709 tags (and no HDR side data) is SDR BT.709.
        if (transfer is null && primaries is null && space is null && IsPlain420(pix))
            return ColorClassification.SdrBt709;

        if (is709) return ColorClassification.SdrBt709;

        // 8-bit but non-420 pixel format with no colour tags, or tags we cannot confidently map.
        return anyExplicit ? ColorClassification.Conflicting : ColorClassification.SdrOther;
    }

    private static bool IsPlain420(string pix) => pix is "yuv420p" or "nv12" or "nv21";

    internal static int InferBitDepth(string pix)
    {
        if (string.IsNullOrEmpty(pix)) return 8;
        if (pix.EndsWith("16le") || pix.EndsWith("16be") || pix.Contains("p016")) return 16;
        if (pix.EndsWith("12le") || pix.EndsWith("12be") || pix.Contains("p012")) return 12;
        if (pix.EndsWith("10le") || pix.EndsWith("10be") || pix.Contains("p010") || pix.EndsWith("10")) return 10;
        if (pix.EndsWith("9le") || pix.EndsWith("9be")) return 9;
        return 8;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToLowerInvariant();
        return v is "unknown" or "n/a" or "unspecified" or "reserved" ? null : v;
    }
}
