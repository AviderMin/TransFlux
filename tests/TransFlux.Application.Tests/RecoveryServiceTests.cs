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
    public async Task PublishedJournal_IsClosed_AndOutputRecorded()
    {
        var store = new InMemoryJobStore();
        var job = MakeJob(JobState.Committing);
        await store.CreateJobAsync(job, CancellationToken.None);
        var journal = MakeJournal(job.JobId, Guid.NewGuid(), CommitPhase.Published);
        await store.UpsertJournalAsync(journal, CancellationToken.None);

        await RecoveryService.RunAsync(store, new FakeCommitter(), CancellationToken.None);

        Assert.Null(await store.GetJournalAsync(journal.CommitToken, CancellationToken.None));
        var outputs = await store.GetOutputsForJobAsync(job.JobId, CancellationToken.None);
        Assert.Single(outputs);
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
    public async Task ExistingOutput_ClosesJournal_WithoutCallingCommitter()
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

        Assert.Null(await store.GetJournalAsync(journal.CommitToken, CancellationToken.None));
        Assert.Equal(0, committer.ReconcileCalls);
        Assert.Equal(0, committer.CommitCalls);
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

    private sealed class PlainCommitter : IOutputCommitter
    {
        public int CommitCalls { get; private set; }

        public Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken ct)
        {
            CommitCalls++;
            return Task.FromResult(new CommitResult { Success = true, CommitToken = "t", FinalPath = request.FinalPath });
        }
    }
}
