using TransFlux.App.State;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Application.Tests;

/// <summary>Workbench warning tests for explicit stream-selection semantics (T02/R03/R05/R07).</summary>
public sealed class StreamSelectionWorkbenchTests
{
    private static MediaDraft Draft(ConversionOptions options, SourceSnapshot source) => new()
    {
        MediaId = source.MediaId,
        Path = source.AbsolutePath,
        Status = DraftStatus.Ready,
        Snapshot = source,
        Options = options,
    };

    [Fact]
    public void TranscodeKeepingSubtitlesDoesNotWarnAboutSubtitleLoss()
    {
        var basic = TestData.MakeSource();
        var source = basic with
        {
            Streams = [.. basic.Streams, new StreamDescriptor { SourceIndex = 2, Kind = MediaKind.Subtitle, Codec = "subrip" }],
        };
        var issues = WorkbenchRules.Validate(Draft(ConversionOptions.Default, source));
        Assert.DoesNotContain(issues, i => i.Code == "SUBTITLE_LOSS");
    }
    [Fact]
    public void SubtitleNoneProducesAnExplicitLossWarning()
    {
        var basic = TestData.MakeSource();
        var source = basic with
        {
            Streams = [.. basic.Streams, new StreamDescriptor { SourceIndex = 2, Kind = MediaKind.Subtitle, Codec = "subrip" }],
        };
        var options = ConversionOptions.Default with { SubtitleMode = SubtitleSelectionMode.None };
        var issues = WorkbenchRules.Validate(Draft(options, source));
        var warning = Assert.Single(issues, i => i.Code == "SUBTITLE_LOSS");
        Assert.Contains("不输出字幕", warning.Message);
        Assert.DoesNotContain("保留全部字幕", warning.Message);
    }

    [Fact]
    public void AudioExtractMultiTrackSelectionBlocksStart()
    {
        var basic = TestData.MakeSource();
        var source = basic with
        {
            Streams = [.. basic.Streams, new StreamDescriptor { SourceIndex = 3, Kind = MediaKind.Audio, Codec = "aac" }],
        };
        var options = ConversionOptions.Default with
        {
            Mode = ConversionMode.AudioExtract, Container = ContainerFormat.M4a,
            SelectedStreams = [1, 3], AudioMode = AudioSelectionMode.Explicit, AudioStreamIndices = [1, 3],
        };
        var item = new WorkbenchItem(Draft(options, source), null);
        Assert.False(item.CanStart);
        Assert.Contains(item.Issues, i => i.Blocking && i.Message.Contains("一次只输出一条音轨"));
    }
}
