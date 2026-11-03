using System.Diagnostics;
using System.Text;

namespace TransFlux.Engine.FFmpeg.Processes;

public sealed record CaptureResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    bool TimedOut,
    bool Canceled = false);

/// <summary>Helpers to run a managed child process to completion with bounded output capture.</summary>
public static class ProcessCapture
{
    /// <summary>Upper bound for draining output after the process tree is gone.</summary>
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Runs a process to completion, capturing stdout/stderr up to byte/char caps.</summary>
    public static async Task<CaptureResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maxStdoutChars,
        int maxStderrChars,
        CancellationToken ct)
    {
        // Cancellation before launch is a normal "canceled" outcome, not an exception or a failure.
        if (ct.IsCancellationRequested)
            return new CaptureResult(-1, string.Empty, string.Empty, false, false, TimedOut: false, Canceled: true);

        using var managed = ManagedProcess.Start(executable, arguments);
        var proc = managed.Process;

        // Drain independently of the caller token: after a cancel/timeout we still want the output
        // produced up to the moment the process tree was torn down. The streams end when the tree is
        // gone; a bounded drain below guarantees a stray inherited pipe handle can never hang the call.
        using var readCts = new CancellationTokenSource();
        var stdoutTask = ReadBoundedAsync(proc.StandardOutput, maxStdoutChars, readCts.Token);
        var stderrTask = ReadBoundedAsync(proc.StandardError, maxStderrChars, readCts.Token);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var timedOut = false;
        var canceled = false;
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation and timeout are distinct outcomes: only the latter is a failure.
            canceled = ct.IsCancellationRequested;
            timedOut = !canceled;
            managed.KillTree();
            try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(OutputDrainTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Timeout or a broken pipe: abandon the reader and keep the text captured so far.
            readCts.Cancel();
        }

        var stdout = await AwaitSafe(stdoutTask).ConfigureAwait(false);
        var stderr = await AwaitSafe(stderrTask).ConfigureAwait(false);

        int exit;
        try { exit = proc.HasExited ? proc.ExitCode : -1; } catch { exit = -1; }

        // A cancellation that lands while the process is already exiting still counts as canceled:
        // callers must never publish a result whose owning job was canceled.
        if (ct.IsCancellationRequested)
        {
            canceled = true;
            timedOut = false;
        }

        return new CaptureResult(exit, stdout.Text, stderr.Text, stdout.Truncated, stderr.Truncated, timedOut, canceled);
    }

    private readonly record struct BoundedText(string Text, bool Truncated);

    private static async Task<BoundedText> ReadBoundedAsync(StreamReader reader, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                if (sb.Length < maxChars)
                {
                    var take = Math.Min(read, maxChars - sb.Length);
                    sb.Append(buffer, 0, take);
                    if (take < read) truncated = true;
                }
                else
                {
                    truncated = true; // drain and discard beyond the cap
                }
            }
        }
        catch (OperationCanceledException) { /* process was killed */ }
        catch (ObjectDisposedException) { }
        return new BoundedText(sb.ToString(), truncated);
    }

    private static async Task<BoundedText> AwaitSafe(Task<BoundedText> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return new BoundedText(string.Empty, false); }
    }
}
