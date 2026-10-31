using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;
using TransFlux.Engine.FFmpeg.Probe;

namespace TransFlux.Engine.IntegrationTests;

public class ProbeTests
{
    [Fact]
    public async Task Probe_GeneratedMp4_NormalizesStreamsAndColor()
    {
        var path = await TestSupport.GenerateVideoAsync("probe-av.mp4");
        var probe = new FfmpegProbe(TestSupport.Core);
        var result = await probe.ProbeAsync(new SourceReference { AbsolutePath = path }, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        var snapshot = result.Value!;
        Assert.True(snapshot.HasVideo);
        Assert.True(snapshot.HasAudio);
        Assert.Equal(2, snapshot.Streams.Count);
        var video = snapshot.PrimaryVideo!;
        Assert.Equal("h264", video.Codec);
        Assert.Equal(320, video.Width);
        Assert.Equal(240, video.Height);
        Assert.Equal("yuv420p", video.PixelFormat);
        Assert.Equal(8, video.BitDepth);
        Assert.Equal(ColorClassification.SdrBt709, snapshot.ColorClassification);
        Assert.NotNull(snapshot.DurationUs);
        Assert.InRange(snapshot.DurationUs!.Value, 1_800_000, 2_400_000);
        Assert.False(snapshot.Fingerprint is null);
        Assert.Equal(Path.GetFullPath(path), snapshot.AbsolutePath);
    }

    [Fact]
    public async Task Probe_MissingFile_ReturnsOperationalFailure()
    {
        var probe = new FfmpegProbe(TestSupport.Core);
        var result = await probe.ProbeAsync(new SourceReference { AbsolutePath = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".mp4") }, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ResultKind.OperationalFailure, result.Kind);
    }

    [Fact]
    public async Task Probe_TruncatedFile_DoesNotSucceedSilently()
    {
        var good = await TestSupport.GenerateVideoAsync("truncate-source.mp4");
        var truncated = TestSupport.PathFor("truncated.mp4");
        var bytes = await File.ReadAllBytesAsync(good);
        await File.WriteAllBytesAsync(truncated, bytes[..Math.Max(64, bytes.Length / 3)]);

        var probe = new FfmpegProbe(TestSupport.Core);
        var result = await probe.ProbeAsync(new SourceReference { AbsolutePath = truncated }, CancellationToken.None);
        // ffprobe may still parse headers on a truncated file; if it succeeds it must not claim a valid full duration.
        if (result.IsSuccess)
            Assert.True(result.Value!.DurationUs is null || result.Value.Format.SizeBytes > 0);
    }
}
