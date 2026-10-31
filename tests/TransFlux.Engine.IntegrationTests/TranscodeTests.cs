using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Engine.FFmpeg.Planner;
using TransFlux.Engine.FFmpeg.Probe;
using TransFlux.Engine.FFmpeg.Runner;
using TransFlux.Engine.FFmpeg.Verifier;

namespace TransFlux.Engine.IntegrationTests;

public class TranscodeTests
{
    private static async Task<(SourceSnapshot Source, string Path)> ProbeAsync(string name, int seconds = 2)
    {
        var path = await TestSupport.GenerateVideoAsync(name, seconds);
        var probe = new FfmpegProbe(TestSupport.Core);
        var r = await probe.ProbeAsync(new SourceReference { AbsolutePath = path }, CancellationToken.None);
        Assert.True(r.IsSuccess, r.Failure?.TechnicalDetail);
        return (r.Value!, path);
    }

    [Fact]
    public async Task Transcode_Mp4_ProducesVerifiedOutputAndPreservesSource()
    {
        var (source, sourcePath) = await ProbeAsync("transcode-mp4-src.mp4");
        var sourceHash = TestSupport.Sha256(sourcePath);
        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mp4);

        var planner = new FfmpegPlanner(TestSupport.Core);
        var planResult = await planner.BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(planResult.IsSuccess, planResult.Failure?.TechnicalDetail);
        var plan = planResult.Value!;

