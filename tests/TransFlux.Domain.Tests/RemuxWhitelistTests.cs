using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.Domain.Tests;

/// <summary>Regression tests for the expanded stream-copy remux whitelist and the
/// graded Dolby Vision rule (task-2 items 4d and 6).</summary>
public class RemuxWhitelistTests
{
    private static bool Has(IReadOnlyList<CompatibilityIssue> issues, string code) =>
        issues.Any(i => i.Code == code);

    private static IReadOnlyList<CompatibilityIssue> ValidateRemux(ContainerFormat container, StreamDescriptor[] streams)
        => MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: container),
            TestData.Source(streams));

    [Theory]
    [InlineData("ac3")]
    [InlineData("eac3")]
    [InlineData("truehd")]
    [InlineData("mlp")]
    [InlineData("dts")]
    [InlineData("mp2")]
    [InlineData("pcm_s32le")]
    public void Remux_DolbyAndLegacyAudio_ToMkv_IsAllowed(string codec)
    {
        var issues = ValidateRemux(ContainerFormat.Mkv, new[] { TestData.Video(), TestData.Audio(codec: codec) });
        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("ac3")]
    [InlineData("eac3")]
    public void Remux_Ac3Family_ToMp4_IsAllowed(string codec)
    {
        var issues = ValidateRemux(ContainerFormat.Mp4, new[] { TestData.Video(), TestData.Audio(codec: codec) });
        Assert.Empty(issues);
    }

    [Fact]
    public void Remux_Truehd_ToMp4_IsBlocked()
    {
        var issues = ValidateRemux(ContainerFormat.Mp4, new[] { TestData.Video(), TestData.Audio(codec: "truehd") });
        Assert.True(Has(issues, "REMUX_AUDIO"));
    }

    [Theory]
    [InlineData("mpeg2video")]
    [InlineData("mpeg4")]
    [InlineData("vc1")]
    [InlineData("prores")]
    [InlineData("theora")]
    [InlineData("vp8")]
    [InlineData("ffv1")]
    public void Remux_MkvCompatibleVideo_ToMkv_IsAllowed(string codec)
    {
        var issues = ValidateRemux(ContainerFormat.Mkv, new[] { TestData.Video(codec: codec), TestData.Audio() });
        Assert.Empty(issues);
    }

    [Fact]
    public void Remux_DvdVobStyle_Mpeg2Ac3_ToMkv_IsAllowed()
    {
        var issues = ValidateRemux(ContainerFormat.Mkv, new[]
        {
            TestData.Video(codec: "mpeg2video"), TestData.Audio(codec: "ac3"),
        });
        Assert.Empty(issues);
    }

    [Fact]
    public void Remux_Mpeg2Video_ToMp4_IsBlocked()
    {
        var issues = ValidateRemux(ContainerFormat.Mp4, new[] { TestData.Video(codec: "mpeg2video"), TestData.Audio() });
        Assert.True(Has(issues, "REMUX_VIDEO"));
    }

    [Fact]
    public void Remux_V2_InvalidVideoSelection_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv,
                requestVersion: 2, video: VideoSelection.Stream(99)), src);
        Assert.True(Has(issues, "VIDEO_SELECTION"));
    }

    [Fact]
    public void Remux_V2_ValidExplicitVideo_IsAllowed()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv,
                requestVersion: 2, video: VideoSelection.Stream(0)), src);
        Assert.Empty(issues);
    }

    // ---------------- Dolby Vision grading ----------------

    [Fact]
    public void Remux_DolbyVision_P8_ToMkv_IsAllowed()
    {
        var src = TestData.Source(
            new[] { TestData.Video(dvProfile: 8, dvBlCompat: 1), TestData.Audio() },
            ColorClassification.DolbyVision);
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);
        Assert.Empty(issues);
    }

    [Fact]
    public void Remux_DolbyVision_P5_ToMkv_IsBlocked()
    {
        var src = TestData.Source(
            new[] { TestData.Video(dvProfile: 5, dvBlCompat: 0), TestData.Audio() },
            ColorClassification.DolbyVision);
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);
        Assert.True(Has(issues, "REMUX_DV"));
    }

    [Fact]
    public void Remux_DolbyVision_P8_ToMp4_IsBlocked()
    {
        var src = TestData.Source(
            new[] { TestData.Video(dvProfile: 8, dvBlCompat: 1), TestData.Audio() },
            ColorClassification.DolbyVision);
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mp4), src);
        Assert.True(Has(issues, "REMUX_DV"));
    }

    [Fact]
    public void CanRemuxDolbyVision_Profile7WithCompatId_IsBlocked()
    {
        Assert.False(MediaCompatibility.CanRemuxDolbyVision(
            TestData.Video(dvProfile: 7, dvBlCompat: 1), ContainerFormat.Mkv, out var reason));
        Assert.Contains("Profile 7", reason);
    }

    [Fact]
    public void CanRemuxDolbyVision_Profile8WithoutCompatId_IsBlocked()
    {
        Assert.False(MediaCompatibility.CanRemuxDolbyVision(
            TestData.Video(dvProfile: 8, dvBlCompat: 0), ContainerFormat.Mkv, out var reason));
        Assert.Contains("Profile 8", reason);
    }

    [Fact]
    public void Remux_DolbyVision_P7Compat1_ToMkv_IsBlocked()
    {
        var src = TestData.Source(
            new[] { TestData.Video(dvProfile: 7, dvBlCompat: 1), TestData.Audio() },
            ColorClassification.DolbyVision);
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);
        Assert.True(Has(issues, "REMUX_DV"));
    }

    [Fact]
    public void Remux_Legacy_NonDvPrimaryWithDvSecondary_IsBlocked()
    {
        var src = TestData.Source(new[]
        {
            TestData.Video(index: 0, color: ColorClassification.SdrBt709),
            TestData.Video(index: 1, isDefault: false, dvProfile: 5, dvBlCompat: 0,
                color: ColorClassification.DolbyVision),
            TestData.Audio(index: 2),
        });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);

        Assert.True(Has(issues, "REMUX_DV"));
    }

    [Fact]
    public void Remux_Legacy_NonDvPrimaryWithValidDvSecondary_IsAllowed()
    {
        var src = TestData.Source(new[]
        {
            TestData.Video(index: 0, color: ColorClassification.SdrBt709),
            TestData.Video(index: 1, isDefault: false, dvProfile: 8, dvBlCompat: 1,
                color: ColorClassification.DolbyVision),
            TestData.Audio(index: 2),
        });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);

        Assert.DoesNotContain(issues, i => i.Code == "REMUX_DV");
    }

    [Fact]
    public void Remux_V2_ExplicitDvSecondarySelection_IsBlocked()
    {
        var src = TestData.Source(new[]
        {
            TestData.Video(index: 0, color: ColorClassification.SdrBt709),
            TestData.Video(index: 1, isDefault: false, dvProfile: 5, dvBlCompat: 0,
                color: ColorClassification.DolbyVision),
            TestData.Audio(index: 2),
        });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv,
                requestVersion: 2, video: VideoSelection.Stream(1)), src);

        Assert.True(Has(issues, "REMUX_DV"));
    }

    [Fact]
    public void CanRemuxDolbyVision_GradingReasons()
    {
        Assert.True(MediaCompatibility.CanRemuxDolbyVision(TestData.Video(dvProfile: 8, dvBlCompat: 2), ContainerFormat.Mkv, out var r1));
        Assert.Null(r1);
        Assert.False(MediaCompatibility.CanRemuxDolbyVision(TestData.Video(dvProfile: 5), ContainerFormat.Mkv, out var r2));
        Assert.Contains("Profile 5", r2);
        Assert.False(MediaCompatibility.CanRemuxDolbyVision(TestData.Video(), ContainerFormat.Mkv, out var r3));
        Assert.Contains("无法识别", r3);
        Assert.False(MediaCompatibility.CanRemuxDolbyVision(TestData.Video(dvProfile: 8, dvBlCompat: 1), ContainerFormat.Mp4, out var r4));
        Assert.Contains("MKV", r4);
    }
}
