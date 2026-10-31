using TransFlux.Domain.Plans;

namespace TransFlux.App.State;

/// <summary>Groups shown in the left target-format catalogue (Format-Factory style).</summary>
public enum TargetFormatGroup { Video, Audio, Image }

/// <summary>
/// One entry in the target-format catalogue. Selecting an entry sets the mode /
/// container / audio codec for newly added files (and for the current selection).
/// Pure data: no I/O.
/// </summary>
public sealed record TargetFormat(
    string Key,
    string Title,
    string Detail,
    TargetFormatGroup Group,
    ConversionMode Mode,
    ContainerFormat Container,
    AudioCodec AudioCodec,
    bool Enabled = true,
    string? DisabledReason = null)
{
    public ConversionOptions ApplyTo(ConversionOptions options) => options with
    {
        Mode = Mode,
        Container = Container,
        AudioCodec = AudioCodec,
    };
}

/// <summary>The fixed target-format catalogue: 视频 / 音频 / 图片 (docs/03).</summary>
public static class TargetFormats
{
    public static readonly IReadOnlyList<TargetFormat> All = new[]
    {
        new TargetFormat("mp4-h264", "MP4", "H.264 / AAC", TargetFormatGroup.Video,
            ConversionMode.VideoTranscode, ContainerFormat.Mp4, AudioCodec.Aac),
        new TargetFormat("mkv-h264", "MKV", "H.264 / AAC", TargetFormatGroup.Video,
            ConversionMode.VideoTranscode, ContainerFormat.Mkv, AudioCodec.Aac),
        new TargetFormat("mkv-remux", "MKV · 转封装", "复制音视频轨道（不重编码）", TargetFormatGroup.Video,
            ConversionMode.Remux, ContainerFormat.Mkv, AudioCodec.Copy),
        new TargetFormat("m4a-aac", "M4A", "AAC 音频", TargetFormatGroup.Audio,
            ConversionMode.AudioExtract, ContainerFormat.M4a, AudioCodec.Aac),
        new TargetFormat("flac", "FLAC", "无损音频", TargetFormatGroup.Audio,
            ConversionMode.AudioExtract, ContainerFormat.Flac, AudioCodec.Flac),
        new TargetFormat("wav", "WAV", "PCM 16-bit", TargetFormatGroup.Audio,
            ConversionMode.AudioExtract, ContainerFormat.Wav, AudioCodec.PcmS16le),
        new TargetFormat("png", "PNG", "图片转换将在后续版本开放", TargetFormatGroup.Image,
            ConversionMode.VideoTranscode, ContainerFormat.Mp4, AudioCodec.Aac,
            Enabled: false, DisabledReason: "图片转换将在后续版本开放"),
        new TargetFormat("jpeg", "JPEG", "图片转换将在后续版本开放", TargetFormatGroup.Image,
            ConversionMode.VideoTranscode, ContainerFormat.Mp4, AudioCodec.Aac,
            Enabled: false, DisabledReason: "图片转换将在后续版本开放"),
    };

    public static TargetFormat Default => All[0];

    public static TargetFormat? Find(string key) => All.FirstOrDefault(f => f.Key == key);

    public static IReadOnlyList<TargetFormat> ForGroup(TargetFormatGroup group)
        => All.Where(f => f.Group == group).ToList();

    public static string GroupTitle(TargetFormatGroup group) => group switch
    {
        TargetFormatGroup.Video => "视频",
        TargetFormatGroup.Audio => "音频",
        _ => "图片",
    };
}
