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
    public void TargetEncoder_QualityValues()
    {
        Assert.Equal("mov_text", SubtitlePolicy.TargetEncoder(ContainerFormat.Mp4, TestData.Subtitle(codec: "subrip")));
        Assert.Equal("", SubtitlePolicy.TargetEncoder(ContainerFormat.Mp4, TestData.Subtitle(codec: "dvd_subtitle")));
        Assert.Equal("copy", SubtitlePolicy.TargetEncoder(ContainerFormat.Mkv, TestData.Subtitle(codec: "hdmv_pgs_subtitle")));
    }
}
