using System.Diagnostics;
using System.Text;

namespace TransFlux.Engine.FFmpeg.Processes;

public sealed record CaptureResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    bool TimedOut);

/// <summary>Helpers to run a managed child process to completion with bounded output capture.</summary>
public static class ProcessCapture
{
    /// <summary>Runs a process to completion, capturing stdout/stderr up to byte/char caps.</summary>
    public static async Task<CaptureResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maxStdoutChars,
        int maxStderrChars,
        CancellationToken ct)
    {
        using var managed = ManagedProcess.Start(executable, arguments);
        var proc = managed.Process;

        var stdoutTask = ReadBoundedAsync(proc.StandardOutput, maxStdoutChars, ct);
        var stderrTask = ReadBoundedAsync(proc.StandardError, maxStderrChars, ct);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            managed.KillTree();
            try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        var stdout = await AwaitSafe(stdoutTask).ConfigureAwait(false);
        var stderr = await AwaitSafe(stderrTask).ConfigureAwait(false);

        int exit;
        try { exit = proc.HasExited ? proc.ExitCode : -1; } catch { exit = -1; }

        return new CaptureResult(exit, stdout.Text, stderr.Text, stdout.Truncated, stderr.Truncated, timedOut);
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
