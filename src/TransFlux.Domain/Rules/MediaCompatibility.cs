using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Domain.Rules;

public sealed record CompatibilityIssue(string Code, string Message, bool Blocking)
{
    public static CompatibilityIssue Block(string code, string msg) => new(code, msg, true);
    public static CompatibilityIssue Warn(string code, string msg) => new(code, msg, false);
}

/// <summary>First-release compatibility matrix (docs/05). Returns blocking/warn issues.</summary>
public static class MediaCompatibility
{
    public static IReadOnlyList<CompatibilityIssue> Validate(ConversionRequest request, SourceSnapshot source)
    {
        var issues = new List<CompatibilityIssue>();
        switch (request.Mode)
        {
            case ConversionMode.VideoTranscode:
                ValidateVideoTranscode(request, source, issues);
                break;
            case ConversionMode.AudioExtract:
                ValidateAudioExtract(request, source, issues);
                break;
            case ConversionMode.Remux:
                ValidateRemux(request, source, issues);
                break;
        }
        return issues;
    }

    private static void ValidateVideoTranscode(ConversionRequest r, SourceSnapshot s, List<CompatibilityIssue> issues)
    {
        if (!s.HasVideo)
            issues.Add(CompatibilityIssue.Block("NO_VIDEO", "Source has no video stream to transcode."));
        if (r.Container is not (ContainerFormat.Mp4 or ContainerFormat.Mkv))
            issues.Add(CompatibilityIssue.Block("BAD_CONTAINER", "Video transcode supports MP4 or MKV only."));
        if (s.ColorClassification is not ColorClassification.SdrBt709)
            issues.Add(CompatibilityIssue.Block("NON_SDR",
                "Only verified SDR BT.709 video may be transcoded. Use Remux for compatible HDR sources."));
        var v = SelectPrimaryVideo(r, s);
        if (v is { BitDepth: > 8 })
            issues.Add(CompatibilityIssue.Block("HIGH_BIT_DEPTH", "First release transcodes only 8-bit video."));
        if (v is { PixelFormat: not null } pf && !IsYuv420p8(pf.PixelFormat!))
            issues.Add(CompatibilityIssue.Block("PIX_FMT", $"Unsupported pixel format {pf.PixelFormat}."));
        if (r.Trim is { } trim && s.DurationUs is long durUs)
        {
            var duration = TimeSpan.FromMicroseconds(durUs);
            if (trim.End > duration || trim.Start < TimeSpan.Zero)
                issues.Add(CompatibilityIssue.Block("TRIM_RANGE", "Trim range is outside the source duration."));
        }
    }

    private static void ValidateAudioExtract(ConversionRequest r, SourceSnapshot s, List<CompatibilityIssue> issues)
    {
        if (!s.HasAudio)
            issues.Add(CompatibilityIssue.Block("NO_AUDIO", "Source has no audio stream to extract."));
        var expected = r.Container switch
        {
            ContainerFormat.M4a => AudioCodec.Aac,
            ContainerFormat.Flac => AudioCodec.Flac,
            ContainerFormat.Wav => AudioCodec.PcmS16le,
            _ => (AudioCodec)(-1),
        };
        if ((int)expected < 0)
            issues.Add(CompatibilityIssue.Block("BAD_CONTAINER", "Audio export supports M4A, FLAC or WAV."));
        else if (r.Audio.Codec != expected)
            issues.Add(CompatibilityIssue.Block("CODEC_CONTAINER", "Audio codec does not match container."));
        if (r.Audio.Codec == AudioCodec.Flac)
        {
            var a = SelectPrimaryAudio(r, s);
            if (a is { SampleRate: > 192000 })
                issues.Add(CompatibilityIssue.Block("SAMPLE_RATE", "FLAC source sample rate exceeds support."));
        }
    }

    private static void ValidateRemux(ConversionRequest r, SourceSnapshot s, List<CompatibilityIssue> issues)
    {
        if (r.Trim is not null)
            issues.Add(CompatibilityIssue.Block("TRIM_REMUX", "Trim is disabled for remux in the first release."));
        if (r.Container is not (ContainerFormat.Mp4 or ContainerFormat.Mkv))
            issues.Add(CompatibilityIssue.Block("BAD_CONTAINER", "Remux supports MP4 or MKV only."));
        if (!s.HasVideo && !s.HasAudio)
            issues.Add(CompatibilityIssue.Block("EMPTY", "Source has no audio or video to copy."));

        foreach (var v in s.VideoStreams)
        {
            bool ok = v.Codec is "h264";
            if (r.Container == ContainerFormat.Mkv)
                ok = v.Codec is "h264" or "hevc";
            if (!ok)
                issues.Add(CompatibilityIssue.Block("REMUX_VIDEO",
                    $"Video codec {v.Codec} cannot be remuxed to {r.Container.DisplayName()}."));
            if (v.BitDepth is > 8 && r.Container == ContainerFormat.Mp4)
                issues.Add(CompatibilityIssue.Block("REMUX_DEPTH", "MP4 remux supports only 8-bit video."));
        }
        foreach (var a in s.AudioStreams)
        {
            bool ok = a.Codec is "aac" or "flac" or "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "mp3";
            if (r.Container == ContainerFormat.Mp4)
                ok = a.Codec is "aac" or "mp3";
            if (!ok)
                issues.Add(CompatibilityIssue.Block("REMUX_AUDIO",
                    $"Audio codec {a.Codec} cannot be remuxed to {r.Container.DisplayName()}."));
        }
        foreach (var sub in s.SubtitleStreams)
        {
            if (sub.IsBitmapSubtitle)
                issues.Add(CompatibilityIssue.Block("REMUX_SUBTITLE_BITMAP",
                    "Bitmap subtitles are not supported in the first release."));
            else if (r.Container == ContainerFormat.Mp4 && sub.Codec is not ("mov_text" or "subrip" or "srt"))
                issues.Add(CompatibilityIssue.Block("REMUX_SUBTITLE",
                    $"Subtitle codec {sub.Codec} cannot be remuxed to MP4."));
        }
        if (s.ColorClassification is ColorClassification.DolbyVision)
            issues.Add(CompatibilityIssue.Block("REMUX_DV", "Dolby Vision remux is blocked in the first release."));
    }

    public static StreamDescriptor? SelectPrimaryVideo(ConversionRequest r, SourceSnapshot s)
    {
        foreach (var idx in r.SelectedStreamIndices)
        {
            var st = s.Streams.FirstOrDefault(x => x.SourceIndex == idx);
            if (st is { Kind: MediaKind.Video, IsAttachedPicture: false }) return st;
        }
        return s.PrimaryVideo;
    }

    public static StreamDescriptor? SelectPrimaryAudio(ConversionRequest r, SourceSnapshot s)
    {
        foreach (var idx in r.SelectedStreamIndices)
        {
            var st = s.Streams.FirstOrDefault(x => x.SourceIndex == idx);
            if (st is { Kind: MediaKind.Audio }) return st;
        }
        return s.PrimaryAudio;
    }

    private static bool IsYuv420p8(string pixFmt) =>
        pixFmt is "yuv420p" or "yuvj420p" or "nv12";
}
