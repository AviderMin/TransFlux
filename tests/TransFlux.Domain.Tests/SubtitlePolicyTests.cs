using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.Domain.Tests;

/// <summary>Regression tests for transcode subtitle carrying rules (task-2 item 3a).</summary>
public class SubtitlePolicyTests
{
    private static SubtitlePolicy.SubtitleSelection Select(ContainerFormat container, params StreamDescriptor[] subs)
    {
        var streams = new List<StreamDescriptor> { TestData.Video(), TestData.Audio() };
        streams.AddRange(subs);
        var request = TestData.Request(mode: ConversionMode.VideoTranscode, container: container);
        return SubtitlePolicy.SelectForTranscode(request, TestData.Source(streams));
    }

    [Fact]
    public void Mp4_Subrip_BecomesMovText()
    {
        var sel = Select(ContainerFormat.Mp4, TestData.Subtitle(codec: "subrip", language: "jpn"));
        var target = Assert.Single(sel.Included);
        Assert.Equal("mov_text", target.Encoder);
        Assert.False(target.IsCopy);
        Assert.Empty(sel.Warnings);
    }

    [Theory]
    [InlineData("ass")]
    [InlineData("ssa")]
    [InlineData("webvtt")]
    [InlineData("text")]
    public void Mp4_TextSubtitles_BecomeMovText(string codec)
    {
        var sel = Select(ContainerFormat.Mp4, TestData.Subtitle(codec: codec));
        Assert.Equal("mov_text", Assert.Single(sel.Included).Encoder);
    }

    [Fact]
    public void Mp4_MovText_StreamCopies()
    {
        var sel = Select(ContainerFormat.Mp4, TestData.Subtitle(codec: "mov_text"));
        var target = Assert.Single(sel.Included);
        Assert.Equal("copy", target.Encoder);
        Assert.True(target.IsCopy);
    }

    [Fact]
    public void Mp4_Bitmap_IsExcludedWithWarning()
    {
        var sel = Select(ContainerFormat.Mp4, TestData.Subtitle(codec: "hdmv_pgs_subtitle"));
        Assert.Empty(sel.Included);
        Assert.Single(sel.Warnings);
        Assert.Contains("hdmv_pgs_subtitle", sel.Warnings[0]);
    }

    [Fact]
    public void Mkv_TextSubtitles_StreamCopy()
    {
        foreach (var codec in new[] { "subrip", "ass", "ssa", "webvtt", "text" })
        {
            var sel = Select(ContainerFormat.Mkv, TestData.Subtitle(codec: codec));
            Assert.Equal("copy", Assert.Single(sel.Included).Encoder);
        }
    }

    [Fact]
    public void Mkv_MovText_BecomesSrt()
    {
        var sel = Select(ContainerFormat.Mkv, TestData.Subtitle(codec: "mov_text"));
        Assert.Equal("srt", Assert.Single(sel.Included).Encoder);
    }

    [Fact]
    public void Mkv_Bitmap_StreamCopies()
    {
        foreach (var codec in new[] { "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle" })
        {
            var sel = Select(ContainerFormat.Mkv, TestData.Subtitle(codec: codec));
            Assert.Equal("copy", Assert.Single(sel.Included).Encoder);
        }
    }

    [Fact]
    public void Webm_Webvtt_Copies_OtherText_BecomesWebvtt_Bitmap_Excluded()
    {
        Assert.Equal("copy", Assert.Single(Select(ContainerFormat.Webm, TestData.Subtitle(codec: "webvtt")).Included).Encoder);
        Assert.Equal("webvtt", Assert.Single(Select(ContainerFormat.Webm, TestData.Subtitle(codec: "ass")).Included).Encoder);
        Assert.Empty(Select(ContainerFormat.Webm, TestData.Subtitle(codec: "dvd_subtitle")).Included);
    }

    [Fact]
    public void SelectedStreamIndices_FilterSubtitles()
    {
        var streams = new List<StreamDescriptor>
        {
            TestData.Video(index: 0), TestData.Audio(index: 1),
            TestData.Subtitle(index: 2, codec: "subrip"), TestData.Subtitle(index: 3, codec: "ass"),
        };
        var request = TestData.Request(mode: ConversionMode.VideoTranscode, container: ContainerFormat.Mp4, selected: new[] { 3 });
        var sel = SubtitlePolicy.SelectForTranscode(request, TestData.Source(streams));
        Assert.Equal(3, Assert.Single(sel.Included).Stream.SourceIndex);
    }

