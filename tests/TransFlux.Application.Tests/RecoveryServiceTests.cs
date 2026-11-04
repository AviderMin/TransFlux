using TransFlux.Application.Contracts;
using TransFlux.Application.Services;
using TransFlux.Domain.Jobs;
using Xunit;

namespace TransFlux.Application.Tests;

public class RecoveryServiceTests
{
    private static Job MakeJob(JobState state, Guid? id = null)
    {
        var source = TestData.MakeSource();
        return new Job
        {
            JobId = id ?? Guid.NewGuid(),
            RequestSnapshot = TestData.MakeRequest(source.MediaId),
            SourceFingerprint = source.Fingerprint,
            EnqueueOrder = 1,
            State = state,
            Revision = 1,
            CreatedUtc = DateTimeOffset.UnixEpoch,
            UpdatedUtc = DateTimeOffset.UnixEpoch,
        };
    }

    private static CommitJournalEntry MakeJournal(Guid jobId, Guid attemptId, CommitPhase phase)
        => new()
        {
            CommitToken = Guid.NewGuid().ToString("N"),
            AttemptId = attemptId,
            JobId = jobId,
            Phase = phase,
            TempPath = @"C:\work\temp.mp4",
            FinalPath = @"C:\out\final.mp4",
            SizeBytes = 1234,
            UpdatedUtc = DateTimeOffset.UnixEpoch,
        };

