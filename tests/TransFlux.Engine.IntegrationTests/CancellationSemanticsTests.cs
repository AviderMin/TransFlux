using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Engine.FFmpeg.Capabilities;
using TransFlux.Engine.FFmpeg.Planner;
using TransFlux.Engine.FFmpeg.Probe;
using TransFlux.Engine.FFmpeg.Verifier;

namespace TransFlux.Engine.IntegrationTests;

/// <summary>
/// R09: every consumer of ProcessCapture must surface cancellation as cancellation instead of
/// an ordinary probe/verify/trial failure. Timeouts keep their existing failure semantics.
/// </summary>
public class CancellationSemanticsTests
{
    [Fact]
    public async Task Probe_PreCanceledToken_ReturnsCanceledResult()
    {
        var path = await TestSupport.GenerateVideoAsync("probe-cancel.mp4", seconds: 1, width: 160, height: 120);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new FfmpegProbe(TestSupport.Core)
            .ProbeAsync(new SourceReference { AbsolutePath = path }, cts.Token);

        Assert.True(result.IsCanceled);
        Assert.Equal(ResultKind.Canceled, result.Kind);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task Probe_Timeout_RemainsProbeFailedNotCanceled()
    {
        var path = await TestSupport.GenerateVideoAsync("probe-timeout.mp4", seconds: 1, width: 160, height: 120);
        var probe = new FfmpegProbe(TestSupport.Core, TimeSpan.FromMilliseconds(1));

        var result = await probe.ProbeAsync(new SourceReference { AbsolutePath = path }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsCanceled);
        Assert.Equal(ResultKind.OperationalFailure, result.Kind);
        Assert.Contains("timed out", result.Failure?.TechnicalDetail ?? string.Empty);
    }

    [Fact]
    public async Task Verify_PreCanceledToken_ThrowsOperationCanceledNotVerifyFailure()
    {
        var path = await TestSupport.GenerateVideoAsync("verify-cancel.mp4", seconds: 1, width: 160, height: 120);
        var source = (await new FfmpegProbe(TestSupport.Core)
            .ProbeAsync(new SourceReference { AbsolutePath = path }, CancellationToken.None)).Value!;
        var request = TestSupport.TranscodeRequest(source);
        var plan = (await new FfmpegPlanner(TestSupport.Core)
            .BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None)).Value!;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FfmpegVerifier(TestSupport.Core).VerifyAsync(new VerificationRequest
            {
                Plan = plan,
                OutputPath = path,
                Source = source,
            }, cts.Token));

        Assert.True(ex.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Verifier_SampleDecode_PreCanceledToken_ReturnsCanceledNotFailure()
    {
        var path = await TestSupport.GenerateVideoAsync("decode-cancel.mp4", seconds: 1, width: 160, height: 120);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var actual = new TransFlux.Domain.Media.SourceSnapshot { MediaId = Guid.NewGuid(), AbsolutePath = path };
        var result = await new FfmpegVerifier(TestSupport.Core).SampleDecodeAsync(path, actual, full: false, cts.Token);

        Assert.False(result.Ok);
        Assert.True(result.Canceled);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Capabilities_ValidateEncoder_PreCanceledToken_ThrowsOperationCanceled()
    {
        var caps = new FfmpegEngineCapabilities(TestSupport.Core);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => caps.ValidateEncoderAsync(
            new HardwareValidationRequest
            {
                EncoderName = "libx264",
                Width = 160,
                Height = 120,
                PixelFormat = "yuv420p",
                Timeout = TimeSpan.FromSeconds(15),
            }, cts.Token));

        Assert.True(ex.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Capabilities_RunListAsync_PreCanceledToken_ThrowsOperationCanceled()
    {
        var caps = new FfmpegEngineCapabilities(TestSupport.Core);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            caps.RunListAsync(["-encoders"], TimeSpan.FromSeconds(20), cts.Token));

        Assert.True(ex.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Capabilities_RunHwaccelsAsync_PreCanceledToken_ThrowsOperationCanceled()
    {
        var caps = new FfmpegEngineCapabilities(TestSupport.Core);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            caps.RunHwaccelsAsync(TimeSpan.FromSeconds(20), cts.Token));

        Assert.True(ex.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Capabilities_QueryAsync_Canceled_DoesNotCachePartialCapabilities()
    {
        var caps = new FfmpegEngineCapabilities(TestSupport.Core);
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caps.QueryAsync(cts.Token));
        }

        var info = await caps.QueryAsync(CancellationToken.None);
        Assert.Contains("libx264", info.Encoders);
        Assert.NotEmpty(info.Hwaccels);
    }
}
