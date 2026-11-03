using System.Diagnostics;
using TransFlux.Engine.FFmpeg.Processes;

namespace TransFlux.Engine.IntegrationTests;

/// <summary>
/// R09: cancellation must be a first-class capture outcome, distinct from timeout and failure.
/// Uses short-lived real Windows child processes (cmd.exe + ping) so these tests do not
/// depend on the media core or any external sample.
/// </summary>
public class ProcessCaptureTests
{
    private static string Cmd => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    [Fact]
    public async Task RunAsync_PreCanceledToken_ReturnsCanceledWithoutTimingOut()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await ProcessCapture.RunAsync(
            Cmd, ["/c", "ping -n 30 127.0.0.1 > nul"],
            TimeSpan.FromSeconds(30), 64 * 1024, 64 * 1024, cts.Token);

        Assert.True(result.Canceled);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_CancelDuringRun_ReturnsCanceledNotTimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(700));

        var stopwatch = Stopwatch.StartNew();
        var result = await ProcessCapture.RunAsync(
            Cmd, ["/c", "echo cancel-output & ping -n 60 127.0.0.1 > nul"],
            TimeSpan.FromSeconds(60), 64 * 1024, 64 * 1024, cts.Token);
        stopwatch.Stop();

        Assert.True(result.Canceled);
        Assert.False(result.TimedOut);
        // Output emitted before the kill must still be drained, and the read must not hang.
        Assert.Contains("cancel-output", result.Stdout);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"cancellation should terminate the process tree promptly, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_Timeout_ReturnsTimedOutNotCanceled()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await ProcessCapture.RunAsync(
            Cmd, ["/c", "ping -n 60 127.0.0.1 > nul"],
            TimeSpan.FromMilliseconds(700), 64 * 1024, 64 * 1024, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(result.TimedOut);
        Assert.False(result.Canceled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"timeout should terminate the process tree promptly, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_NormalExit_CapturesOutputAndIsNeitherCanceledNorTimedOut()
    {
        var result = await ProcessCapture.RunAsync(
            Cmd, ["/c", "echo capture-ok"],
            TimeSpan.FromSeconds(30), 64 * 1024, 64 * 1024, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Canceled);
        Assert.Contains("capture-ok", result.Stdout);
    }
}
