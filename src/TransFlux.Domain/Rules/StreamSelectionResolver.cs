using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Domain.Rules;

/// <summary>
/// Single resolution entry for stream selection. Every consumer (compatibility, planner,
/// subtitle policy, workbench rules) must resolve through this class so audio, video and
/// subtitle intents cannot drift apart.
///
/// RequestVersion=1 keeps the historical rule: a non-empty SelectedStreamIndices list filters
/// video (first selected video), audio and subtitles at the same time; an empty list keeps all
/// audio and all subtitles. RequestVersion=2 ignores SelectedStreamIndices and resolves the
/// explicit Video/AudioTracks/Subtitles fields instead: audio and subtitle selections are
/// completely independent, subtitles default to AutoPreserve, None means intentionally emit
/// nothing, and Explicit keeps exactly the listed source stream indices.
/// </summary>
public static class StreamSelectionResolver
{
    public sealed record ResolvedStreamSelection(
        StreamDescriptor? Video,
        IReadOnlyList<StreamDescriptor> Audio,
        IReadOnlyList<StreamDescriptor> Subtitles,
        IReadOnlyList<string> Warnings);

    public static bool IsExplicitSelection(ConversionRequest request) => request.RequestVersion >= 2;

    public static ResolvedStreamSelection Resolve(ConversionRequest request, SourceSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        return IsExplicitSelection(request) ? ResolveExplicit(request, source) : ResolveLegacy(request, source);
    }

    private static ResolvedStreamSelection ResolveExplicit(ConversionRequest request, SourceSnapshot source)
    {
        var warnings = new List<string>();
        var video = ResolveVideo(request.VideoSelect, source, warnings);
        var audio = ResolveAudio(request.AudioTracks, source, warnings);
        var subtitles = ResolveSubtitles(request.Subtitles, source, warnings);
        return new ResolvedStreamSelection(video, audio, subtitles, warnings);
    }

    private static StreamDescriptor? ResolveVideo(VideoSelection selection, SourceSnapshot source, List<string> warnings)
    {
        if (selection.Mode == VideoSelectionMode.Auto) return source.PrimaryVideo;
        if (selection.SourceStreamIndex is not int index)
        {
            warnings.Add("视频选择为指定流，但没有提供源流索引。");
            return null;
        }
        var stream = source.Streams.FirstOrDefault(s =>
            s.SourceIndex == index && s.Kind == MediaKind.Video && !s.IsAttachedPicture);
        if (stream is null) warnings.Add($"所选视频流 #{index} 不存在或不是视频轨。");
        return stream;
    }

    private static IReadOnlyList<StreamDescriptor> ResolveAudio(AudioSelection selection, SourceSnapshot source, List<string> warnings)
    {
        switch (selection.Mode)
        {
            case AudioSelectionMode.Auto:
                return source.AudioStreams.ToList();
            case AudioSelectionMode.None:
                return Array.Empty<StreamDescriptor>();
        }

        var streams = new List<StreamDescriptor>();
        foreach (var index in selection.SourceStreamIndices.Distinct())
        {
            var stream = source.Streams.FirstOrDefault(s => s.SourceIndex == index && s.Kind == MediaKind.Audio);
            if (stream is null) warnings.Add($"所选音轨 #{index} 不存在或不是音轨。");
            else streams.Add(stream);
        }
        return streams;
    }

    private static IReadOnlyList<StreamDescriptor> ResolveSubtitles(SubtitleSelection selection, SourceSnapshot source, List<string> warnings)
    {
        switch (selection.Mode)
        {
            case SubtitleSelectionMode.AutoPreserve:
                return source.SubtitleStreams.ToList();
            case SubtitleSelectionMode.None:
                return Array.Empty<StreamDescriptor>();
        }

        var streams = new List<StreamDescriptor>();
        foreach (var index in selection.SourceStreamIndices.Distinct())
        {
            var stream = source.Streams.FirstOrDefault(s => s.SourceIndex == index && s.Kind == MediaKind.Subtitle);
            if (stream is null) warnings.Add($"所选字幕轨 #{index} 不存在或不是字幕轨。");
            else streams.Add(stream);
        }
        return streams;
    }

    private static ResolvedStreamSelection ResolveLegacy(ConversionRequest request, SourceSnapshot source)
    {
        var indices = request.SelectedStreamIndices;

        StreamDescriptor? video = null;
        foreach (var index in indices)
        {
            var stream = source.Streams.FirstOrDefault(s =>
                s.SourceIndex == index && s.Kind == MediaKind.Video && !s.IsAttachedPicture);
            if (stream is not null) { video = stream; break; }
        }
        video ??= source.PrimaryVideo;

        // Legacy audio rule: selected audio tracks in request order; when the list contains no
        // audio index at all, every source audio track is kept (the old planner behaviour).
        var audio = new List<StreamDescriptor>();
        foreach (var index in indices)
        {
            var stream = source.Streams.FirstOrDefault(s => s.SourceIndex == index && s.Kind == MediaKind.Audio);
            if (stream is not null) audio.Add(stream);
        }
        if (audio.Count == 0) audio.AddRange(source.AudioStreams);
        if (audio.Count == 0 && source.PrimaryAudio is { } primary) audio.Add(primary);

        var subtitleIndices = indices.ToHashSet();
        var subtitles = indices.Count > 0
            ? source.SubtitleStreams.Where(s => subtitleIndices.Contains(s.SourceIndex)).ToList()
            : source.SubtitleStreams.ToList();

        return new ResolvedStreamSelection(video, audio, subtitles, Array.Empty<string>());
    }
}
