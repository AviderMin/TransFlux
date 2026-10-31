using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.App.Hosting;

// Degraded implementations used only when the pinned media core cannot be located/verified.
// They keep the workbench usable and surface a clear CoreMissing failure instead of crashing.

public sealed class MissingCoreCapabilities : IEngineCapabilities
{
    public CoreIdentity GetIdentity() => new() { CoreHash = string.Empty, FfmpegVersion = "(missing)", FfmpegPath = string.Empty, FfprobePath = string.Empty };
    public Task<FfmpegCapabilityInfo> QueryAsync(CancellationToken ct) => Task.FromResult(new FfmpegCapabilityInfo());
    public Task<HardwareValidationResult> ValidateEncoderAsync(HardwareValidationRequest request, CancellationToken ct) => Task.FromResult(new HardwareValidationResult { Success = false, FailureReason = "Media core missing" });
}

public sealed class MissingProbe : IMediaProbe
{
    public Task<Result<SourceSnapshot>> ProbeAsync(SourceReference source, CancellationToken ct) =>
        Task.FromResult(Result<SourceSnapshot>.Operational(FailureInfo.Of(FailureCode.CoreMissing, "core.missing", "The media core (ffmpeg/ffprobe) is missing or failed integrity verification.", false, "Reinstall or restore the media core.")));
}

public sealed class MissingPlanner : IExecutionPlanner
{
    public Task<Result<ValidatedPlan>> BuildAsync(ConversionRequest request, SourceSnapshot source, CapabilitySnapshot capabilities, CancellationToken ct) =>
        Task.FromResult(Result<ValidatedPlan>.Validation(FailureInfo.Of(FailureCode.CoreMissing, "core.missing", "The media core is missing.", false, "Reinstall or restore the media core.")));
}

public sealed class MissingRunner : IMediaRunner
{
    public Task<RunResult> RunAsync(AttemptContext attempt, IProgress<EngineProgress> progress, CancellationToken ct) =>
        Task.FromResult(RunResult.Fail(null, TimeSpan.Zero, FailureInfo.Of(FailureCode.CoreMissing, "core.missing", "The media core is missing."), null));
}

public sealed class MissingVerifier : IOutputVerifier
{
    public Task<VerificationResult> VerifyAsync(VerificationRequest request, CancellationToken ct) =>
        Task.FromResult(VerificationResult.Fail(FailureInfo.Of(FailureCode.CoreMissing, "core.missing", "The media core is missing."), "not verified"));
}

public sealed class MissingCapabilityService : ICapabilityService
{
    private readonly CapabilitySnapshot _snapshot = new() { CapabilityHash = "core-missing", CoreHash = string.Empty };
    public Task<CapabilitySnapshot> GetAsync(CapabilityQuery query, CancellationToken ct) => Task.FromResult(_snapshot);
    public Task<CapabilitySnapshot> RefreshAsync(CancellationToken ct) => Task.FromResult(_snapshot);
}
