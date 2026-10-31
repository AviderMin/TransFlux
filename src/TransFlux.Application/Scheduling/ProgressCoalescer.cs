using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;

namespace TransFlux.Application.Scheduling;

/// <summary>
/// Coalesces engine progress for a single attempt into at most 4 Hz UI events.
/// Out-of-order or stale samples (lower processed time / older sample timestamp) are dropped,
/// and a terminal stage is always emitted. The minimum interval is clamped to 250 ms so the
/// 4 Hz budget can never be exceeded even if callers pass a smaller throttle.
/// </summary>
internal sealed class ProgressCoalescer
{
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly Guid _jobId;
    private readonly Guid _attemptId;
    private readonly TimeSpan _minInterval;
    private readonly IClock _clock;
    private readonly Action<ProgressSnapshot> _emit;

    private DateTimeOffset _lastEmit = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSampled = DateTimeOffset.MinValue;
    private long _lastProcessedUs = -1;
    private long _sequence;

    public ProgressCoalescer(Guid jobId, Guid attemptId, TimeSpan throttle, IClock clock, Action<ProgressSnapshot> emit)
    {
        _jobId = jobId;
        _attemptId = attemptId;
        _minInterval = throttle < MaxInterval ? MaxInterval : throttle;
        _clock = clock;
        _emit = emit;
    }

    public void Report(EngineProgress progress)
    {
        lock (_gate)
        {
            if (progress.ProcessedUs is long processed)
            {
                // Ignore out-of-order progress for the same attempt.
                if (_lastProcessedUs >= 0 && processed < _lastProcessedUs)
                {
                    return;
                }
            }

            if (progress.SampledUtc < _lastSampled)
            {
                // Ignore an event sampled earlier than the last accepted one (expired/out of order).
                return;
            }

            var now = _clock.UtcNow;
            var isTerminalStage = progress.Stage is JobStage.Finalizing;
            if (!isTerminalStage && now - _lastEmit < _minInterval)
            {
                return;
            }

            _lastEmit = now;
            _lastSampled = progress.SampledUtc;
            if (progress.ProcessedUs is long p)
            {
                _lastProcessedUs = p;
            }

            _sequence++;
            _emit(new ProgressSnapshot
            {
                JobId = _jobId,
                AttemptId = _attemptId,
                Sequence = _sequence,
                Stage = progress.Stage,
                ProcessedUs = progress.ProcessedUs,
                Fraction = progress.Fraction,
                Speed = progress.Speed,
                Eta = progress.Eta,
                BytesWritten = progress.BytesWritten,
                SampledUtc = progress.SampledUtc,
            });
        }
    }
}
