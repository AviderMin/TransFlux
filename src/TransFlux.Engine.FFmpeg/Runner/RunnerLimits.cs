namespace TransFlux.Engine.FFmpeg.Runner;

/// <summary>
/// Safety limits for a single ffmpeg attempt. The defaults are deliberately generous for
/// feature-length encodes while still guaranteeing that a wedged process (network share
/// drop, driver deadlock, unkillable child) cannot hold a scheduler slot forever.
/// </summary>
public sealed record RunnerLimits
{
    /// <summary>Default limits: 24h hard cap, 10min without a <c>-progress</c> heartbeat.</summary>
    public static RunnerLimits Default { get; } = new();

    /// <summary>Hard cap on total wall-clock time for one attempt.</summary>
    public TimeSpan MaxRuntime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Maximum time without a <c>-progress</c> heartbeat before the attempt is treated as stalled.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the watchdog samples the runtime/heartbeat clocks.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}
