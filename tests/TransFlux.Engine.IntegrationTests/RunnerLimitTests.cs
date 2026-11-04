using System.Diagnostics;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Engine.FFmpeg.Runner;

namespace TransFlux.Engine.IntegrationTests;

/// <summary>
/// E4: the runner must never wait forever for a wedged ffmpeg. Short custom limits plus a real
/// ffmpeg process prove the watchdog kills the process tree and returns a distinct failure
/// (run.timeout / run.stalled) rather than a cancellation or a hang.
/// </summary>
public class RunnerLimitTests
{
    private static AttemptContext Attempt(IReadOnlyList<string> arguments)
    {
        return new AttemptContext
        {
            JobId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            Candidate = new ExecutionCandidate
            {
                CandidateId = "test-runner-limits",
                Backend = HardwareBackend.Cpu,
                EncoderName = "libx264",
                Arguments = arguments,
            },
            Source = new SourceSnapshot
            {
                MediaId = Guid.NewGuid(),
                AbsolutePath = "C:\\fake\\runner-limits-input.mp4",
            },
            Request = new ConversionRequest { SourceSnapshotId = Guid.NewGuid() },
            TempPath = Path.Combine(TestSupport.NewWorkDir("runner-limits"), "out.mp4"),
            FfmpegPath = TestSupport.Core.FfmpegPath,
            FfprobePath = TestSupport.Core.FfprobePath,
        };
    }

    /// <summary>
    /// Real-time throttled ffmpeg input: the process would run for an hour, so it cannot finish
    /// before the test's short limits regardless of machine speed or parallel test load.
    /// </summary>
    private static List<string> RealtimeEncodeArgs(string statsPeriod)
    {
        return
        [
            "-hide_banner", "-nostdin", "-v", "error",
            "-progress", "pipe:1", "-stats_period", statsPeriod,
            "-re", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=3600",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-f", "null", "-",
        ];
    }

    [Fact]
    public void Defaults_AreGenerousButBounded()
    {
        var limits = RunnerLimits.Default;
        Assert.Equal(TimeSpan.FromHours(24), limits.MaxRuntime);
        Assert.Equal(TimeSpan.FromMinutes(10), limits.StallTimeout);
        Assert.True(limits.PollInterval > TimeSpan.Zero);
    }

    [Fact]
    public async Task MaxRuntimeExceeded_KillsRealFfmpegAndReturnsRunTimeout()
    {
        var runner = new FfmpegRunner(TestSupport.Core, new RunnerLimits
        {
            MaxRuntime = TimeSpan.FromMilliseconds(1500),
            StallTimeout = TimeSpan.FromMinutes(10),
            PollInterval = TimeSpan.FromMilliseconds(100),
        });
        var attempt = Attempt(RealtimeEncodeArgs("0.2"));
        var stopwatch = Stopwatch.StartNew();

        var run = await runner.RunAsync(attempt, new CollectingProgress(), CancellationToken.None);
        stopwatch.Stop();

        Assert.False(run.Succeeded);
        Assert.False(run.Canceled);
        Assert.NotNull(run.Failure);
        Assert.Equal("run.timeout", run.Failure!.UserMessageKey);
        Assert.Contains("maximum runtime", run.Failure.TechnicalDetail);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"watchdog took {stopwatch.Elapsed}");
        Assert.False(File.Exists(attempt.TempPath));
    }

    [Fact]
    public async Task StallTimeoutExceeded_KillsRealFfmpegAndReturnsRunStalled()
    {
        var runner = new FfmpegRunner(TestSupport.Core, new RunnerLimits
        {
            MaxRuntime = TimeSpan.FromMinutes(5),
            StallTimeout = TimeSpan.FromMilliseconds(800),
            PollInterval = TimeSpan.FromMilliseconds(50),
        });
        // Suppress -progress blocks for the whole encode so the heartbeat stays stale even
        // though ffmpeg itself is busy; this is exactly the failure mode the watchdog covers.
        var attempt = Attempt(RealtimeEncodeArgs("120"));
        var stopwatch = Stopwatch.StartNew();

        var run = await runner.RunAsync(attempt, new CollectingProgress(), CancellationToken.None);
        stopwatch.Stop();

        Assert.False(run.Succeeded);
        Assert.False(run.Canceled);
        Assert.NotNull(run.Failure);
        Assert.Equal("run.stalled", run.Failure!.UserMessageKey);
        Assert.Contains("no -progress heartbeat", run.Failure.TechnicalDetail);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"watchdog took {stopwatch.Elapsed}");
        Assert.False(File.Exists(attempt.TempPath));
    }

    [Fact]
    public async Task OutputDrainWait_IsBounded_WhenPumpNeverCompletes()
    {
        // A child that inherits the stdout/stderr handle can keep the read loop open even after
        // the process tree is killed. The pump wait must abandon it instead of hanging forever.
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        await FfmpegRunner.AwaitBoundAsync(never.Task, TimeSpan.FromMilliseconds(200));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"bounded wait took {stopwatch.Elapsed}");
        Assert.False(never.Task.IsCompleted);
    }

    [Fact]
    public async Task Cancellation_StillReturnsCanceled_EvenWithShortLimits()
    {
        var runner = new FfmpegRunner(TestSupport.Core, new RunnerLimits
        {
            MaxRuntime = TimeSpan.FromSeconds(30),
            StallTimeout = TimeSpan.FromSeconds(30),
            PollInterval = TimeSpan.FromMilliseconds(50),
        });
        var attempt = Attempt(RealtimeEncodeArgs("0.2"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var run = await runner.RunAsync(attempt, new CollectingProgress(), cts.Token);

        Assert.True(run.Canceled);
        Assert.False(run.Succeeded);
        Assert.False(File.Exists(attempt.TempPath));
    }
}