    [Fact]
    public async Task MarksActiveJobsInterrupted()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Running);
        await store.CreateJobAsync(job, CancellationToken.None);

        await RecoveryService.RunAsync(store, new FakeCommitter(), CancellationToken.None);

        var reloaded = await store.GetJobAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobState.Interrupted, reloaded!.State);
    }

    [Fact]
    public async Task PublishedJournal_IsReconciledAndRecordsOutput()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Committing);
        await store.CreateJobAsync(job, CancellationToken.None);
        var journal = MakeJournal(job.JobId, Guid.NewGuid(), CommitPhase.Published);
        await store.UpsertJournalAsync(journal, CancellationToken.None);
        var committer = new FakeCommitter();

        await RecoveryService.RunAsync(store, committer, CancellationToken.None);

        // Published journals must go through the same identity reconciliation as Prepared ones.
        Assert.Equal(1, committer.ReconcileCalls);
        Assert.Null(await store.GetJournalAsync(journal.CommitToken, CancellationToken.None));
        var outputs = await store.GetOutputsForJobAsync(job.JobId, CancellationToken.None);
        Assert.Single(outputs);
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(job.JobId, default))!.State);
    }

    [Fact]
    public async Task PublishedJournal_ReconcileSuccess_MarksAttemptAndJobSucceeded_WithoutRecordVerifier()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Committing);
        await store.CreateJobAsync(job, CancellationToken.None);
        var attempt = MakeAttempt(job.JobId);
        await store.CreateAttemptAsync(attempt, CancellationToken.None);
        var journal = MakeJournal(job.JobId, attempt.AttemptId, CommitPhase.Published);
        await store.UpsertJournalAsync(journal, CancellationToken.None);
        // Reconciler only: it cannot verify a recorded output through IOutputRecordVerifier, so a
        // successful journal reconciliation must itself complete the attempt and job.
        var committer = new ReconcilerOnlyCommitter();

        await RecoveryService.RunAsync(store, committer, CancellationToken.None);

        Assert.Equal(1, committer.ReconcileCalls);
        Assert.Null(await store.GetJournalAsync(journal.CommitToken, CancellationToken.None));
        Assert.Single(await store.GetOutputsForJobAsync(job.JobId, CancellationToken.None));
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.Equal(AttemptState.Succeeded, (await store.GetAttemptAsync(attempt.AttemptId, default))!.State);
    }

    [Fact]
    public async Task JournalForNonCommittingActiveJob_DoesNotMarkAttemptSucceeded()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Running);
        await store.CreateJobAsync(job, CancellationToken.None);
        var attempt = MakeAttempt(job.JobId) with { State = AttemptState.Running };
        await store.CreateAttemptAsync(attempt, CancellationToken.None);
        var journal = MakeJournal(job.JobId, attempt.AttemptId, CommitPhase.Published);
        await store.UpsertJournalAsync(journal, CancellationToken.None);

        await RecoveryService.RunAsync(store, new FakeCommitter(), CancellationToken.None);

        // The journal reconciles, but the job cannot legally become Succeeded from Running. The
        // attempt must stay untouched so it is not paired with a job that recovery later interrupts.
        Assert.Equal(JobState.Interrupted, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.Equal(AttemptState.Running, (await store.GetAttemptAsync(attempt.AttemptId, default))!.State);
    }

    [Fact]
    public async Task PublishedJournal_ReconcileFailure_KeepsJournalAndMovesJobToNeedsAttention()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Committing);
        await store.CreateJobAsync(job, CancellationToken.None);
        var journal = MakeJournal(job.JobId, Guid.NewGuid(), CommitPhase.Published);
        await store.UpsertJournalAsync(journal, CancellationToken.None);
        var committer = new FakeCommitter
        {
            OnReconcile = (_, _) => Task.FromResult(new CommitResult
            {
                Success = false,
                Failure = FailureInfo.Of(FailureCode.VerifyFailed, "COMMIT_PUBLISHED_MISSING", "final missing"),
            }),
        };

        await RecoveryService.RunAsync(store, committer, CancellationToken.None);

        Assert.Equal(1, committer.ReconcileCalls);
        Assert.NotNull(await store.GetJournalAsync(journal.CommitToken, CancellationToken.None));
        Assert.Empty(await store.GetOutputsForJobAsync(job.JobId, CancellationToken.None));
        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(job.JobId, default))!.State);
    }

    [Fact]
    public async Task PreparedJournal_ReconcileSuccess_RecordsOutputAndClosesJournal()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Committing);
        await store.CreateJobAsync(job, CancellationToken.None);
        var journal = MakeJournal(job.JobId, Guid.NewGuid(), CommitPhase.Prepared);
        await store.UpsertJournalAsync(journal, CancellationToken.None);
        var committer = new FakeCommitter();

        await RecoveryService.RunAsync(store, committer, CancellationToken.None);

        Assert.Equal(1, committer.ReconcileCalls);
        Assert.Null(await store.GetJournalAsync(journal.CommitToken, CancellationToken.None));
        Assert.Single(await store.GetOutputsForJobAsync(job.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task PreparedJournal_ReconcileFailure_MovesJobToNeedsAttention_AndKeepsJournal()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Committing);
        await store.CreateJobAsync(job, CancellationToken.None);
        var journal = MakeJournal(job.JobId, Guid.NewGuid(), CommitPhase.Prepared);
        await store.UpsertJournalAsync(journal, CancellationToken.None);
        var committer = new FakeCommitter
        {
            OnReconcile = (_, _) => Task.FromResult(new CommitResult
            {
                Success = false,
                Failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryFailed", "identity mismatch"),
            }),
        };

        await RecoveryService.RunAsync(store, committer, CancellationToken.None);

        Assert.NotNull(await store.GetJournalAsync(journal.CommitToken, CancellationToken.None));
        var reloaded = await store.GetJobAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobState.NeedsAttention, reloaded!.State);
    }

    [Fact]
    public async Task ExistingOutput_StillReconcilesIdentityBeforeClosingJournal()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Succeeded);
        await store.CreateJobAsync(job, CancellationToken.None);
        var attemptId = Guid.NewGuid();
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attemptId,
            FinalPath = @"C:\out\final.mp4",
            Size = 1234,
            CommitToken = "t",
            CommittedUtc = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);
        var journal = MakeJournal(job.JobId, attemptId, CommitPhase.Prepared);
        await store.UpsertJournalAsync(journal, CancellationToken.None);
        var committer = new FakeCommitter();

        await RecoveryService.RunAsync(store, committer, CancellationToken.None);

        // A database output record is historical fact, not proof that the file still exists.
        Assert.Equal(1, committer.ReconcileCalls);
        Assert.Null(await store.GetJournalAsync(journal.CommitToken, CancellationToken.None));
        Assert.Equal(0, committer.CommitCalls);
        // A terminal job must not be re-opened by the recovery success transition.
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(job.JobId, default))!.State);
    }

    [Fact]
    public async Task OneBrokenJournal_DoesNotAbortRecoveryOfOthers()
    {
        var store = new InMemoryJobStore();
        var jobA = MakeJob(JobState.Committing);
        var jobB = MakeJob(JobState.Committing);
        var running = MakeJob(JobState.Running);
        await store.CreateJobAsync(jobA, CancellationToken.None);
        await store.CreateJobAsync(jobB, CancellationToken.None);
        await store.CreateJobAsync(running, CancellationToken.None);

        var journalA = MakeJournal(jobA.JobId, Guid.NewGuid(), CommitPhase.Published);
        var journalB = MakeJournal(jobB.JobId, Guid.NewGuid(), CommitPhase.Published);
        await store.UpsertJournalAsync(journalA, CancellationToken.None);
        await store.UpsertJournalAsync(journalB, CancellationToken.None);
        store.BeforeDeleteJournal = token => token == journalA.CommitToken
            ? Task.FromException(new IOException("simulated db busy"))
            : Task.CompletedTask;

        await RecoveryService.RunAsync(store, new FakeCommitter(), CancellationToken.None);

        // The broken journal stays open and its job needs manual review...
        Assert.NotNull(await store.GetJournalAsync(journalA.CommitToken, CancellationToken.None));
        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(jobA.JobId, default))!.State);
        // ...but the other journal still reconciles and the active job still becomes Interrupted.
        Assert.Null(await store.GetJournalAsync(journalB.CommitToken, CancellationToken.None));
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(jobB.JobId, default))!.State);
        Assert.Equal(JobState.Interrupted, (await store.GetJobAsync(running.JobId, default))!.State);
    }

    [Fact]
    public async Task CommittingJob_ReadFailure_DoesNotAbortOtherRecoveryWork()
    {
        var store = new InMemoryJobStore();
        var committing = MakeJob(JobState.Committing);
        var running = MakeJob(JobState.Running);
        await store.CreateJobAsync(committing, CancellationToken.None);
        await store.CreateJobAsync(running, CancellationToken.None);
        var attempt = MakeAttempt(committing.JobId);
        await store.CreateAttemptAsync(attempt, CancellationToken.None);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = committing.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = @"C:outinal.mp4",
            Size = 1234,
            CommitToken = "t",
            CommittedUtc = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);
        store.BeforeGetOutputsForJob = jobId => jobId == committing.JobId
            ? Task.FromException(new IOException("simulated output-read failure"))
            : Task.CompletedTask;

        await RecoveryService.RunAsync(store, new FakeCommitter(), CancellationToken.None);

        // The broken row is isolated and flagged, and the remaining active job is still handled.
        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(committing.JobId, default))!.State);
        Assert.Equal(JobState.Interrupted, (await store.GetJobAsync(running.JobId, default))!.State);
    }

    [Fact]
    public async Task MarkActiveJobsInterrupted_RunsWhenCommittingSweepListingFails()
    {
        var store = new InMemoryJobStore();
        var committing = MakeJob(JobState.Committing);
        var running = MakeJob(JobState.Running);
        await store.CreateJobAsync(committing, CancellationToken.None);
        await store.CreateJobAsync(running, CancellationToken.None);
        store.BeforeGetJobs = states => states is not null && states.Contains(JobState.Committing)
            ? Task.FromException(new IOException("simulated job-list failure"))
            : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() =>
            RecoveryService.RunAsync(store, new FakeCommitter(), CancellationToken.None));

        // The finally block must still clear active jobs even when the previous phase failed.
        Assert.Equal(JobState.Interrupted, (await store.GetJobAsync(running.JobId, default))!.State);
    }

    [Fact]
    public async Task FallsBackToCommitAsync_WhenCommitterIsNotReconciler()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Committing);
        await store.CreateJobAsync(job, CancellationToken.None);
        var journal = MakeJournal(job.JobId, Guid.NewGuid(), CommitPhase.Prepared);
        await store.UpsertJournalAsync(journal, CancellationToken.None);
        var committer = new PlainCommitter();

        await RecoveryService.RunAsync(store, committer, CancellationToken.None);

        Assert.Equal(1, committer.CommitCalls);
        Assert.Null(await store.GetJournalAsync(journal.CommitToken, CancellationToken.None));
    }

    [Fact]
    public async Task CommittingJob_RecordedOutputMissingFinal_MovesToNeedsAttention()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Committing);
        await store.CreateJobAsync(job, CancellationToken.None);
        var attempt = MakeAttempt(job.JobId);
        await store.CreateAttemptAsync(attempt, CancellationToken.None);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = @"C:\out\missing.mp4",
            Size = 1234,
            CommitToken = "t",
            CommittedUtc = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);
        var committer = new FakeCommitter
        {
            OnVerifyOutput = (_, _) => Task.FromResult(new CommitResult
            {
                Success = false,
                Failure = FailureInfo.Of(FailureCode.VerifyFailed, "COMMIT_OUTPUT_MISSING", "final missing"),
            }),
        };

        await RecoveryService.RunAsync(store, committer, CancellationToken.None);

        Assert.Equal(1, committer.VerifyOutputCalls);
        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.Single(await store.GetOutputsForJobAsync(job.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task CommittingJob_RecordedOutputVerified_MarksSucceeded()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Committing);
        await store.CreateJobAsync(job, CancellationToken.None);
        var attempt = MakeAttempt(job.JobId);
        await store.CreateAttemptAsync(attempt, CancellationToken.None);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = @"C:\out\verified.mp4",
            Size = 1234,
            CommitToken = "t",
            CommittedUtc = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);
        var committer = new FakeCommitter();

        await RecoveryService.RunAsync(store, committer, CancellationToken.None);

        Assert.Equal(1, committer.VerifyOutputCalls);
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.Equal(AttemptState.Succeeded, (await store.GetAttemptAsync(attempt.AttemptId, default))!.State);
    }

    private static Attempt MakeAttempt(Guid jobId)
    {
        var source = TestData.MakeSource();
        return new Attempt
        {
            AttemptId = Guid.NewGuid(),
            JobId = jobId,
            Ordinal = 1,
            PlanSnapshot = TestData.MakePlan(),
            TempPath = @"C:\work\temp.mp4",
            ReservedFinalPath = @"C:\out\final.mp4",
            State = AttemptState.Committing,
            StartedUtc = DateTimeOffset.UnixEpoch,
        };
    }

    private sealed class PlainCommitter : IOutputCommitter
    {
        public int CommitCalls { get; private set; }

        public Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken ct)
        {
            CommitCalls++;
            return Task.FromResult(new CommitResult { Success = true, CommitToken = "t", FinalPath = request.FinalPath });
        }
    }

    private sealed class ReconcilerOnlyCommitter : IOutputCommitter, ICommitReconciler
    {
        public int ReconcileCalls { get; private set; }

        public Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken ct)
            => throw new InvalidOperationException("CommitAsync must not be used when a reconciler is available.");

        public Task<CommitResult> ReconcileAsync(CommitJournalEntry entry, CancellationToken ct)
        {
            ReconcileCalls++;
            return Task.FromResult(new CommitResult
            {
                Success = true,
                CommitToken = entry.CommitToken,
                FinalPath = entry.FinalPath,
            });
        }
    }
}
