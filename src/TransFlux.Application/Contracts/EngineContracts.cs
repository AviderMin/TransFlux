using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Application.Contracts;

/// <summary>Placeholders substituted by the runner at execution time. Never user-authored.</summary>
public static class PlanPlaceholders
{
    public const string Input = "\u0001TF_INPUT\u0001";
    public const string Output = "\u0001TF_OUTPUT\u0001";
}

/// <summary>Everything the runner needs for one attempt.</summary>
public sealed record AttemptContext
{
    public required Guid JobId { get; init; }
    public required Guid AttemptId { get; init; }
    public required ExecutionCandidate Candidate { get; init; }
    public required SourceSnapshot Source { get; init; }
    public required ConversionRequest Request { get; init; }
    public required string TempPath { get; init; }
    public required string FfmpegPath { get; init; }
    public required string FfprobePath { get; init; }
    public long? EffectiveDurationUs { get; init; }
}

public sealed record EngineProgress
{
    public JobStage Stage { get; init; } = JobStage.Encoding;
    public long? ProcessedUs { get; init; }
    public double? Fraction { get; init; }
    public double? Speed { get; init; }
    public TimeSpan? Eta { get; init; }
    public long? BytesWritten { get; init; }
    public DateTimeOffset SampledUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RunResult
{
    public bool Succeeded { get; init; }
    public bool Canceled { get; init; }
    public int? ExitCode { get; init; }
    public string? StderrTail { get; init; }
    public TimeSpan Duration { get; init; }
    public long? OutputBytes { get; init; }
    public FailureInfo? Failure { get; init; }

    public static RunResult Ok(int exitCode, TimeSpan duration, long? bytes, string? stderr) =>
        new() { Succeeded = true, ExitCode = exitCode, Duration = duration, OutputBytes = bytes, StderrTail = stderr };
    public static RunResult Fail(int? exitCode, TimeSpan duration, FailureInfo failure, string? stderr) =>
        new() { Succeeded = false, ExitCode = exitCode, Duration = duration, Failure = failure, StderrTail = stderr };
    public static RunResult CanceledResult(TimeSpan duration) =>
        new() { Succeeded = false, Canceled = true, Duration = duration };
}

public interface IMediaRunner
{
    Task<RunResult> RunAsync(AttemptContext attempt, IProgress<EngineProgress> progress, CancellationToken ct);
}

public sealed record VerificationRequest
{
    public required ValidatedPlan Plan { get; init; }
    public required string OutputPath { get; init; }
    public required SourceSnapshot Source { get; init; }
    public bool FullDecode { get; init; }
}

public sealed record VerificationResult
{
    public bool Passed { get; init; }
    public string Summary { get; init; } = string.Empty;
    public FailureInfo? Failure { get; init; }
    public SourceSnapshot? ActualSnapshot { get; init; }

    public static VerificationResult Ok(string summary, SourceSnapshot? actual = null) => new() { Passed = true, Summary = summary, ActualSnapshot = actual };
    public static VerificationResult Fail(FailureInfo failure, string summary) => new() { Passed = false, Failure = failure, Summary = summary };
}

public interface IOutputVerifier
{
    Task<VerificationResult> VerifyAsync(VerificationRequest request, CancellationToken ct);
}
