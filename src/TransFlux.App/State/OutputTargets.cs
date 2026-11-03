using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.App.State;

/// <summary>Dependent field changes retain compatible values and normalize only incompatible ones.</summary>
public static class OutputTargets
{
    public static bool IsAudio(ConversionOptions o) => o.Mode == ConversionMode.AudioExtract;
    public static bool IsPcm(AudioCodec codec) => codec is AudioCodec.PcmS16le or AudioCodec.PcmS24le;
    public static ConversionOptions WithMedia(ConversionOptions o, bool audio)
    {
        if (IsAudio(o) == audio) return o;
        var next = o with { Mode = audio ? ConversionMode.AudioExtract : ConversionMode.VideoTranscode };
        if (audio)
        {
            // Audio extraction is single-track: a multi-track video selection must not carry
            // over into a request that could silently drop tracks.
            var legacyCount = next.SelectedStreams.Count;
            var explicitCount = next.AudioMode == AudioSelectionMode.Explicit ? next.AudioStreamIndices.Count : 0;
            if (next.AudioMode == AudioSelectionMode.None || Math.Max(legacyCount, explicitCount) > 1)
                next = next with { AudioMode = AudioSelectionMode.Auto, AudioStreamIndices = Array.Empty<int>(), SelectedStreams = Array.Empty<int>() };
        }
        // Source track indices, trim range and unrelated settings stay valid across media forms.
        return WithContainer(next, audio ? ContainerFormat.M4a : ContainerFormat.Mp4);
    }
    public static ConversionOptions WithContainer(ConversionOptions o, ContainerFormat container)
    {
        var audio = OutputFormatCatalog.AudioCodecs(container);
        var video = OutputFormatCatalog.VideoCodecs(container);
        var next = o with { Container = container };
        if (o.Mode == ConversionMode.Remux) next = next with { AudioCodec = AudioCodec.Copy };
        else if (audio.Count > 0 && !audio.Contains(o.AudioCodec)) next = WithAudioCodec(next, audio[0]);
        if (o.Mode == ConversionMode.VideoTranscode && video.Count > 0 && !video.Contains(o.VideoCodec))
            next = next with { VideoCodec = video[0], Hardware = HardwareBackend.Auto, CrfOverride = null };
        return next;
    }
    public static ConversionOptions WithPcmBitDepth(ConversionOptions o, bool twentyFourBit)
        => o with { AudioCodec = twentyFourBit ? AudioCodec.PcmS24le : AudioCodec.PcmS16le };
    public static ConversionOptions WithAudioCodec(ConversionOptions o, AudioCodec codec)
    {
        var maxBitrate = codec == AudioCodec.Mp3 ? 320 : codec == AudioCodec.Opus ? 510 : 512;
        var rate = o.AudioSampleRate;
        if (codec == AudioCodec.Opus && rate is not (null or 8000 or 12000 or 16000 or 24000 or 48000)
            || codec == AudioCodec.Mp3 && rate is not (null or 8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000)) rate = null;
        return o with { AudioCodec = codec, AudioSampleRate = rate,
            AudioBitRateKbps = o.AudioBitRateKbps is int bitrate ? Math.Min(bitrate, maxBitrate) : null };
    }
    public static ConversionOptions WithVideoCodec(ConversionOptions o, VideoCodec codec)
    {
        if (codec == VideoCodec.Copy)
            return o with { Mode = ConversionMode.Remux, AudioCodec = AudioCodec.Copy, TrimStart = null, TrimEnd = null };
        return WithContainer(o with { Mode = ConversionMode.VideoTranscode, VideoCodec = codec,
            Hardware = HardwareBackend.Auto, CrfOverride = null }, o.Container);
    }

    public static ConversionOptions WithAudioMode(ConversionOptions o, AudioSelectionMode mode)
        => o with
        {
            AudioMode = mode,
            AudioStreamIndices = mode == AudioSelectionMode.Explicit ? o.AudioStreamIndices : Array.Empty<int>(),
            SelectedStreams = mode == AudioSelectionMode.Explicit ? o.AudioStreamIndices : Array.Empty<int>(),
        };

    public static ConversionOptions WithAudioAuto(ConversionOptions o) => WithAudioMode(o, AudioSelectionMode.Auto);

    public static ConversionOptions WithAudioTrack(ConversionOptions o, int sourceStreamIndex)
        => WithAudioTrackSelection(o, new[] { sourceStreamIndex });

    public static ConversionOptions WithAudioTrackSelection(ConversionOptions o, IReadOnlyList<int> sourceStreamIndices)
    {
        var indices = sourceStreamIndices.Distinct().OrderBy(i => i).ToArray();
        return o with { AudioMode = AudioSelectionMode.Explicit, AudioStreamIndices = indices, SelectedStreams = indices };
    }

    public static ConversionOptions WithSubtitleMode(ConversionOptions o, SubtitleSelectionMode mode)
        => o with
        {
            SubtitleMode = mode,
            SubtitleStreamIndices = mode == SubtitleSelectionMode.Explicit ? o.SubtitleStreamIndices : Array.Empty<int>(),
        };

    public static ConversionOptions WithSubtitleTracks(ConversionOptions o, IReadOnlyList<int> sourceStreamIndices)
        => o with
        {
            SubtitleMode = SubtitleSelectionMode.Explicit,
            SubtitleStreamIndices = sourceStreamIndices.Distinct().OrderBy(i => i).ToArray(),
        };
}
