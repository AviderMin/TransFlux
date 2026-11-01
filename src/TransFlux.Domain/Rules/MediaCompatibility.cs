using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Domain.Rules;

public sealed record CompatibilityIssue(string Code, string Message, bool Blocking)
{
    public static CompatibilityIssue Block(string code, string msg) => new(code, msg, true);
    public static CompatibilityIssue Warn(string code, string msg) => new(code, msg, false);
}

/// <summary>Output compatibility plus source colour/stream constraints (docs/05).</summary>
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
        if (request.Mode != ConversionMode.Remux && request.Audio.Codec == AudioCodec.Opus)
        {
            if (request.Audio.SampleRate is int rate && rate is not (8000 or 12000 or 16000 or 24000 or 48000))
                issues.Add(CompatibilityIssue.Block("AUDIO_RATE", "Opus 支持 8000、12000、16000、24000、48000 Hz，请选择受支持的采样率或留空自动。"));
            if (request.Audio.BitRateKbps is > 510)
                issues.Add(CompatibilityIssue.Block("AUDIO_BITRATE", "Opus 码率不得超过 510 kbps。"));
        }
        if (request.Mode != ConversionMode.Remux && request.Audio.Codec == AudioCodec.Mp3
            && (request.Audio.Channels ?? SelectPrimaryAudio(request, source)?.Channels) is > 2)
            issues.Add(CompatibilityIssue.Block("AUDIO_CHANNELS", "MP3 仅支持单声道或立体声，请将声道数改为 1 或 2。"));
        if (request.Mode != ConversionMode.Remux && request.Audio.Codec == AudioCodec.Mp3 && request.Audio.BitRateKbps is > 320)
            issues.Add(CompatibilityIssue.Block("AUDIO_BITRATE", "MP3 码率不得超过 320 kbps。"));
        if (request.Mode != ConversionMode.Remux && request.Audio.Codec == AudioCodec.Mp3
            && request.Audio.SampleRate is not (null or 8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000))
            issues.Add(CompatibilityIssue.Block("AUDIO_RATE", "MP3 不支持该采样率，请改为 44100/48000 Hz 或留空自动。"));
        return issues;
    }

    private static void ValidateVideoTranscode(ConversionRequest r, SourceSnapshot s, List<CompatibilityIssue> issues)
    {
        if (!s.HasVideo)
            issues.Add(CompatibilityIssue.Block("NO_VIDEO", "Source has no video stream to transcode."));
        if (!OutputFormatCatalog.VideoContainers.Contains(r.Container))
            issues.Add(CompatibilityIssue.Block("BAD_CONTAINER", "Choose a video container."));
        else if (!OutputFormatCatalog.VideoCodecs(r.Container).Contains(r.VideoCodec)
            || !OutputFormatCatalog.AudioCodecs(r.Container).Contains(r.Audio.Codec))
            issues.Add(CompatibilityIssue.Block("CODEC_CONTAINER", "Selected codecs do not match the video container."));
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
        if (!OutputFormatCatalog.AudioContainers.Contains(r.Container))
            issues.Add(CompatibilityIssue.Block("BAD_CONTAINER", "Choose an audio container."));
        else if (!OutputFormatCatalog.AudioCodecs(r.Container).Contains(r.Audio.Codec))
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
        if (!OutputFormatCatalog.VideoContainers.Contains(r.Container))
            issues.Add(CompatibilityIssue.Block("BAD_CONTAINER", "Choose a video container for remux."));

        var selected = SelectRemuxStreams(r, s);
        if (selected.Count == 0)
            issues.Add(CompatibilityIssue.Block("EMPTY", "Source has no selected audio or video to copy."));

        foreach (var v in selected.Where(st => st.Kind == MediaKind.Video))
        {
            bool ok = OutputFormatCatalog.RemuxVideoCodecs(r.Container).Contains(v.Codec);
            if (!ok)
                issues.Add(CompatibilityIssue.Block("REMUX_VIDEO",
                    $"Video codec {v.Codec} cannot be remuxed to {r.Container.DisplayName()}."));
            if (v.BitDepth is > 8 && r.Container == ContainerFormat.Mp4)
                issues.Add(CompatibilityIssue.Block("REMUX_DEPTH", "MP4 remux supports only 8-bit video."));
        }
        foreach (var a in selected.Where(st => st.Kind == MediaKind.Audio))
        {
            bool ok = OutputFormatCatalog.RemuxAudioCodecs(r.Container).Contains(a.Codec);
            if (!ok)
                issues.Add(CompatibilityIssue.Block("REMUX_AUDIO",
                    $"Audio codec {a.Codec} cannot be remuxed to {r.Container.DisplayName()}."));
        }
        foreach (var sub in selected.Where(st => st.Kind == MediaKind.Subtitle))
        {
            if (sub.IsBitmapSubtitle)
            {
                if (!OutputFormatCatalog.RemuxBitmapSubtitles(r.Container).Contains(sub.Codec))
                    issues.Add(CompatibilityIssue.Block("REMUX_SUBTITLE_BITMAP",
                        $"Bitmap subtitle {sub.Codec} cannot be copied to {r.Container.DisplayName()}."));
            }
            else if (r.Container is ContainerFormat.Mp4 or ContainerFormat.Mov && sub.Codec != "mov_text"
                || r.Container == ContainerFormat.Webm && sub.Codec != "webvtt")
            {
                issues.Add(CompatibilityIssue.Block("REMUX_SUBTITLE",
                    $"Subtitle codec {sub.Codec} cannot be copied to {r.Container.DisplayName()}."));
            }
        }
        if (s.ColorClassification is ColorClassification.DolbyVision && !CanRemuxDolbyVision(s.PrimaryVideo, r.Container, out var dvReason))
            issues.Add(CompatibilityIssue.Block("REMUX_DV", dvReason!));
    }

    /// <summary>
    /// Graded Dolby Vision rule: stream-copy remux to MKV is allowed when the DOVI
    /// configuration record shows a backward-compatible base layer (profile 8 / BL
    /// compatibility id &gt; 0) so the RPU survives via copy. Profile 5/7 (no compatible
    /// base layer) and unknown profiles are refused with a specific reason.
    /// </summary>
    public static bool CanRemuxDolbyVision(StreamDescriptor? video, ContainerFormat container, out string? reason)
    {
        if (container != ContainerFormat.Mkv)
        {
            reason = "Dolby Vision 目前仅支持转封装到 MKV（stream copy 保留 RPU），其他容器已阻止。";
            return false;
        }
        if (video is null)
        {
            reason = "无法识别 Dolby Vision 配置（缺少 DOVI configuration record），已阻止转封装。";
            return false;
        }
        if (video.DvProfile == 8 || video.DvBlSignalCompatibilityId is > 0)
        {
            reason = null;
            return true;
        }
        reason = video.DvProfile is 5 or 7
            ? $"Dolby Vision Profile {video.DvProfile} 无兼容基底（BL compatibility id=0），暂不支持转封装，请使用专业工具处理。"
            : "无法识别 Dolby Vision profile（缺少 DOVI configuration record），已阻止转封装。";
        return false;
    }

    /// <summary>
    /// Returns the exact remux input set used by validation, argument mapping and expected output.
    /// An empty selection preserves the existing default of all non-attachment media streams.
    /// </summary>
    public static IReadOnlyList<StreamDescriptor> SelectRemuxStreams(ConversionRequest r, SourceSnapshot s)
    {
        // Matroska keeps font/attachment streams (ASS/SSA styling depends on them);
        // other containers cannot carry them, so they are dropped there.
        bool KeepAttachments(ContainerFormat c) => c == ContainerFormat.Mkv;

        if (r.SelectedStreamIndices.Count == 0)
        {
            return s.Streams.Where(st => IsRemuxStream(st, KeepAttachments(r.Container))).ToList();
        }

        var indices = r.SelectedStreamIndices.ToHashSet();
        return s.Streams.Where(st => indices.Contains(st.SourceIndex) && IsRemuxStream(st, KeepAttachments(r.Container))).ToList();
    }

    private static bool IsRemuxStream(StreamDescriptor st, bool keepAttachments) =>
        st.Kind is MediaKind.Audio or MediaKind.Subtitle
        || st.Kind == MediaKind.Attachment && keepAttachments
        || st.Kind == MediaKind.Video && !st.IsAttachedPicture;

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
