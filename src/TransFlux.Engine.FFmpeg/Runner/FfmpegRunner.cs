using System.Diagnostics;
using System.Globalization;
using System.Text;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Engine.FFmpeg.Core;
using TransFlux.Domain.Plans;
using TransFlux.Engine.FFmpeg.Processes;

namespace TransFlux.Engine.FFmpeg.Runner;

/// <summary>
/// Executes one attempt's compiled argument list inside a Job Object, parses structured
/// <c>-progress pipe:1</c> output and maps it to <see cref="EngineProgress"/>. Cancellation
/// terminates the whole process tree. Never reports success without a non-empty output file.
/// </summary>
public sealed class FfmpegRunner : IMediaRunner
{
    public const int MaxStderrTailChars = 64 * 1024;
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(250);

    private readonly FfmpegCorePaths _core;

    public FfmpegRunner(FfmpegCorePaths core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
    }

    public async Task<RunResult> RunAsync(AttemptContext attempt, IProgress<EngineProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var stopwatch = Stopwatch.StartNew();
        var executable = string.IsNullOrWhiteSpace(attempt.FfmpegPath) ? _core.FfmpegPath : attempt.FfmpegPath;
        var arguments = Substitute(attempt);

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(attempt.TempPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }
        catch { /* muxer will surface a clearer error */ }

        var durationUs = attempt.EffectiveDurationUs ?? attempt.Source.DurationUs;
        var stderrTail = new BoundedTail(MaxStderrTailChars);

        try
        {
            using var managed = ManagedProcess.Start(executable, arguments);
            var process = managed.Process;

            Task stdoutPump = PumpStdoutAsync(process.StandardOutput, progress, durationUs, ct);
            Task stderrPump = PumpStderrAsync(process.StandardError, stderrTail, ct);

            var canceled = false;
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                managed.KillTree();
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }

            await SafeAsync(stdoutPump).ConfigureAwait(false);
            await SafeAsync(stderrPump).ConfigureAwait(false);
            stopwatch.Stop();

            if (canceled || ct.IsCancellationRequested)
            {
                TryDelete(attempt.TempPath);
                return RunResult.CanceledResult(stopwatch.Elapsed);
            }

            int exitCode;
            try { exitCode = process.HasExited ? process.ExitCode : -1; } catch { exitCode = -1; }

            long? bytes = null;
            try
            {
                var info = new FileInfo(attempt.TempPath);
                if (info.Exists) bytes = info.Length;
            }
            catch { }

            if (exitCode == 0 && bytes is > 0)
                return RunResult.Ok(exitCode, stopwatch.Elapsed, bytes, Tail(stderrTail));

            var failure = ClassifyFailure(exitCode, stderrTail.ToString(), bytes, attempt.Candidate.Backend != HardwareBackend.Cpu);
            TryDelete(attempt.TempPath);
            return RunResult.Fail(exitCode, stopwatch.Elapsed, failure, Tail(stderrTail));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            TryDelete(attempt.TempPath);
            return RunResult.CanceledResult(stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TryDelete(attempt.TempPath);
            return RunResult.Fail(null, stopwatch.Elapsed,
                FailureInfo.Of(FailureCode.ProcessFailed, "run.start_failed", ex.Message), null);
        }
    }

    private static List<string> Substitute(AttemptContext attempt)
    {
        var input = attempt.Source.AbsolutePath;
        var output = attempt.TempPath;
        var list = new List<string>(attempt.Candidate.Arguments.Count);
        foreach (var a in attempt.Candidate.Arguments)
        {
            if (a == PlanPlaceholders.Input) list.Add(input);
            else if (a == PlanPlaceholders.Output) list.Add(output);
            else list.Add(a);
        }
        return list;
    }

    private static async Task PumpStdoutAsync(StreamReader reader, IProgress<EngineProgress> progress, long? durationUs, CancellationToken ct)
    {
        var block = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastEmit = Stopwatch.StartNew();
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                var index = line.IndexOf('=');
                if (index <= 0) continue;
                var key = line[..index].Trim();
                var value = line[(index + 1)..].Trim();

                if (!string.Equals(key, "progress", StringComparison.Ordinal))
                {
                    block[key] = value;
                    continue;
                }

                var isEnd = string.Equals(value, "end", StringComparison.OrdinalIgnoreCase);
                if (lastEmit.Elapsed >= ProgressThrottle || isEnd)
                {
                    var sample = BuildProgress(block, durationUs);
                    if (sample is not null) progress?.Report(sample);
                    lastEmit.Restart();
                }
                block.Clear();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private static EngineProgress? BuildProgress(IReadOnlyDictionary<string, string> block, long? durationUs)
    {
        long? processedUs = null;
        if (block.TryGetValue("out_time_us", out var us) && long.TryParse(us, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pUs))
            processedUs = pUs;
        else if (block.TryGetValue("out_time_ms", out var ms) && long.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pMs))
            processedUs = pMs; // historically microseconds in ffmpeg; verified against out_time below when possible
        else if (block.TryGetValue("out_time", out var text) && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts))
            processedUs = (long)ts.TotalMicroseconds;