    [Fact]
    public void LegacyV1_AudioOnlySelection_StillFiltersSubtitlesOut()
    {
        var streams = new List<StreamDescriptor>
        {
            TestData.Video(index: 0), TestData.Audio(index: 1),
            TestData.Subtitle(index: 2, codec: "subrip"), TestData.Subtitle(index: 3, codec: "ass"),
        };
        var source = TestData.Source(streams);
        var legacy = TestData.Request(selected: new[] { 1 });
        Assert.Equal(1, legacy.RequestVersion);
        Assert.Empty(SubtitlePolicy.SelectForTranscode(legacy, source).Included);
        Assert.Equal(2, SubtitlePolicy.SelectForTranscode(TestData.Request(), source).Included.Count);
    }

    [Fact]
    public void V2_SelectingAnAudioTrack_DoesNotChangeSubtitlePreservation()
    {
        var streams = new List<StreamDescriptor>
        {
            TestData.Video(index: 0), TestData.Audio(index: 1),
            TestData.Subtitle(index: 2, codec: "subrip"), TestData.Subtitle(index: 3, codec: "ass"),
        };
        var request = TestData.Request(requestVersion: 2, audioTracks: AudioSelection.Tracks(1));
        var selection = SubtitlePolicy.SelectForTranscode(request, TestData.Source(streams));
        Assert.Equal(new[] { 2, 3 }, selection.Included.Select(t => t.Stream.SourceIndex));
    }

    [Fact]
    public void V2_SubtitleNone_ExcludesEverythingWhileAudioSelectionIsIndependent()
    {
        var streams = new List<StreamDescriptor>
        {
            TestData.Video(index: 0), TestData.Audio(index: 1), TestData.Audio(index: 3),
            TestData.Subtitle(index: 2, codec: "subrip"),
        };
        var source = TestData.Source(streams);
        var request = TestData.Request(requestVersion: 2,
            audioTracks: AudioSelection.Tracks(3), subtitles: SubtitleSelection.None);
        var resolved = StreamSelectionResolver.Resolve(request, source);
        Assert.Equal(new[] { 3 }, resolved.Audio.Select(a => a.SourceIndex));
        Assert.Empty(SubtitlePolicy.SelectForTranscode(request, source).Included);
    }

    [Fact]
    public void V2_SubtitleExplicit_SelectsOnlyListedTracks()
    {
        var streams = new List<StreamDescriptor>
        {
            TestData.Video(index: 0), TestData.Audio(index: 1),
            TestData.Subtitle(index: 2, codec: "subrip"), TestData.Subtitle(index: 3, codec: "ass"),
        };
        var request = TestData.Request(requestVersion: 2,
            audioTracks: AudioSelection.Auto, subtitles: SubtitleSelection.Tracks(3));
        var selection = SubtitlePolicy.SelectForTranscode(request, TestData.Source(streams));
        Assert.Equal(3, Assert.Single(selection.Included).Stream.SourceIndex);
    }

    [Fact]
    public void V2_AudioNone_ProducesNoAudio_WhileSubtitlesDefaultToAll()
    {
        var streams = new List<StreamDescriptor>
        {
            TestData.Video(index: 0), TestData.Audio(index: 1),
            TestData.Subtitle(index: 2, codec: "subrip"), TestData.Subtitle(index: 3, codec: "ass"),
        };
        var request = TestData.Request(requestVersion: 2, audioTracks: AudioSelection.None);
        var resolved = StreamSelectionResolver.Resolve(request, TestData.Source(streams));
        Assert.Empty(resolved.Audio);
        Assert.Equal(new[] { 2, 3 }, resolved.Subtitles.Select(s => s.SourceIndex));
    }

    [Fact]
    public void V2_VideoExplicit_PinsSingleSourceVideo()
    {
        var v0 = TestData.Video(index: 0, isDefault: true);
        var v1 = TestData.Video(index: 1, isDefault: false);
        var source = TestData.Source(new[] { v0, v1, TestData.Audio(index: 2) });
        var request = TestData.Request(requestVersion: 2, video: VideoSelection.Stream(1));
        Assert.Same(v1, StreamSelectionResolver.Resolve(request, source).Video);
        Assert.Same(v0, StreamSelectionResolver.Resolve(TestData.Request(requestVersion: 2), source).Video);
    }

    [Fact]
    public void TargetEncoder_QualityValues()
    {
        Assert.Equal("mov_text", SubtitlePolicy.TargetEncoder(ContainerFormat.Mp4, TestData.Subtitle(codec: "subrip")));
        Assert.Equal("", SubtitlePolicy.TargetEncoder(ContainerFormat.Mp4, TestData.Subtitle(codec: "dvd_subtitle")));
        Assert.Equal("copy", SubtitlePolicy.TargetEncoder(ContainerFormat.Mkv, TestData.Subtitle(codec: "hdmv_pgs_subtitle")));
    }
}
