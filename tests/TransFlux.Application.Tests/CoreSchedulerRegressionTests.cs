using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;

namespace TransFlux.Application.Tests;

public sealed class CoreSchedulerRegressionTests
{
    [Fact]
    public async Task Retry_NeedsAttention_IsAlwaysRejectedForManualReview()
    {
        await using var h = new SchedulerHarness();
        var id = await h.EnqueueAsync();
        var job = await h.Store.GetJobAsync(id, default);
        await h.Store.UpdateJobAsync(job! with
        {
            State = JobState.NeedsAttention,
            Revision = job.Revision + 1,
        }, job.Revision, default);

        var journal = new CommitJournalEntry
        {
            CommitToken = "unresolved",
            AttemptId = Guid.NewGuid(),
            JobId = id,
            Phase = CommitPhase.Prepared,
            TempPath = @"C:\work\attempt.mp4",
            FinalPath = @"C:\out\result.mp4",
            SizeBytes = 100,
            UpdatedUtc = h.Clock.UtcNow,
        };
        await h.Store.UpsertJournalAsync(journal, default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Scheduler.RetryAsync(id, default));
        Assert.Contains("manual output verification", ex.Message, StringComparison.OrdinalIgnoreCase);

        await h.Store.DeleteJournalAsync(journal.CommitToken, default);
        var stillRejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Scheduler.RetryAsync(id, default));
        Assert.Contains("manual output verification", stillRejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DispatchLoop_TransientStoreFailure_RecoversWithoutRestart()
    {
        await using var h = new SchedulerHarness();
        var id = await h.EnqueueAsync();

        // Recount is best-effort and consumes the first failure; the actual dispatch read
        // consumes the second and exercises the bounded-backoff recovery path.
        h.Store.QueuedReadFailuresToInject = 2;
        h.Scheduler.Start();

        var completed = await h.WaitForStateAsync(id, JobState.Succeeded);
        Assert.Equal(JobState.Succeeded, completed.State);
        Assert.Equal(0, h.Store.QueuedReadFailuresToInject);
        Assert.Single(h.Runner.Attempts);
    }

    [Fact]
    public async Task ManualGpu_CapabilityFailure_DoesNotAutomaticallyUseCpu()
    {
        await using var h = new SchedulerHarness();
        h.Planner.Handler = (_, _, _, _) =>
            Task.FromResult(Result<ValidatedPlan>.Ok(TestData.MakePlan(2)));
        h.Runner.Handler = (_, _, _) => Task.FromResult(RunResult.Fail(
            1,
            TimeSpan.Zero,
            FailureInfo.Of(FailureCode.EncoderInitFailed, "error.encoderInit"),
            "GPU initialization failed"));
        var request = TestData.MakeRequest(h.Source.MediaId) with
        {
            Hardware = new HardwarePreference { Backend = HardwareBackend.NvidiaNvenc },
        };
        var id = await h.Scheduler.EnqueueAsync(new EnqueueRequest
        {
            Request = request,
            Source = h.Source,
        }, default);

        h.Scheduler.Start();
        await h.WaitForStateAsync(id, JobState.Failed);

        Assert.Single(h.Runner.Attempts);
        Assert.Single(h.Store.AllAttempts);
    }

    [Fact]
    public async Task QueueMove_ReordersOnlyQueuedJobsAndRejectsNonQueued()
    {
        await using var h = new SchedulerHarness();
        var first = await h.EnqueueAsync();
        var second = await h.EnqueueAsync();

        Assert.True(await h.Scheduler.MoveQueuedAsync(second, -1, default));
        var queued = await h.Store.GetJobsAsync(new[] { JobState.Queued }, default);
        Assert.Equal(new[] { second, first }, queued.Select(j => j.JobId));

        var running = (await h.Store.GetJobAsync(first, default))! with
        {
            State = JobState.Running,
            Revision = (await h.Store.GetJobAsync(first, default))!.Revision + 1,
        };
        await h.Store.UpdateJobAsync(running, running.Revision - 1, default);
        Assert.False(await h.Scheduler.MoveQueuedAsync(first, 1, default));
    }

    [Fact]
    public async Task SourceChangedAfterVerification_FailsBeforeCommit()
    {
        await using var h = new SchedulerHarness();
        var probes = 0;
        h.Probe.Handler = (_, _) =>
        {
            var current = ++probes;
            var snapshot = current == 1 ? h.Source : h.Source with { SizeBytes = h.Source.SizeBytes + 1 };
            return Task.FromResult(Result<Domain.Media.SourceSnapshot>.Ok(snapshot));
        };

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        await h.WaitForStateAsync(id, JobState.Failed);

        Assert.Equal(2, probes);
        Assert.Equal(0, h.Committer.CommitCalls);
        var attempt = Assert.Single(h.Store.AllAttempts);
        Assert.Equal(AttemptState.Failed, attempt.State);
        Assert.Equal(FailureCode.InputChanged, attempt.Failure?.Code);
    }
}