        double? speed = null;
        if (block.TryGetValue("speed", out var speedText))
        {
            var cleaned = speedText.TrimEnd('x', 'X');
            if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
                speed = s;
        }

        long? totalSize = null;
        if (block.TryGetValue("total_size", out var sizeText)
            && long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size >= 0)
            totalSize = size;

        double? fraction = null;
        TimeSpan? eta = null;
        if (processedUs is long p && durationUs is long d && d > 0)
        {
            fraction = Math.Clamp((double)p / d, 0d, 1d);
            if (speed is double sp && sp > 0)
            {
                var remainingUs = Math.Max(0, d - p);
                eta = TimeSpan.FromSeconds(remainingUs / 1_000_000d / sp);
            }
        }

        if (processedUs is null && fraction is null && totalSize is null) return null;

        return new EngineProgress
        {
            Stage = JobStage.Encoding,
            ProcessedUs = processedUs,
            Fraction = fraction,
            Speed = speed,
            Eta = eta,
            BytesWritten = totalSize,
            SampledUtc = DateTimeOffset.UtcNow,
        };
    }

    private static async Task PumpStderrAsync(StreamReader reader, BoundedTail tail, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                tail.AppendLine(line);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private static FailureInfo ClassifyFailure(int exitCode, string stderr, long? bytes, bool hardware)
    {
        var lower = stderr.ToLowerInvariant();
        if (lower.Contains("no space left") || lower.Contains("disk full") || lower.Contains("not enough space"))
            return FailureInfo.Of(FailureCode.DiskFull, "run.disk_full", TailText(stderr), retryable: false);
        if (lower.Contains("permission denied") || lower.Contains("access is denied") || lower.Contains("access denied"))
            return FailureInfo.Of(FailureCode.AccessDenied, "run.access_denied", TailText(stderr));
        if (lower.Contains("unknown encoder") || lower.Contains("encoder") && lower.Contains("not found")
            || lower.Contains("cannot load") || lower.Contains("failed to initialize")
            || hardware && (lower.Contains("nvenc api version") || lower.Contains("open encoder")
                || lower.Contains("no capable devices") || lower.Contains("no nvenc capable") || lower.Contains("no device")
                || lower.Contains("unsupported device") || lower.Contains("error initializing") || lower.Contains("device failed")
                || lower.Contains("device creation failed") || lower.Contains("failed to get adapter")))
            return FailureInfo.Of(FailureCode.EncoderInitFailed, "run.encoder_init_failed", TailText(stderr), retryable: true);
        if (bytes is null or 0)
            return FailureInfo.Of(FailureCode.ProcessFailed, "run.no_output",
                $"ffmpeg exited with {exitCode} and produced no non-empty output. {TailText(stderr)}", retryable: true);
        return FailureInfo.Of(FailureCode.ProcessFailed, "run.failed",
            $"ffmpeg exited with {exitCode}. {TailText(stderr)}", retryable: true);
    }

    private static string Tail(BoundedTail tail) => tail.ToString();

    private static string TailText(string value) => value.Length <= 2000 ? value : value[^2000..];

    private static void TryDelete(string path)
    {
        // A just-terminated process (or a scanner) can briefly hold the file; retry a few times.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
            catch
            {
                return;
            }
        }
    }

    private static async Task SafeAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { }
    }

    private sealed class BoundedTail
    {
        private readonly int _capacity;
        private readonly StringBuilder _sb = new();

        public BoundedTail(int capacity) => _capacity = capacity;

        public void AppendLine(string line)
        {
            _sb.AppendLine(line);
            if (_sb.Length > _capacity * 2)
            {
                var keep = _sb.Length - _capacity;
                _sb.Remove(0, keep);
            }
        }

        public override string ToString()
        {
            var text = _sb.ToString();
            return text.Length <= _capacity ? text : text[^_capacity..];
        }
    }
}
