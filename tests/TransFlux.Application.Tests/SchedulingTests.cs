using TransFlux.Application.Contracts;
using TransFlux.Application.Scheduling;
using TransFlux.Domain.Jobs;
using Xunit;

namespace TransFlux.Application.Tests;

public class QueueOrderingTests
{
    private static Job Job(long order, DateTimeOffset created) => new()
    {
        JobId = Guid.NewGuid(),
        RequestSnapshot = TestData.MakeRequest(Guid.NewGuid()),
        SourceFingerprint = TestData.MakeSource().Fingerprint,
        EnqueueOrder = order,
        CreatedUtc = created,
        UpdatedUtc = created,
        State = JobState.Queued,
    };

    [Fact]
    public void Order_PreservesFifo_ForEqualWaits()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var jobs = new[] { Job(3, now), Job(1, now), Job(2, now) };

        var ordered = QueueOrdering.Order(jobs, now, TimeSpan.FromSeconds(30));

        Assert.Equal(new long[] { 1, 2, 3 }, ordered.Select(j => j.EnqueueOrder).ToArray());
    }

    [Fact]
    public void AgingKey_Decreases_AsJobWaitsLonger()
    {
        var created = DateTimeOffset.UnixEpoch;
        var job = Job(5, created);

        var freshKey = QueueOrdering.AgingKey(job, created, TimeSpan.FromSeconds(30));
        var agedKey = QueueOrdering.AgingKey(job, created.AddSeconds(90), TimeSpan.FromSeconds(30));

        Assert.Equal(5, freshKey);
        Assert.Equal(2, agedKey); // 90s / 30s = 3 quanta of boost
    }
}

public class ProgressCoalescerTests
{
    private static EngineProgress P(long processed, DateTimeOffset sampled, JobStage stage = JobStage.Encoding) => new()
    {
        Stage = stage,
        ProcessedUs = processed,
        Fraction = 0.1,
        SampledUtc = sampled,
    };

    [Fact]
    public void Report_ThrottlesToConfiguredInterval()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var emitted = new List<ProgressSnapshot>();
        var coalescer = new ProgressCoalescer(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMilliseconds(500), clock, emitted.Add);

        // Ten samples at the same fake instant with increasing processed time: only the first survives.
        for (var i = 1; i <= 10; i++)
        {
            coalescer.Report(P(i * 1000, clock.UtcNow.AddMilliseconds(i)));
        }
        Assert.Single(emitted);

        // Advance the clock past the throttle window: next sample is emitted.
        clock.Advance(TimeSpan.FromMilliseconds(600));
        coalescer.Report(P(20_000, clock.UtcNow));
        Assert.Equal(2, emitted.Count);
        Assert.Equal(new long[] { 1, 2 }, emitted.Select(e => e.Sequence).ToArray());
    }

    [Fact]
    public void Report_DiscardsOutOfOrderAndExpiredSamples()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var emitted = new List<ProgressSnapshot>();
        var coalescer = new ProgressCoalescer(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMilliseconds(250), clock, emitted.Add);

        coalescer.Report(P(5_000, clock.UtcNow));
        Assert.Single(emitted);

        clock.Advance(TimeSpan.FromMilliseconds(300));
        // Lower processed time than last -> out of order, discarded.
        coalescer.Report(P(4_000, clock.UtcNow));
        Assert.Single(emitted);

        clock.Advance(TimeSpan.FromMilliseconds(300));
        // Sampled timestamp older than the last accepted -> expired, discarded.
        coalescer.Report(P(6_000, DateTimeOffset.UnixEpoch.AddSeconds(-5)));
        Assert.Single(emitted);

        clock.Advance(TimeSpan.FromMilliseconds(300));
        coalescer.Report(P(7_000, clock.UtcNow));
        Assert.Equal(2, emitted.Count);
    }

    [Fact]
    public void Report_AlwaysEmitsTerminalStage()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var emitted = new List<ProgressSnapshot>();
        var coalescer = new ProgressCoalescer(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromSeconds(10), clock, emitted.Add);

        coalescer.Report(P(1_000, clock.UtcNow));
        coalescer.Report(P(2_000, clock.UtcNow, JobStage.Finalizing));

        Assert.Equal(2, emitted.Count);
        Assert.Equal(JobStage.Finalizing, emitted[1].Stage);
    }
}
