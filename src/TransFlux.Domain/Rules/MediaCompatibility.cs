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
    /// <summary>
    /// Input container/demuxer names that are playlists, concatenation scripts or image
    /// sequences rather than a single media timeline. They are never valid TransFlux inputs.
    /// </summary>
    private static readonly string[] UnsupportedInputFormats =
    [
        "concat", "ffconcat", "hls", "applehttp", "dash", "image2", "image2pipe",
    ];

    public static IReadOnlyList<CompatibilityIssue> Validate(ConversionRequest request, SourceSnapshot source)
    {
        var issues = new List<CompatibilityIssue>();
        if (request.RequestVersion is < 1 or > 2)
        {
            issues.Add(CompatibilityIssue.Block("REQUEST_VERSION",
                $"不支持的请求版本 {request.RequestVersion}；当前支持版本 1 和 2。"));
            return issues;
        }
        ValidateInputFormat(source, issues);
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

    private static void ValidateInputFormat(SourceSnapshot source, List<CompatibilityIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(source.Format.FormatName)) return;
        foreach (var token in source.Format.FormatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!UnsupportedInputFormats.Contains(token, StringComparer.OrdinalIgnoreCase)) continue;
            issues.Add(CompatibilityIssue.Block("INPUT_FORMAT",
                $"输入格式 '{token}' 属于播放列表/拼接/图片序列，不是单一媒体文件，无法作为转码输入。"));
            return;
        }
    }

    private static void ValidateVideoTranscode(ConversionRequest r, SourceSnapshot s, List<CompatibilityIssue> issues)
    {
        if (!s.HasVideo)
            issues.Add(CompatibilityIssue.Block("NO_VIDEO", "Source has no video stream to transcode."));
        if (!OutputFormatCatalog.VideoContainers.Contains(r.Container))
            issues.Add(CompatibilityIssue.Block("BAD_CONTAINER", "Choose a video container."));
        else if (!OutputFormatCatalog.VideoCodecs(r.Container).Contains(r.VideoCodec)
            || (r.Audio.Codec != AudioCodec.Copy && !OutputFormatCatalog.AudioCodecs(r.Container).Contains(r.Audio.Codec)))
            issues.Add(CompatibilityIssue.Block("CODEC_CONTAINER", "Selected codecs do not match the video container."));
        if (r.Audio.Codec == AudioCodec.Copy)
            ValidateAudioCopy(r, s, issues);
        var v = SelectPrimaryVideo(r, s);
        var classification = v?.ColorClassification ?? s.ColorClassification;
        if (classification is not ColorClassification.SdrBt709)
            issues.Add(CompatibilityIssue.Block("NON_SDR",
                "Only verified SDR BT.709 video may be transcoded. Use Remux for compatible HDR sources."));
        if (r.RequestVersion >= 2 && v is null)
            issues.Add(CompatibilityIssue.Block("VIDEO_SELECTION", "所选视频轨不存在或不是视频轨，请重新选择。"));
        if (r.RequestVersion >= 2 && r.AudioTracks.Mode == AudioSelectionMode.Explicit
            && StreamSelectionResolver.Resolve(r, s).Audio.Count == 0)
            issues.Add(CompatibilityIssue.Block("AUDIO_SELECTION",
                "已选择“指定音轨”但没有有效音轨（未勾选或所选音轨不存在）；请至少选择一条有效音轨或改用自动。"));
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

    /// <summary>
    /// G2 audio-copy slice: stream-copy audio is supported only for video transcode to MKV.
    /// The planner maps the resolved tracks unchanged, so every selected source codec must be
    /// on the Matroska remux whitelist and at least one track must actually be selected.
    /// </summary>
    private static void ValidateAudioCopy(ConversionRequest r, SourceSnapshot s, List<CompatibilityIssue> issues)
    {
        if (!OutputFormatCatalog.SupportsAudioCopy(r.Container))
        {
            issues.Add(CompatibilityIssue.Block("AUDIO_COPY_UNSUPPORTED",
                $"音轨复制目前仅支持 MKV 输出，{r.Container.DisplayName()} 不支持。"));
            return;
        }

        var selection = StreamSelectionResolver.Resolve(r, s);
        if (r.AudioTracks.Mode == AudioSelectionMode.None || selection.Audio.Count == 0)
        {
            issues.Add(CompatibilityIssue.Block("AUDIO_COPY_UNSUPPORTED",
                "音频选择为“复制”但没有可输出的源音轨，请选择音轨或改用音频编码。"));
            return;
        }

        var copyable = OutputFormatCatalog.RemuxAudioCodecs(ContainerFormat.Mkv);
        foreach (var audio in selection.Audio)
        {
            if (copyable.Contains(audio.Codec, StringComparer.OrdinalIgnoreCase)) continue;
            issues.Add(CompatibilityIssue.Block("AUDIO_COPY_UNSUPPORTED",
                $"源音轨 #{audio.SourceIndex}（{audio.Codec}）无法直接复制到 MKV，请改用音频编码。"));
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
        if (r.RequestVersion >= 2)
        {
            // Audio extraction is explicitly single-track. Never let a multi-track selection
            // degrade into silently writing only the first track.
            switch (r.AudioTracks.Mode)
            {
                case AudioSelectionMode.None:
                    issues.Add(CompatibilityIssue.Block("AUDIO_EXTRACT_NONE",
                        "音频提取需要输出一条音轨，当前设置为不输出音轨，请选择一条源音轨。"));
                    break;
                case AudioSelectionMode.Explicit when r.AudioTracks.SourceStreamIndices.Count > 1:
                    issues.Add(CompatibilityIssue.Block("AUDIO_EXTRACT_SINGLE",
                        "音频提取一次只输出一条音轨，请只选择一条源音轨。"));
                    break;
                case AudioSelectionMode.Explicit when r.AudioTracks.SourceStreamIndices.Count == 0:
                    issues.Add(CompatibilityIssue.Block("AUDIO_EXTRACT_SINGLE",
                        "音频提取一次只输出一条音轨，请选择一条源音轨或使用自动。"));
                    break;
            }
            if (s.HasAudio && SelectPrimaryAudio(r, s) is null)
                issues.Add(CompatibilityIssue.Block("AUDIO_EXTRACT_TRACK", "所选音轨不存在，请重新选择。"));
        }
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
        if (r.RequestVersion >= 2)
        {
            var selection = StreamSelectionResolver.Resolve(r, s);
            if (selection.Video is null && (s.HasVideo || r.VideoSelect.Mode == VideoSelectionMode.Explicit))
                issues.Add(CompatibilityIssue.Block("VIDEO_SELECTION",
                    "所选视频轨不存在或不是视频轨，请重新选择。"));
            if (r.AudioTracks.Mode == AudioSelectionMode.Explicit && selection.Audio.Count == 0)
                issues.Add(CompatibilityIssue.Block("AUDIO_SELECTION",
                    "已选择“指定音轨”但没有有效音轨（未勾选或所选音轨不存在）；请至少选择一条有效音轨或改用自动。"));
        }

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
        // Legacy remux can map several video tracks; a secondary Dolby Vision track must not
        // bypass the MKV/profile gate just because the primary happens to be non-DV. Evaluate
        // every mapped video track and fail-closed on any DV-classified track.
        var primaryVideo = SelectPrimaryVideo(r, s);
        foreach (var video in selected.Where(st => st.Kind == MediaKind.Video))
        {
            var classification = EffectiveClassification(video, s, primaryVideo);
            if (classification is not ColorClassification.DolbyVision) continue;
            if (!CanRemuxDolbyVision(video, r.Container, out var dvReason))
                issues.Add(CompatibilityIssue.Block("REMUX_DV", dvReason!));
        }
    }

    /// <summary>
    /// Per-track colour class with a conservative fallback: an explicit per-stream class wins,
    /// a DOVI record implies Dolby Vision, and the legacy snapshot-level class only applies to
    /// the primary video track (the historical scope of that field).
    /// </summary>
    private static ColorClassification EffectiveClassification(StreamDescriptor video, SourceSnapshot source,
        StreamDescriptor? primaryVideo)
    {
        if (video.ColorClassification is { } classification) return classification;
        if (video.DvProfile is not null) return ColorClassification.DolbyVision;
        return primaryVideo is { } primary && primary.SourceIndex == video.SourceIndex
            ? source.ColorClassification
            : ColorClassification.Unknown;
    }

    /// <summary>
    /// Graded Dolby Vision rule: stream-copy remux to MKV is allowed only when the DOVI
    /// configuration record identifies Profile 8 with a backward-compatible base layer
    /// (BL compatibility id &gt; 0). A non-zero compatibility id alone is not sufficient:
    /// inconsistent combinations such as Profile 7 + compatibility id 1 stay blocked.
    /// All other profiles (5/6/7 and unknown) are refused with a specific reason.
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
        if (video.DvProfile == 8 && video.DvBlSignalCompatibilityId is > 0)
        {
            reason = null;
            return true;
        }
        reason = video.DvProfile is { } profile
            ? $"Dolby Vision Profile {profile} 暂不支持转封装（仅 Profile 8 且 BL compatibility id > 0 可保留 RPU）。"
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

        if (r.RequestVersion >= 2)
        {
            var selection = StreamSelectionResolver.Resolve(r, s);
            var wanted = new HashSet<int>();
            if (selection.Video is { } video) wanted.Add(video.SourceIndex);
            foreach (var audio in selection.Audio) wanted.Add(audio.SourceIndex);
            foreach (var subtitle in selection.Subtitles) wanted.Add(subtitle.SourceIndex);
            return s.Streams.Where(st => IsRemuxStream(st, KeepAttachments(r.Container))
                && (st.Kind == MediaKind.Attachment || wanted.Contains(st.SourceIndex))).ToList();
        }

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
        if (r.RequestVersion >= 2) return StreamSelectionResolver.Resolve(r, s).Video;
        foreach (var idx in r.SelectedStreamIndices)
        {
            var st = s.Streams.FirstOrDefault(x => x.SourceIndex == idx);
            if (st is { Kind: MediaKind.Video, IsAttachedPicture: false }) return st;
        }
        return s.PrimaryVideo;
    }

    public static StreamDescriptor? SelectPrimaryAudio(ConversionRequest r, SourceSnapshot s)
    {
        if (r.RequestVersion >= 2)
        {
            if (r.AudioTracks.Mode == AudioSelectionMode.None) return null;
            if (r.AudioTracks.Mode == AudioSelectionMode.Auto) return s.PrimaryAudio;
            return StreamSelectionResolver.Resolve(r, s).Audio.FirstOrDefault();
        }
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
