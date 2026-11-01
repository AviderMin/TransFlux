using System.Collections.Concurrent;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;

namespace TransFlux.Application.Tests;

public sealed class RuntimeQueueTests
{
    [Fact]
    public async Task PauseWhileCapacityIsFullDoesNotStartTheNextJob()
    {
        await using var h = new SchedulerHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Runner.Handler = async (_, _, ct) => { await release.Task.WaitAsync(ct); return RunResult.Ok(0, TimeSpan.FromSeconds(1), 1000, null); };
        var first = await h.EnqueueAsync(); var next = await h.EnqueueAsync();
        h.Scheduler.Start(); await h.WaitForStateAsync(first, JobState.Running);
        await h.Scheduler.PauseQueueAsync(default); release.SetResult();
        await h.WaitForStateAsync(first, JobState.Succeeded);
        // Wake the dispatcher deterministically while paused, rather than only checking an immediate state.
        var extra = await h.EnqueueAsync();
        await Task.Delay(100);
        Assert.Equal(JobState.Queued, (await h.Store.GetJobAsync(next, default))!.State);
        Assert.Single(h.Runner.Attempts);
        await h.Scheduler.ResumeQueueAsync(default);
        await h.WaitForStateAsync(next, JobState.Succeeded); await h.WaitForStateAsync(extra, JobState.Succeeded);
    }

    [Fact]
    public async Task CancelDuringRecoveringFallbackReachesCanceledTerminalState()
    {
        await using var h = new SchedulerHarness();
        h.Planner.Handler = (_, _, _, _) => Task.FromResult(Result<ValidatedPlan>.Ok(TestData.MakePlan(2)));
        h.Runner.Handler = (_, _, _) => Task.FromResult(RunResult.Fail(1, TimeSpan.Zero,
            FailureInfo.Of(FailureCode.DeviceUnavailable, "test.deviceUnavailable"), "encode failed"));
        var id = await h.EnqueueAsync();
        var canceledDuringRecovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Store.AfterUpdateJob = async job =>
        {
            if (job.State == JobState.Recovering)
            {
                canceledDuringRecovering.TrySetResult();
                await h.Scheduler.CancelAsync(job.JobId, CancellationToken.None);
            }
        };
        h.Scheduler.Start();
        await h.WaitForStateAsync(id, JobState.Canceled, TimeSpan.FromSeconds(5));
        Assert.True(canceledDuringRecovering.Task.IsCompleted);
        Assert.Equal(JobState.Canceled, (await h.Store.GetJobAsync(id, default))!.State);
    }

    [Fact]
    public async Task RuntimeConcurrencyCanIncreaseAndDecrease_WithoutCancelingActiveTasks()
    {
        await using var h = new SchedulerHarness();
        var releases = new ConcurrentDictionary<Guid, TaskCompletionSource>();
        var verificationPolicies = new ConcurrentQueue<bool>();
        h.Verifier.Handler = (request, _) =>
        { verificationPolicies.Enqueue(request.FullDecode); return Task.FromResult(VerificationResult.Ok("ok")); };
        h.Runner.Handler = async (ctx, _, ct) =>
        {
            var done = releases.GetOrAdd(ctx.JobId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            await done.Task.WaitAsync(ct); return RunResult.Ok(0, TimeSpan.FromSeconds(1), 1000, null);
        };
        var first = await h.EnqueueAsync(); var second = await h.EnqueueAsync(); var third = await h.EnqueueAsync();
        h.Scheduler.Start(); await h.WaitForStateAsync(first, JobState.Running);
        h.Scheduler.ConfigureExecution(2, false, true);
        await h.WaitForStateAsync(second, JobState.Running);
        h.Scheduler.ConfigureExecution(1, true, false);
        releases[first].SetResult(); await h.WaitForStateAsync(first, JobState.Succeeded);
        Assert.Equal(JobState.Running, (await h.Store.GetJobAsync(second, default))!.State);
        Assert.Equal(JobState.Queued, (await h.Store.GetJobAsync(third, default))!.State);
        releases[second].SetResult(); await h.WaitForStateAsync(second, JobState.Succeeded);
        await h.WaitForStateAsync(third, JobState.Running); releases[third].SetResult();
        await h.WaitForStateAsync(third, JobState.Succeeded);
        Assert.Equal(new[] { false, true, false }, verificationPolicies.ToArray());
    }

    [Fact]
    public async Task WatchSnapshotIncludesCommittedOutput_AndSignalsEmptySnapshotCompletion()
    {
        await using var h = new SchedulerHarness();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var empty = h.Scheduler.WatchAsync(new(), cts.Token).GetAsyncEnumerator())
        { Assert.True(await empty.MoveNextAsync()); Assert.Equal(JobEventKind.SnapshotCompleted, empty.Current.Kind); }
        h.Scheduler.Start(); var id = await h.EnqueueAsync(); await h.WaitForStateAsync(id, JobState.Succeeded);
        await using var watch = h.Scheduler.WatchAsync(new(), cts.Token).GetAsyncEnumerator();
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal(id, watch.Current.JobId); Assert.NotNull(watch.Current.Output);
        Assert.Equal(AttemptState.Succeeded, watch.Current.Attempt!.State);
    }
}