        var attempt = TestSupport.BuildAttempt(plan, source, request);
        var progress = new CollectingProgress();
        var run = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, progress, CancellationToken.None);

        Assert.True(run.Succeeded, run.Failure?.TechnicalDetail ?? run.StderrTail);
        Assert.True(run.OutputBytes is > 0);
        Assert.True(File.Exists(attempt.TempPath));

        var outputProbe = await new FfmpegProbe(TestSupport.Core).ProbeAsync(
            new SourceReference { AbsolutePath = attempt.TempPath }, CancellationToken.None);
        Assert.True(outputProbe.IsSuccess);
        var output = outputProbe.Value!;
        Assert.Equal("h264", output.PrimaryVideo!.Codec);
        Assert.Equal("yuv420p", output.PrimaryVideo.PixelFormat);
        Assert.Equal(320, output.PrimaryVideo.Width);
        Assert.Equal(240, output.PrimaryVideo.Height);
        Assert.Equal("aac", output.PrimaryAudio!.Codec);

        var verification = await new FfmpegVerifier(TestSupport.Core).VerifyAsync(new VerificationRequest
        {
            Plan = plan,
            OutputPath = attempt.TempPath,
            Source = source,
        }, CancellationToken.None);
        Assert.True(verification.Passed, verification.Summary);

        Assert.Equal(sourceHash, TestSupport.Sha256(sourcePath));
        Assert.NotEmpty(progress.Items);
        Assert.Contains(progress.Items, p => p.Fraction is not null || p.ProcessedUs is not null);
    }

    [Fact]
    public async Task Transcode_Mkv_ProducesMatroska()
    {
        var (source, _) = await ProbeAsync("transcode-mkv-src.mp4");
        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mkv);
        var planResult = await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(planResult.IsSuccess, planResult.Failure?.TechnicalDetail);
        var plan = planResult.Value!;

        var attempt = TestSupport.BuildAttempt(plan, source, request);
        var run = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, new CollectingProgress(), CancellationToken.None);
        Assert.True(run.Succeeded, run.Failure?.TechnicalDetail ?? run.StderrTail);

        var outputProbe = await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = attempt.TempPath }, CancellationToken.None);
        Assert.True(outputProbe.IsSuccess);
        Assert.Contains("matroska", outputProbe.Value!.Format.FormatName);
        Assert.Equal("h264", outputProbe.Value.PrimaryVideo!.Codec);
    }

    [Fact]
    public async Task Verifier_FailsWhenExpectedWidthIsWrong()
    {
        var (source, _) = await ProbeAsync("verify-mismatch-src.mp4");
        var request = TestSupport.TranscodeRequest(source);
        var plan = (await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None)).Value!;
        var attempt = TestSupport.BuildAttempt(plan, source, request);
        var run = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, new CollectingProgress(), CancellationToken.None);
        Assert.True(run.Succeeded, run.Failure?.TechnicalDetail);

        var tampered = plan with { ExpectedOutput = plan.ExpectedOutput with { Width = 100 } };
        var verification = await new FfmpegVerifier(TestSupport.Core).VerifyAsync(new VerificationRequest
        {
            Plan = tampered,
            OutputPath = attempt.TempPath,
            Source = source,
        }, CancellationToken.None);
        Assert.False(verification.Passed);
        Assert.NotNull(verification.Failure);
    }

    [Fact]
    public async Task Transcode_RotatedSource_BakesRotationAndClearsTag()
    {
        var basePath = await TestSupport.GenerateVideoAsync("rot-base.mp4", seconds: 2, width: 320, height: 240, withAudio: false);
        var rotatedPath = await TestSupport.GenerateRotatedVideoAsync(basePath, "rot-src.mp4", 90);

        var probe = await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = rotatedPath }, CancellationToken.None);
        Assert.True(probe.IsSuccess, probe.Failure?.TechnicalDetail);
        var source = probe.Value!;
        var rotation = source.PrimaryVideo!.RotationDegrees ?? 0;
        Assert.True(rotation is 90 or 270, $"expected swapped rotation, got {rotation}");

        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mp4) with { Audio = new AudioSpec { Codec = AudioCodec.Aac } };
        var planResult = await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(planResult.IsSuccess, planResult.Failure?.TechnicalDetail);
        var plan = planResult.Value!;
        Assert.Equal(240, plan.ExpectedOutput.Width);
        Assert.Equal(320, plan.ExpectedOutput.Height);

        var attempt = TestSupport.BuildAttempt(plan, source, request);
        var run = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, new CollectingProgress(), CancellationToken.None);
        Assert.True(run.Succeeded, run.Failure?.TechnicalDetail ?? run.StderrTail);

        var outputProbe = await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = attempt.TempPath }, CancellationToken.None);
        Assert.True(outputProbe.IsSuccess);
        var output = outputProbe.Value!;
        Assert.Equal(240, output.PrimaryVideo!.Width);
        Assert.Equal(320, output.PrimaryVideo.Height);
        Assert.True((output.PrimaryVideo.RotationDegrees ?? 0) == 0, "rotation metadata should be cleared after baking");

        var verification = await new FfmpegVerifier(TestSupport.Core).VerifyAsync(new VerificationRequest
        {
            Plan = plan,
            OutputPath = attempt.TempPath,
            Source = source,
        }, CancellationToken.None);
        Assert.True(verification.Passed, verification.Summary);
    }

    [Fact]
    public async Task Transcode_Trim_ProducesExpectedDuration()
    {
        var srcPath = await TestSupport.GenerateVideoAsync("trim-src.mp4", seconds: 4, width: 320, height: 240);
        var probe = await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = srcPath }, CancellationToken.None);
        Assert.True(probe.IsSuccess);
        var source = probe.Value!;
        var known = source.DurationUs is long d ? TimeSpan.FromMicroseconds(d) : (TimeSpan?)null;

        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mp4) with
        {
            Trim = TrimRange.Create(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), known),
        };
        var planResult = await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(planResult.IsSuccess, planResult.Failure?.TechnicalDetail);
        var plan = planResult.Value!;
        Assert.Equal(1_000_000, plan.ExpectedOutput.DurationUs);

        var attempt = TestSupport.BuildAttempt(plan, source, request);
        var run = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, new CollectingProgress(), CancellationToken.None);
        Assert.True(run.Succeeded, run.Failure?.TechnicalDetail ?? run.StderrTail);

        var outputProbe = await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = attempt.TempPath }, CancellationToken.None);
        Assert.True(outputProbe.IsSuccess);
        var durationUs = outputProbe.Value!.DurationUs!.Value;
        Assert.InRange(durationUs, 800_000, 1_300_000);

        var verification = await new FfmpegVerifier(TestSupport.Core).VerifyAsync(new VerificationRequest
        {
            Plan = plan,
            OutputPath = attempt.TempPath,
            Source = source,
        }, CancellationToken.None);
        Assert.True(verification.Passed, verification.Summary);
    }
}
