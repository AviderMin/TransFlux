using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Domain.Rules;

/// <summary>
/// Decides how source subtitle streams are carried into a transcode output:
/// stream copy when the container holds the codec as-is, an explicit text
/// transcode otherwise (mov_text for MP4/MOV, srt for MKV, webvtt for WebM).
/// Bitmap subtitles only stream-copy into Matroska; anywhere else they are
/// excluded with a warning (burn-in and external import are out of scope).
/// </summary>
public static class SubtitlePolicy
{
    public static bool IsTextCodec(string? codec) => codec is
        "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text" or "text";

    public sealed record SubtitleTarget(StreamDescriptor Stream, string Encoder, bool IsCopy);

    public sealed record SubtitleSelection(
        IReadOnlyList<SubtitleTarget> Included,
        IReadOnlyList<string> Warnings);

    /// <summary>Target encoder ("copy" = stream copy) for one source subtitle in a container.</summary>
    public static string TargetEncoder(ContainerFormat container, StreamDescriptor sub)
    {
        var isText = sub.IsTextSubtitle || IsTextCodec(sub.Codec);
        return container switch
        {
            ContainerFormat.Mkv => !isText ? "copy"
                : sub.Codec == "mov_text" ? "srt" : "copy",
            ContainerFormat.Mp4 or ContainerFormat.Mov => !isText ? ""
                : sub.Codec == "mov_text" ? "copy" : "mov_text",
            ContainerFormat.Webm => !isText ? ""
                : sub.Codec == "webvtt" ? "copy" : "webvtt",
            _ => "",
        };
    }

    /// <summary>
    /// Selects subtitle streams for a video transcode: user-selected streams when the
    /// request pins stream indices, otherwise every subtitle stream of the source.
    /// Streams the container cannot hold are excluded and reported as warnings.
    /// </summary>
    public static SubtitleSelection SelectForTranscode(ConversionRequest request, SourceSnapshot source)
    {
        var candidates = source.SubtitleStreams.AsEnumerable();
        if (request.SelectedStreamIndices.Count > 0)
        {
            var indices = request.SelectedStreamIndices.ToHashSet();
            candidates = candidates.Where(s => indices.Contains(s.SourceIndex));
        }

        var included = new List<SubtitleTarget>();
        var warnings = new List<string>();
        foreach (var sub in candidates)
        {
            var encoder = TargetEncoder(request.Container, sub);
            if (encoder.Length == 0)
            {
                warnings.Add($"字幕流 #{sub.SourceIndex}（{Display(sub.Codec)}）无法写入 {request.Container.DisplayName()}，已跳过。");
                continue;
            }
            included.Add(new SubtitleTarget(sub, encoder, IsCopy: encoder == "copy"));
        }
        return new SubtitleSelection(included, warnings);
    }

    private static string Display(string codec) => string.IsNullOrEmpty(codec) ? "未知编码" : codec;
}
