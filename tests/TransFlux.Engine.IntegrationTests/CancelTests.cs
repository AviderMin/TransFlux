using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Engine.FFmpeg.Planner;
using TransFlux.Engine.FFmpeg.Probe;
using TransFlux.Engine.FFmpeg.Runner;

namespace TransFlux.Engine.IntegrationTests;

public class CancelTests
{
    [Fact]
    public async Task Cancel_DuringEncode_ReturnsCanceledAndRemovesPartialOutput()
    {
        // A moderately sized sample keeps the encode running long enough to cancel mid-flight.
        var srcPath = await TestSupport.GenerateVideoAsync("cancel-src.mp4", seconds: 20, width: 1280, height: 720);
        var probe = await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = srcPath }, CancellationToken.None);
        Assert.True(probe.IsSuccess, probe.Failure?.TechnicalDetail);
        var source = probe.Value!;

        var request = TestSupport.TranscodeRequest(source);
        var planResult = await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(planResult.IsSuccess, planResult.Failure?.TechnicalDetail);
        var attempt = TestSupport.BuildAttempt(planResult.Value!, source, request);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(600));
        var run = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, new CollectingProgress(), cts.Token);

        Assert.True(run.Canceled);
        Assert.False(run.Succeeded);
        Assert.False(File.Exists(attempt.TempPath));
    }

    [Fact]
    public async Task Cancel_PreCanceledToken_ReturnsCanceled()
    {
        var srcPath = await TestSupport.GenerateVideoAsync("cancel-pre-src.mp4", seconds: 2, width: 320, height: 240);
        var probe = await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = srcPath }, CancellationToken.None);
        var source = probe.Value!;
        var request = TestSupport.TranscodeRequest(source);
        var plan = (await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None)).Value!;
        var attempt = TestSupport.BuildAttempt(plan, source, request);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var run = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, new CollectingProgress(), cts.Token);
        Assert.True(run.Canceled);
    }
}
