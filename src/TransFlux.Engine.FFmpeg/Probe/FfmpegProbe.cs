using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Engine.FFmpeg.Core;
using TransFlux.Engine.FFmpeg.Processes;

namespace TransFlux.Engine.FFmpeg.Probe;

/// <summary>ffprobe-based media probe. Uses structured arguments, bounded output and timeouts.</summary>
public sealed class FfmpegProbe : IMediaProbe
{
    public const int MaxJsonChars = 8 * 1024 * 1024;
    public const int MaxStderrChars = 64 * 1024;

    private readonly FfmpegCorePaths _core;
    private readonly TimeSpan _timeout;

    public FfmpegProbe(FfmpegCorePaths core) : this(core, TimeSpan.FromSeconds(15)) { }

    public FfmpegProbe(FfmpegCorePaths core, TimeSpan timeout)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _timeout = timeout;
    }

    public async Task<Result<SourceSnapshot>> ProbeAsync(SourceReference source, CancellationToken ct)
    {
        if (source is null)
            return Result<SourceSnapshot>.Operational(FailureInfo.Of(FailureCode.ProbeFailed, "probe.invalid_source", "Source reference was null."));

        var path = source.AbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Result<SourceSnapshot>.Operational(FailureInfo.Of(
                FailureCode.ProbeFailed, "probe.file_missing", $"Input file not found: '{path}'."));

        var arguments = new List<string>
        {
            "-hide_banner", "-v", "error",
            "-print_format", "json",
            "-show_format", "-show_streams", "-show_chapters",
            "-protocol_whitelist", "file",
            path,
        };

        CaptureResult capture;
        try
        {
            capture = await ProcessCapture.RunAsync(_core.FfprobePath, arguments, _timeout, MaxJsonChars, MaxStderrChars, ct);
        }
        catch (OperationCanceledException)
        {
            return Result<SourceSnapshot>.Canceled();
        }
        catch (Exception ex)
        {
            return Result<SourceSnapshot>.Operational(FailureInfo.Of(FailureCode.ProbeFailed, "probe.start_failed", ex.Message));
        }

        if (capture.TimedOut)
            return Result<SourceSnapshot>.Operational(FailureInfo.Of(
                FailureCode.ProbeFailed, "probe.timeout", $"ffprobe timed out after {_timeout.TotalSeconds:0}s."));
        if (capture.ExitCode != 0)
            return Result<SourceSnapshot>.Operational(FailureInfo.Of(
                FailureCode.ProbeFailed, "probe.exit_nonzero", Trim(capture.Stderr)));
        if (capture.StdoutTruncated)
            return Result<SourceSnapshot>.Operational(FailureInfo.Of(
                FailureCode.ProbeFailed, "probe.too_large", $"ffprobe output exceeded {MaxJsonChars} characters."));

        try
        {
            var info = new FileInfo(path);
            var snapshot = FfprobeJsonParser.Parse(
                capture.Stdout, Path.GetFullPath(path), info.Length, info.LastWriteTimeUtc, source.MediaId);
            return Result<SourceSnapshot>.Ok(snapshot);
        }
        catch (Exception ex)
        {
            return Result<SourceSnapshot>.Operational(FailureInfo.Of(
                FailureCode.ProbeFailed, "probe.parse_failed", ex.Message));
        }
    }

    private static string Trim(string value) =>
        value.Length <= 4000 ? value : value[^4000..];
}
