using System.Collections.Concurrent;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;
using Xunit;

namespace TransFlux.Application.Tests;

public class JobSchedulerTests
{
    [Fact]
    public async Task RunsQueuedJob_ToSucceeded_WithSingleAttempt()
    {
        await using var h = new SchedulerHarness();
        h.Scheduler.Start();

        var id = await h.EnqueueAsync();
        var job = await h.WaitForStateAsync(id, JobState.Succeeded);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Single(h.Runner.Attempts);
        var attempt = Assert.Single(h.Store.AllAttempts);
        Assert.Equal(AttemptState.Succeeded, attempt.State);
        Assert.Single(await h.Store.GetOutputsForJobAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_QueuedJob_IsCanceledDirectly()
    {
        await using var h = new SchedulerHarness();
        var id = await h.EnqueueAsync();

        var result = await h.Scheduler.CancelAsync(id, CancellationToken.None);

        Assert.True(result.Accepted);
        var job = await h.Store.GetJobAsync(id, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(JobState.Canceled, job!.State);
        Assert.Empty(h.Runner.Attempts);
    }

    [Fact]
    public async Task Cancel_DuringRunning_EndsCanceled_AndDoesNotSucceed()
    {
        await using var h = new SchedulerHarness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Runner.Handler = async (_, _, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
                return RunResult.Ok(0, TimeSpan.Zero, 100, null);
            }
            catch (OperationCanceledException)
            {
                return RunResult.CanceledResult(TimeSpan.Zero);
            }
        };

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        await started.Task;
        await h.WaitForStateAsync(id, JobState.Running);

        var result = await h.Scheduler.CancelAsync(id, CancellationToken.None);
        Assert.True(result.Accepted);

        var job = await h.WaitForStateAsync(id, JobState.Canceled);
        Assert.Equal(JobState.Canceled, job.State);
        Assert.Empty(await h.Store.GetOutputsForJobAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_AtCommitBoundary_IsRefused_AndJobSucceeds()
    {
        await using var h = new SchedulerHarness();
        var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Committer.OnCommit = async (req, _) =>
        {
            commitEntered.TrySetResult();
            await release.Task;
            return new CommitResult { Success = true, CommitToken = "tok", FinalPath = req.FinalPath };
        };

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        await commitEntered.Task;
        await h.WaitForStateAsync(id, JobState.Committing);

        var result = await h.Scheduler.CancelAsync(id, CancellationToken.None);
        Assert.False(result.Accepted);
        Assert.Contains("saving", result.Message, StringComparison.OrdinalIgnoreCase);

        release.TrySetResult();
        var job = await h.WaitForStateAsync(id, JobState.Succeeded);
        Assert.Equal(JobState.Succeeded, job.State);
    }

    [Fact]
    public async Task CapabilityFailure_TriggersExactlyOneAutomaticFallback()
    {
        await using var h = new SchedulerHarness();
        h.Planner.Handler = (_, _, _, _) => Task.FromResult(Result<ValidatedPlan>.Ok(TestData.MakePlan(2)));
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

        Assert.Equal(2, runs);
        var attempts = h.Store.AllAttempts.OrderBy(a => a.Ordinal).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(AttemptState.Failed, attempts[0].State);
        Assert.NotNull(attempts[0].FallbackReason);
        Assert.Equal(AttemptState.Succeeded, attempts[1].State);
    }

    [Fact]
    public async Task CapabilityFailure_WithFallbackDisabled_FailsImmediately()
    {
        await using var h = new SchedulerHarness(o => o.AllowAutomaticFallback = false);
        h.Planner.Handler = (_, _, _, _) => Task.FromResult(Result<ValidatedPlan>.Ok(TestData.MakePlan(2)));
        h.Runner.Handler = (_, _, _) => Task.FromResult(
            RunResult.Fail(1, TimeSpan.Zero, FailureInfo.Of(FailureCode.EncoderInitFailed, "error.encoderInit"), "boom"));

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        var job = await h.WaitForStateAsync(id, JobState.Failed);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Single(h.Store.AllAttempts);
    }

    [Fact]
    public async Task NonCapabilityFailure_DoesNotFallback()
    {
        await using var h = new SchedulerHarness();
        h.Planner.Handler = (_, _, _, _) => Task.FromResult(Result<ValidatedPlan>.Ok(TestData.MakePlan(2)));
        h.Runner.Handler = (_, _, _) => Task.FromResult(
            RunResult.Fail(1, TimeSpan.Zero, FailureInfo.Of(FailureCode.ProcessFailed, "error.processFailed"), "boom"));

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        var job = await h.WaitForStateAsync(id, JobState.Failed);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Single(h.Store.AllAttempts);
    }

    [Fact]
    public async Task PauseQueue_StopsNewStarts_ResumeRunsThem()
    {
        await using var h = new SchedulerHarness();
        await h.Scheduler.PauseQueueAsync(CancellationToken.None);
        Assert.True(h.Scheduler.GetControl().Paused);

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        await Task.Delay(200);

        Assert.Empty(h.Runner.Attempts);
        var paused = await h.Store.GetJobAsync(id, CancellationToken.None);
        Assert.Equal(JobState.Queued, paused!.State);

        await h.Scheduler.ResumeQueueAsync(CancellationToken.None);
        var job = await h.WaitForStateAsync(id, JobState.Succeeded);
        Assert.Equal(JobState.Succeeded, job.State);
    }

    [Fact]
    public async Task RemoveQueued_ReturnsTrue_AndCancelsQueuedJob()
    {
        await using var h = new SchedulerHarness();
        var id = await h.EnqueueAsync();

        var removed = await h.Scheduler.RemoveQueuedAsync(id, CancellationToken.None);

        Assert.True(removed);
        var job = await h.Store.GetJobAsync(id, CancellationToken.None);
        Assert.Equal(JobState.Canceled, job!.State);
        Assert.False(await h.Scheduler.RemoveQueuedAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Retry_CreatesNewJobWithRetryOfLink()
    {
        await using var h = new SchedulerHarness();
        h.Runner.Handler = (_, _, _) => Task.FromResult(
            RunResult.Fail(1, TimeSpan.Zero, FailureInfo.Of(FailureCode.ProcessFailed, "error.processFailed"), "boom"));
        h.Scheduler.Start();

        var originalId = await h.EnqueueAsync();
        await h.WaitForStateAsync(originalId, JobState.Failed);

        var retryId = await h.Scheduler.RetryAsync(originalId, CancellationToken.None);
        Assert.NotEqual(originalId, retryId);

        var retry = await h.Store.GetJobAsync(retryId, CancellationToken.None);
        Assert.NotNull(retry);
        Assert.Equal(originalId, retry!.RetryOfJobId);
    }

    [Fact]
    public async Task WatchAsync_DeliversSnapshot_ThenLiveEvents()
    {
        await using var h = new SchedulerHarness();
        var existing = await h.EnqueueAsync();

        var collected = new ConcurrentQueue<JobEvent>();
        using var cts = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in h.Scheduler.WatchAsync(new JobWatchQuery { IncludeCompleted = true }, cts.Token))
                {
                    collected.Enqueue(e);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await SchedulerHarness.WaitUntilAsync(() => collected.Any(e => e.Kind == JobEventKind.Snapshot && e.JobId == existing));

        var live = await h.EnqueueAsync();
        await SchedulerHarness.WaitUntilAsync(() => collected.Any(e => e.Kind == JobEventKind.Enqueued && e.JobId == live));

        cts.Cancel();
        await pump;
    }

    [Fact]
    public async Task ProgressFromRunner_IsPublished_AsProgressEvents()
    {
        await using var h = new SchedulerHarness();
        h.Runner.Handler = async (_, progress, _) =>
        {
            progress.Report(new EngineProgress { Stage = JobStage.Encoding, ProcessedUs = 1_000_000, Fraction = 0.2, SampledUtc = h.Clock.UtcNow });
            h.Clock.Advance(TimeSpan.FromMilliseconds(600));
            progress.Report(new EngineProgress { Stage = JobStage.Encoding, ProcessedUs = 2_000_000, Fraction = 0.4, SampledUtc = h.Clock.UtcNow });
            await Task.Yield();
            return RunResult.Ok(0, TimeSpan.FromSeconds(1), 1000, null);
        };

        var collected = new ConcurrentQueue<JobEvent>();
        using var cts = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in h.Scheduler.WatchAsync(new JobWatchQuery { IncludeCompleted = true }, cts.Token))
                {
                    if (e.Kind == JobEventKind.Progress)
                    {
                        collected.Enqueue(e);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        await h.WaitForStateAsync(id, JobState.Succeeded);

        await SchedulerHarness.WaitUntilAsync(() => collected.Count >= 2);
        Assert.All(collected, e => Assert.NotNull(e.Progress));
        var sequences = collected.Select(e => e.Progress!.Sequence).ToList();
        Assert.Equal(sequences.OrderBy(s => s), sequences);

        cts.Cancel();
        await pump;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            Interlocked.CompareExchange(ref target, value, current);
        }
    }

    [Fact]
    public async Task HonorsMaxConcurrentTranscodes()
    {
        await using var h = new SchedulerHarness(o => o.MaxConcurrentTranscodes = 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maxObserved = 0;
        h.Runner.Handler = async (_, _, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            InterlockedMax(ref maxObserved, current);
            try
            {
                await release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Decrement(ref active);
                return RunResult.CanceledResult(TimeSpan.Zero);
            }
            Interlocked.Decrement(ref active);
            return RunResult.Ok(0, TimeSpan.FromSeconds(1), 1000, null);
        };

        h.Scheduler.Start();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await h.EnqueueAsync());
        }

        await SchedulerHarness.WaitUntilAsync(() => Volatile.Read(ref active) >= 2);
        Assert.Equal(2, Volatile.Read(ref active));

        release.TrySetResult();
        foreach (var id in ids)
        {
            await h.WaitForStateAsync(id, JobState.Succeeded);
        }
        Assert.True(maxObserved <= 2, $"observed {maxObserved} concurrent attempts");
    }

    [Fact]
    public async Task Cancel_DuringRunning_ReleasesSlot_SoNextJobRuns()
    {
        await using var h = new SchedulerHarness();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        h.Runner.Handler = async (_, _, ct) =>
        {
            var n = Interlocked.Increment(ref runs);
            if (n == 1)
            {
                firstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    return RunResult.CanceledResult(TimeSpan.Zero);
                }
            }
            return RunResult.Ok(0, TimeSpan.FromSeconds(1), 1000, null);
        };

        h.Scheduler.Start();
        var first = await h.EnqueueAsync();
        await firstStarted.Task;
        await h.WaitForStateAsync(first, JobState.Running);

        var second = await h.EnqueueAsync();
        var cancel = await h.Scheduler.CancelAsync(first, CancellationToken.None);
        Assert.True(cancel.Accepted);

        await h.WaitForStateAsync(first, JobState.Canceled);
        await h.WaitForStateAsync(second, JobState.Succeeded);
    }

    [Fact]
    public async Task CommitFailure_PreservesTempAndWorkDirectory_AndMovesJobToNeedsAttention()
    {
        await using var h = new SchedulerHarness();
        h.Runner.Handler = (attempt, _, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(attempt.TempPath)!);
            File.WriteAllText(attempt.TempPath, "verified-output");
            return Task.FromResult(RunResult.Ok(0, TimeSpan.FromSeconds(1), 15, null));
        };
        h.Committer.OnCommit = (_, _) => Task.FromResult(new CommitResult
        {
            Success = false,
            Failure = FailureInfo.Of(FailureCode.DbFailed, "error.dbFailed", "injected db failure"),
        });

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        var job = await h.WaitForStateAsync(id, JobState.NeedsAttention);

        var attempt = Assert.Single(h.Runner.Attempts);
        Assert.Equal(JobState.NeedsAttention, job.State);
        Assert.True(File.Exists(attempt.TempPath), "the verified temp output must be preserved after a failed commit");
        Assert.Empty(h.OutputPaths.ReservationReleased);
        Assert.Empty(h.OutputPaths.WorkDirectoriesDeleted);
        Assert.Empty(await h.Store.GetOutputsForJobAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task CommitException_PreservesTempAndWorkDirectory_AndMovesJobToNeedsAttention()
    {
        await using var h = new SchedulerHarness();
        h.Runner.Handler = (attempt, _, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(attempt.TempPath)!);
            File.WriteAllText(attempt.TempPath, "verified-output");
            return Task.FromResult(RunResult.Ok(0, TimeSpan.FromSeconds(1), 15, null));
        };
        h.Committer.OnCommit = (_, _) => throw new IOException("injected database write failure");

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        var job = await h.WaitForStateAsync(id, JobState.NeedsAttention);

        var attempt = Assert.Single(h.Runner.Attempts);
        Assert.Equal(JobState.NeedsAttention, job.State);
        Assert.True(File.Exists(attempt.TempPath), "the verified temp output must be preserved after a commit exception");
        Assert.Empty(h.OutputPaths.ReservationReleased);
        Assert.Empty(h.OutputPaths.WorkDirectoriesDeleted);
    }

    [Fact]
    public async Task AbandonNeedsAttention_CleansStagingJournalAndReservation_WithoutDeletingFinalOutput()
    {
        await using var h = new SchedulerHarness();
        h.Runner.Handler = (attempt, _, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(attempt.TempPath)!);
            File.WriteAllText(attempt.TempPath, "verified-output");
            return Task.FromResult(RunResult.Ok(0, TimeSpan.FromSeconds(1), 15, null));
        };
        h.Committer.OnCommit = (_, _) => Task.FromResult(new CommitResult
        {
            Success = false,
            Failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitFailed", "injected commit failure"),
        });

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        await h.WaitForStateAsync(id, JobState.NeedsAttention);
        var attempt = Assert.Single(h.Store.AllAttempts);
        var workDirectory = Path.GetDirectoryName(attempt.TempPath)!;
        var finalPath = attempt.ReservedFinalPath;

        // A user file already at the reserved final name must never be deleted by abandonment.
        File.WriteAllText(finalPath, "user-final-output");
        var journal = new CommitJournalEntry
        {
            CommitToken = Guid.NewGuid().ToString("N"),
            AttemptId = attempt.AttemptId,
            JobId = id,
            Phase = CommitPhase.Prepared,
            TempPath = attempt.TempPath,
            FinalPath = finalPath,
            SizeBytes = 15,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        await h.Store.UpsertJournalAsync(journal, default);

        Assert.True(await h.Scheduler.AbandonAsync(id, CancellationToken.None));

        Assert.Equal(JobState.Canceled, (await h.Store.GetJobAsync(id, default))!.State);
        Assert.Null(await h.Store.GetJournalAsync(journal.CommitToken, default));
        Assert.True(File.Exists(finalPath), "abandon must never delete the user's final output file");
        Assert.False(Directory.Exists(workDirectory));
        Assert.Contains(h.OutputPaths.ReservationReleased, r => r.FinalPath == finalPath);
        Assert.Contains(h.OutputPaths.WorkDirectoriesDeleted, r => r.WorkDirectory == workDirectory);
    }

    [Fact]
    public async Task Abandon_RefusesJobsThatAreNotNeedsAttention()
    {
        await using var h = new SchedulerHarness();
        var id = await h.EnqueueAsync();

        Assert.False(await h.Scheduler.AbandonAsync(id, CancellationToken.None));
        Assert.Equal(JobState.Queued, (await h.Store.GetJobAsync(id, default))!.State);

        h.Scheduler.Start();
        await h.WaitForStateAsync(id, JobState.Succeeded);

        Assert.False(await h.Scheduler.AbandonAsync(id, CancellationToken.None));
        Assert.Equal(JobState.Succeeded, (await h.Store.GetJobAsync(id, default))!.State);
    }

    [Fact]
    public async Task CancellationDuringCommit_DoesNotBecomeNeedsAttention()
    {
        await using var h = new SchedulerHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Committer.OnCommit = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new CommitResult { Success = true };
        };

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        await entered.Task;
        await h.WaitForStateAsync(id, JobState.Committing);

        // Shutdown cancels the job's token while CommitAsync is awaiting it.
        await h.Scheduler.DisposeAsync();

        var job = await h.Store.GetJobAsync(id, default);
        Assert.NotNull(job);
        Assert.NotEqual(JobState.NeedsAttention, job!.State);
        var attempt = Assert.Single(h.Store.AllAttempts);
        Assert.Equal(AttemptState.Canceled, attempt.State);
    }

    [Fact]
    public async Task CommitSuccess_ReleasesReservationAndDeletesWorkDirectory()
    {
        await using var h = new SchedulerHarness();
        h.Runner.Handler = (attempt, _, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(attempt.TempPath)!);
            File.WriteAllText(attempt.TempPath, "verified-output");
            return Task.FromResult(RunResult.Ok(0, TimeSpan.FromSeconds(1), 15, null));
        };

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        await h.WaitForStateAsync(id, JobState.Succeeded);

        Assert.Single(h.OutputPaths.ReservationReleased);
        Assert.Single(h.OutputPaths.WorkDirectoriesDeleted);
        var attempt = Assert.Single(h.Runner.Attempts);
        Assert.False(Directory.Exists(Path.GetDirectoryName(attempt.TempPath)));
    }

    [Fact]
    public async Task VerificationFailure_CleansWorkDirectoryAndReservation()
    {
        await using var h = new SchedulerHarness();
        h.Runner.Handler = (attempt, _, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(attempt.TempPath)!);
            File.WriteAllText(attempt.TempPath, "verified-output");
            return Task.FromResult(RunResult.Ok(0, TimeSpan.FromSeconds(1), 15, null));
        };
        h.Verifier.Handler = (_, _) => Task.FromResult(
            VerificationResult.Fail(FailureInfo.Of(FailureCode.VerifyFailed, "error.verifyFailed"), "bad output"));

        h.Scheduler.Start();
        var id = await h.EnqueueAsync();
        var job = await h.WaitForStateAsync(id, JobState.Failed);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Single(h.OutputPaths.ReservationReleased);
        Assert.Single(h.OutputPaths.WorkDirectoriesDeleted);
    }
}
