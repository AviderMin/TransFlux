using TransFlux.Domain.Media;
using TransFlux.Engine.FFmpeg.Probe;

namespace TransFlux.Engine.IntegrationTests;

public class ColorAndParserTests
{
    private static StreamDescriptor Video(string pixFmt, int bitDepth, string? transfer = null,
        string? primaries = null, string? space = null, bool hdrSide = false, string? side = null)
        => new()
        {
            SourceIndex = 0,
            Kind = MediaKind.Video,
            Codec = "h264",
            Width = 1920,
            Height = 1080,
            PixelFormat = pixFmt,
            BitDepth = bitDepth,
            ColorTransfer = transfer,
            ColorPrimaries = primaries,
            ColorSpace = space,
            HasHdrSideData = hdrSide,
            SideDataSummary = side,
        };

    [Fact]
    public void Plain8BitYuv420p_NoTags_IsSdrBt709()
        => Assert.Equal(ColorClassification.SdrBt709, ColorClassifier.Classify(Video("yuv420p", 8)));

    [Fact]
    public void ExplicitBt709_IsSdrBt709()
        => Assert.Equal(ColorClassification.SdrBt709, ColorClassifier.Classify(Video("yuv420p", 8, "bt709", "bt709", "bt709")));

    [Fact]
    public void TenBit_IsBlockedClass()
        => Assert.Equal(ColorClassification.WideGamut, ColorClassifier.Classify(Video("yuv420p10le", 10)));

    [Fact]
    public void PqTransfer_IsHdr10()
        => Assert.Equal(ColorClassification.Hdr10, ColorClassifier.Classify(Video("yuv420p10le", 10, "smpte2084", "bt2020", "bt2020nc")));

    [Fact]
    public void Bt2020_IsWideGamut()
        => Assert.Equal(ColorClassification.WideGamut, ColorClassifier.Classify(Video("yuv420p10le", 10, "bt709", "bt2020", "bt2020nc")));

    [Fact]
    public void DolbyVisionSideData_IsDolbyVision()
        => Assert.Equal(ColorClassification.DolbyVision, ColorClassifier.Classify(Video("yuv420p", 8, hdrSide: true, side: "DOVI configuration record")));

    [Fact]
    public void Bt601_IsSdrBt601()
        => Assert.Equal(ColorClassification.SdrBt601, ColorClassifier.Classify(Video("yuv420p", 8, "smpte170m", "smpte170m", "smpte170m")));

    [Fact]
    public void HdrMasteringSideData_IsHdr10()
        => Assert.Equal(ColorClassification.Hdr10, ColorClassifier.Classify(Video("yuv420p", 8, hdrSide: true, side: "Mastering display metadata; Content light level metadata")));

    [Fact]
    public void RotationNormalization_ClampsToQuadrants()
    {
        Assert.Equal(270, FfprobeJsonParser.NormalizeClockwiseRotation(-90));
        Assert.Equal(90, FfprobeJsonParser.NormalizeClockwiseRotation(90));
        Assert.Equal(180, FfprobeJsonParser.NormalizeClockwiseRotation(180));
        Assert.Equal(0, FfprobeJsonParser.NormalizeClockwiseRotation(0));
        Assert.Equal(0, FfprobeJsonParser.NormalizeClockwiseRotation(360));
    }

    [Fact]
    public void Parser_NormalizesMinimalProbeJson()
    {
        const string json = """
        {
          "streams": [
            { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 320, "height": 240,
              "pix_fmt": "yuv420p", "sample_aspect_ratio": "1:1", "r_frame_rate": "30/1",
              "avg_frame_rate": "30/1", "bits_per_raw_sample": "8", "duration": "2.000000",
              "disposition": { "default": 1 } },
            { "index": 1, "codec_name": "aac", "codec_type": "audio", "channels": 2,
              "channel_layout": "stereo", "sample_rate": "48000", "duration": "2.000000" }
          ],
          "chapters": [],
          "format": { "format_name": "mov,mp4", "duration": "2.000000", "size": "46930", "bit_rate": "187720" }
        }
        """;
        var snapshot = FfprobeJsonParser.Parse(json, "C:\\x\\a.mp4", 46930, DateTimeOffset.UtcNow, Guid.NewGuid());
        Assert.Equal(2, snapshot.Streams.Count);
        Assert.True(snapshot.HasVideo);
        Assert.True(snapshot.HasAudio);
        Assert.Equal(2_000_000, snapshot.DurationUs);
        Assert.Equal("h264", snapshot.PrimaryVideo!.Codec);
        Assert.Equal(30, snapshot.PrimaryVideo.FrameRate!.Value.Numerator);
        Assert.Equal(ColorClassification.SdrBt709, snapshot.ColorClassification);
    }
}
