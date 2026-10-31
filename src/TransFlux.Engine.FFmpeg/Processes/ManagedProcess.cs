using System.Diagnostics;
using System.Text;

namespace TransFlux.Engine.FFmpeg.Processes;

/// <summary>
/// A launched child process that is owned by a Windows Job Object for the duration of its life.
/// The public surface is intentionally minimal so probe / trial-run / transcode / verify all
/// start processes the same hardened way: ArgumentList, no shell, no window, job membership.
/// </summary>
public sealed class ManagedProcess : IDisposable
{
    private readonly JobObjectHandle _job;
    private bool _disposed;

    private ManagedProcess(Process process, JobObjectHandle job)
    {
        Process = process;
        _job = job;
    }

    public Process Process { get; }

    public static ManagedProcess Start(string executable, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        var job = new JobObjectHandle();
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in arguments) psi.ArgumentList.Add(a);

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start '{executable}'.");

            if (!job.TryAssign(process))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw new InvalidOperationException(
                    $"Failed to place '{executable}' (pid {SafePid(process)}) in a job object; refusing to run unmanaged.");
            }

            return new ManagedProcess(process, job);
        }
        catch
        {
            process?.Dispose();
            job.Dispose();
            throw;
        }
    }

    /// <summary>Terminates the whole job (process tree). Safe to call multiple times.</summary>
    public void KillTree()
    {
        _job.Terminate();
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
        Process.Dispose();
        _job.Dispose();
    }

    private static int SafePid(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }
}
