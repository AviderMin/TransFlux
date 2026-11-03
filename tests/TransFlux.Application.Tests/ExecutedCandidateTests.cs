using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;

namespace TransFlux.Application.Tests;

/// <summary>R10: an attempt must persist the candidate it actually executed, so a GPU failure
/// followed by a CPU success is never reported as the GPU candidate.</summary>
public sealed class ExecutedCandidateTests
{
    [Fact]
    public async Task AutomaticFallback_RecordsExecutedCandidateForEachAttempt()
    {
        await using var h = new SchedulerHarness();
        var plan = TestData.MakePlan(2);
        // Put the hardware candidate first so the CPU path is reached only through fallback.
        var gpuFirst = plan with { CandidatePaths = [plan.CandidatePaths[1], plan.CandidatePaths[0]] };
        h.Planner.Handler = (_, _, _, _) => Task.FromResult(Result<ValidatedPlan>.Ok(gpuFirst));
        var runs = 0;
        h.Runner.Handler = (_, _, _) =>
        {
            var n = Interlocked.Increment(ref runs);
            return Task.FromResult(n == 1
                ? RunResult.Fail(1, TimeSpan.Zero, FailureInfo.Of(FailureCode.EncoderInitFailed, "error.encoderInit"), "boom")
                : RunResult.Ok(0, TimeSpan.FromSeconds(1), 1000, null));
        };

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        await h.WaitForStateAsync(id, JobState.Succeeded);

        var attempts = h.Store.AllAttempts.OrderBy(a => a.Ordinal).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal("hw-1", attempts[0].ExecutedCandidateId);
        Assert.Equal("cpu-x264", attempts[1].ExecutedCandidateId);
    }

    [Fact]
    public async Task SingleCandidateAttempt_RecordsExecutedCandidate()
    {
        await using var h = new SchedulerHarness();
        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        await h.WaitForStateAsync(id, JobState.Succeeded);

        var attempt = Assert.Single(h.Store.AllAttempts);
        Assert.Equal("cpu-x264", attempt.ExecutedCandidateId);
    }
}
