using System.Globalization;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.App.State;

/// <summary>Pure display formatting helpers shared by the Reactor components.</summary>
public static class Fmt
{
    public static string Duration(TimeSpan? value)
    {
        if (value is not TimeSpan d || d <= TimeSpan.Zero) return "—";
        var total = (long)Math.Floor(d.TotalHours);
        return total >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", total, d.Minutes, d.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", d.Minutes, d.Seconds);
    }

    public static string DurationMicros(long? micros)
        => micros is long m && m > 0 ? Duration(TimeSpan.FromMicroseconds(m)) : "—";

    public static string Size(long? bytes)
    {
        if (bytes is not long b || b < 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0
            ? $"{v:0} {units[u]}"
            : string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", v, units[u]);
    }

    public static string Speed(double? speed)
        => speed is double s && s > 0 ? string.Format(CultureInfo.InvariantCulture, "{0:0.00}×", s) : "—";

    public static string Eta(TimeSpan? eta)
    {
        if (eta is not TimeSpan t || t <= TimeSpan.Zero) return "—";
        if (t.TotalHours >= 1) return $"约 {(int)t.TotalHours} 小时 {t.Minutes} 分";
        if (t.TotalMinutes >= 1) return $"约 {(int)t.TotalMinutes} 分 {t.Seconds} 秒";
        return $"约 {t.Seconds} 秒";
    }

    public static string FrameRate(TransFlux.Domain.Common.Rational? rate)
        => rate is { } r && r.Denominator != 0 ? string.Format(CultureInfo.InvariantCulture, "{0:0.###}", r.ToDouble()) : "—";

    public static string Resolution(StreamDescriptor? v)
        => v is { Width: int w, Height: int h } ? $"{w}×{h}" : "—";

    public static string VideoText(StreamDescriptor? v)
        => v is null ? "无视频" : $"{CodecName(v.Codec)} · {Resolution(v)}";

    public static string AudioText(StreamDescriptor? a)
    {
        if (a is null) return "无音频";
        var channel = a.ChannelLayout ?? (a.Channels is int c ? c switch { 1 => "1.0", 2 => "2.0", 6 => "5.1", 8 => "7.1", _ => $"{c}ch" } : null);
        var rate = a.SampleRate is int sr ? $"{sr / 1000.0:0.#}kHz" : null;
        var parts = new[] { CodecName(a.Codec), channel, rate }.Where(p => !string.IsNullOrEmpty(p));
        return string.Join(" · ", parts);
    }

    public static string TrackSummary(SourceSnapshot? snapshot, ConversionOptions? options = null)
    {
        if (snapshot is null) return "正在读取媒体信息…";
        var v = VideoText(snapshot.PrimaryVideo);
        var a = AudioText(snapshot.PrimaryAudio);
        var audioTotal = snapshot.AudioStreams.Count();
        var audioSelected = options?.SelectedStreams.Count is > 0 ? options.SelectedStreams.Count : Math.Min(1, audioTotal);
        var audioMap = audioTotal > 0 ? $"输出 {audioSelected}/{audioTotal} 音轨" : "无音轨";
        var subs = snapshot.SubtitleStreams.Count();
        var subtitle = subs > 0 ? $" · 字幕 {subs} 未包含" : string.Empty;
        return $"{v} · {a} · {ColorText(snapshot.ColorClassification)} · {audioMap}{subtitle}";
    }

    public static string CodecName(string? codec) => codec switch
    {
        null or "" => "未知",
        "h264" => "H.264",
        "hevc" => "H.265",
        "av1" => "AV1",
        "aac" => "AAC",
        "flac" => "FLAC",
        "pcm_s16le" => "PCM 16-bit",
        "pcm_s24le" => "PCM 24-bit",
        "pcm_s32le" => "PCM 32-bit",
        "mp3" => "MP3",
        "subrip" or "srt" => "SRT",
        "ass" or "ssa" => "ASS",
        "mov_text" => "mov_text",
        _ => codec!,
    };

    public static string ColorText(ColorClassification c) => c switch
    {
        ColorClassification.SdrBt709 => "SDR BT.709",
        ColorClassification.SdrBt601 => "SDR BT.601",
        ColorClassification.SdrOther => "SDR（其他）",
        ColorClassification.Hdr10 => "HDR10",
        ColorClassification.Hdr10Plus => "HDR10+",
        ColorClassification.DolbyVision => "Dolby Vision",
        ColorClassification.WideGamut => "广色域",
        ColorClassification.Conflicting => "色彩冲突",
        _ => "色彩未知",
    };

    public static string JobStateText(TransFlux.Domain.Jobs.JobState state) => state switch
    {
        TransFlux.Domain.Jobs.JobState.Queued => "排队中",
        TransFlux.Domain.Jobs.JobState.Preflighting => "预检中",
        TransFlux.Domain.Jobs.JobState.Running => "转换中",
        TransFlux.Domain.Jobs.JobState.Verifying => "校验中",
        TransFlux.Domain.Jobs.JobState.Committing => "正在保存",
        TransFlux.Domain.Jobs.JobState.Succeeded => "已完成",
        TransFlux.Domain.Jobs.JobState.Failed => "失败",
        TransFlux.Domain.Jobs.JobState.CancelRequested => "正在取消",
        TransFlux.Domain.Jobs.JobState.Canceled => "已取消",
        TransFlux.Domain.Jobs.JobState.NeedsAttention => "需处理",
        TransFlux.Domain.Jobs.JobState.Recovering => "恢复中",
        TransFlux.Domain.Jobs.JobState.Interrupted => "已中断",
        _ => state.ToString(),
    };

    public static string StageText(TransFlux.Domain.Jobs.JobStage stage) => stage switch
    {
        TransFlux.Domain.Jobs.JobStage.Preflight => "预检",
        TransFlux.Domain.Jobs.JobStage.Encoding => "编码",
        TransFlux.Domain.Jobs.JobStage.Verifying => "校验",
        TransFlux.Domain.Jobs.JobStage.Committing => "保存",
        TransFlux.Domain.Jobs.JobStage.Finalizing => "收尾",
        _ => stage.ToString(),
    };

    public static string AudioCodecName(AudioCodec codec) => codec.Name();

    /// <summary>Short "target format" line for a catalogue choice / task row.</summary>
    public static string TargetSummary(ConversionOptions options) => options.Mode switch
    {
        ConversionMode.Remux => options.Container.DisplayName() + " · 转封装",
        ConversionMode.AudioExtract => options.Container.DisplayName() + " · " + AudioCodecName(options.AudioCodec),
        _ => options.Container.DisplayName() + " · " + options.VideoCodec.Name() + " · " + AudioCodecName(options.AudioCodec),
    };

    public static string FormatTimecode(TimeSpan? value)
        => value is TimeSpan v
            ? string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}", (int)v.TotalHours, v.Minutes, v.Seconds, v.Milliseconds)
            : string.Empty;

    public static TimeSpan? ParseTimecode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] formats = { @"hh\:mm\:ss\.fff", @"h\:mm\:ss\.fff", @"mm\:ss\.fff", @"hh\:mm\:ss", @"mm\:ss" };
        return TimeSpan.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, out var result)
            ? result
            : TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out var loose) ? loose : null;
    }
}
