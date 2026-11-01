using TransFlux.Domain.Plans;

namespace TransFlux.Domain.Rules;

/// <summary>One output compatibility matrix shared by the inspector and planner validation.</summary>
public static class OutputFormatCatalog
{
    public static IReadOnlyList<ContainerFormat> VideoContainers { get; } =
        [ContainerFormat.Mp4, ContainerFormat.Mkv, ContainerFormat.Mov, ContainerFormat.Webm];
    public static IReadOnlyList<ContainerFormat> AudioContainers { get; } =
        [ContainerFormat.M4a, ContainerFormat.Mp3, ContainerFormat.Flac, ContainerFormat.Wav, ContainerFormat.Ogg, ContainerFormat.Opus, ContainerFormat.Aac];

    public static IReadOnlyList<VideoCodec> VideoCodecs(ContainerFormat container) => container switch
    {
        ContainerFormat.Mp4 => [VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1],
        ContainerFormat.Mov => [VideoCodec.H264, VideoCodec.Hevc],
        ContainerFormat.Mkv => [VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1, VideoCodec.Vp9],
        ContainerFormat.Webm => [VideoCodec.Vp9, VideoCodec.Av1],
        _ => [],
    };
    public static IReadOnlyList<AudioCodec> AudioCodecs(ContainerFormat container) => container switch
    {
        ContainerFormat.Mp4 => [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Alac],
        ContainerFormat.Mov => [AudioCodec.Aac, AudioCodec.Alac, AudioCodec.PcmS16le, AudioCodec.PcmS24le],
        ContainerFormat.Mkv => [AudioCodec.Aac, AudioCodec.Opus, AudioCodec.Vorbis, AudioCodec.Flac, AudioCodec.Mp3, AudioCodec.PcmS16le, AudioCodec.PcmS24le],
        ContainerFormat.Webm => [AudioCodec.Opus, AudioCodec.Vorbis],
        ContainerFormat.M4a => [AudioCodec.Aac, AudioCodec.Alac],
        ContainerFormat.Mp3 => [AudioCodec.Mp3],
        ContainerFormat.Flac => [AudioCodec.Flac],
        ContainerFormat.Wav => [AudioCodec.PcmS16le, AudioCodec.PcmS24le],
        ContainerFormat.Ogg => [AudioCodec.Vorbis, AudioCodec.Opus],
        ContainerFormat.Opus => [AudioCodec.Opus],
        ContainerFormat.Aac => [AudioCodec.Aac],
        _ => [],
    };
    /// <summary>
    /// Probe codec names accepted for stream-copy remux into the container. Wider than the
    /// transcode matrix on purpose: remux copies whatever the container natively holds.
    /// </summary>
    public static IReadOnlyList<string> RemuxVideoCodecs(ContainerFormat container) => container switch
    {
        ContainerFormat.Mp4 => ["h264", "hevc", "av1", "mpeg4"],
        ContainerFormat.Mov => ["h264", "hevc", "mpeg4", "prores"],
        ContainerFormat.Mkv => ["h264", "hevc", "av1", "vp8", "vp9", "mpeg1video", "mpeg2video",
            "mpeg4", "vc1", "prores", "theora", "h263", "msmpeg4v3", "ffv1"],
        ContainerFormat.Webm => ["vp8", "vp9", "av1"],
        _ => [],
    };
    /// <summary>Probe codec names accepted for stream-copy remux into the container.</summary>
    public static IReadOnlyList<string> RemuxAudioCodecs(ContainerFormat container) => container switch
    {
        ContainerFormat.Mp4 => ["aac", "mp3", "alac", "ac3", "eac3"],
        ContainerFormat.Mov => ["aac", "alac", "pcm_s16le", "pcm_s24le", "ac3", "eac3"],
        ContainerFormat.Mkv => ["aac", "mp3", "opus", "vorbis", "flac", "pcm_s16le", "pcm_s24le",
            "pcm_s32le", "ac3", "eac3", "truehd", "mlp", "dts", "mp2"],
        ContainerFormat.Webm => ["opus", "vorbis"],
        _ => [],
    };
    /// <summary>Bitmap subtitle codecs (probe names) allowed for stream-copy remux into the container.</summary>
    public static IReadOnlyList<string> RemuxBitmapSubtitles(ContainerFormat container) => container switch
    {
        ContainerFormat.Mkv => ["hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub", "dvb_teletext"],
        _ => [],
    };

    public static string Name(this VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264 / AVC", VideoCodec.Hevc => "H.265 / HEVC", VideoCodec.Av1 => "AV1",
        VideoCodec.Vp9 => "VP9", VideoCodec.Copy => "复制原始编码（转封装）", _ => codec.ToString(),
    };
    public static string Name(this AudioCodec codec) => codec switch
    {
        AudioCodec.Aac => "AAC", AudioCodec.Mp3 => "MP3", AudioCodec.Opus => "Opus", AudioCodec.Vorbis => "Vorbis",
        AudioCodec.Flac => "FLAC", AudioCodec.Alac => "ALAC", AudioCodec.PcmS16le => "PCM 16-bit",
        AudioCodec.PcmS24le => "PCM 24-bit", AudioCodec.Copy => "复制原始音轨", _ => codec.ToString(),
    };
    public static string Encoder(this AudioCodec codec) => codec switch
    {
        AudioCodec.Aac => "aac", AudioCodec.Mp3 => "libmp3lame", AudioCodec.Opus => "libopus", AudioCodec.Vorbis => "libvorbis",
        AudioCodec.Flac => "flac", AudioCodec.Alac => "alac", AudioCodec.PcmS16le => "pcm_s16le", AudioCodec.PcmS24le => "pcm_s24le",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
    public static string ProbeName(this VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "h264", VideoCodec.Hevc => "hevc", VideoCodec.Av1 => "av1", VideoCodec.Vp9 => "vp9",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
    public static string ProbeName(this AudioCodec codec) => codec switch
    {
        AudioCodec.Mp3 => "mp3", AudioCodec.Opus => "opus", AudioCodec.Vorbis => "vorbis", _ => codec.Encoder(),
    };
    public static bool HasBitrate(this AudioCodec codec) => codec is AudioCodec.Aac or AudioCodec.Mp3 or AudioCodec.Opus or AudioCodec.Vorbis;
}
