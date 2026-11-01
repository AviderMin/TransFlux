using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.App.State;

public sealed record TargetFormat(string Key, ConversionMode Mode, ContainerFormat Container, VideoCodec VideoCodec, AudioCodec AudioCodec)
{
    public ConversionOptions ApplyTo(ConversionOptions o) => o with
    { Mode = Mode, Container = Container, VideoCodec = VideoCodec, AudioCodec = AudioCodec };
}

/// <summary>Stable last-used keys, including the keys written by earlier releases.</summary>
public static class TargetFormats
{
    public static IReadOnlyList<TargetFormat> All { get; } = Build();
    public static TargetFormat Default => All[0];
    public static TargetFormat? Find(string key) => All.FirstOrDefault(f => f.Key == key);
    public static TargetFormat? Find(ConversionOptions o) => All.FirstOrDefault(f => f.Mode == o.Mode && f.Container == o.Container
        && (o.Mode != ConversionMode.VideoTranscode || f.VideoCodec == o.VideoCodec)
        && (o.Mode == ConversionMode.Remux || f.AudioCodec == o.AudioCodec));
    private static IReadOnlyList<TargetFormat> Build()
    {
        var result = new List<TargetFormat>();
        foreach (var container in OutputFormatCatalog.VideoContainers)
        {
            foreach (var video in OutputFormatCatalog.VideoCodecs(container))
                foreach (var audio in OutputFormatCatalog.AudioCodecs(container))
                {
                    var prefix = $"{container.ToString().ToLowerInvariant()}-{video.ToString().ToLowerInvariant()}";
                    var key = audio == AudioCodec.Aac ? prefix : prefix + "-" + audio.ToString().ToLowerInvariant();
                    result.Add(new(key, ConversionMode.VideoTranscode, container, video, audio));
                }
            result.Add(new(container.ToString().ToLowerInvariant() + "-remux", ConversionMode.Remux, container, VideoCodec.H264, AudioCodec.Copy));
        }
        foreach (var container in OutputFormatCatalog.AudioContainers)
            foreach (var audio in OutputFormatCatalog.AudioCodecs(container))
            {
                var key = container switch
                {
                    ContainerFormat.Flac => "flac", ContainerFormat.Wav when audio == AudioCodec.PcmS16le => "wav",
                    _ => $"{container.ToString().ToLowerInvariant()}-{audio.ToString().ToLowerInvariant()}",
                };
                result.Add(new(key, ConversionMode.AudioExtract, container, VideoCodec.H264, audio));
            }
        return result;
    }
}
