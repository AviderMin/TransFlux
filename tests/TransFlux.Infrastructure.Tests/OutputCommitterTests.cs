using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;

namespace TransFlux.Infrastructure.Tests;

public sealed class OutputCommitterTests
{
    private sealed record Harness(SqliteJobStore Store, OutputCommitter Committer, WindowsFileSystemService Fs, Job Job, Attempt Attempt);

    private static async Task<Harness> CreateAsync(TempDir dir)
    {
        var fs = new WindowsFileSystemService();
        var clock = new SystemClock();
        var store = new SqliteJobStore(Path.Combine(dir.Path, "state.db"));
        await store.InitializeAsync(default);
        var job = TestData.NewJob();
        await store.CreateJobAsync(job, default);
        var attempt = TestData.NewAttempt(job.JobId);
        await store.CreateAttemptAsync(attempt, default);
        return new Harness(store, new OutputCommitter(store, fs, clock), fs, job, attempt);
    }

    [Fact]
    public async Task Commit_Publishes_And_Records_Output()
    {
        using var dir = new TempDir();
        var h = await CreateAsync(dir);
        await using var store = h.Store;

        var temp = dir.NewFile("a.tmp", "0123456789");
        var final = Path.Combine(dir.Path, "out.mp4");

        var result = await h.Committer.CommitAsync(new CommitRequest
        {
            JobId = h.Job.JobId,
            AttemptId = h.Attempt.AttemptId,
            TempPath = temp,
            FinalPath = final,
            ExpectedSize = 10,
        }, default);

        Assert.True(result.Success, result.Failure?.TechnicalDetail);
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(temp));

        var outputs = await store.GetOutputsForJobAsync(h.Job.JobId, default);
        Assert.Single(outputs);
        Assert.Equal(final, outputs[0].FinalPath);
        Assert.Equal(10, outputs[0].Size);
        Assert.Equal(result.CommitToken, outputs[0].CommitToken);
        Assert.Empty(await store.GetOpenJournalsAsync(default));
    }

    [Fact]
    public async Task Commit_Refuses_Overwrite_WhenFinalExists()
    {
        using var dir = new TempDir();
        var h = await CreateAsync(dir);
        await using var store = h.Store;

        var temp = dir.NewFile("a.tmp", "0123456789");
        var final = dir.NewFile("out.mp4", "already-here");

        var result = await h.Committer.CommitAsync(new CommitRequest
        {
            JobId = h.Job.JobId,
            AttemptId = h.Attempt.AttemptId,
            TempPath = temp,
            FinalPath = final,
            ExpectedSize = 10,
        }, default);

        Assert.False(result.Success);
        Assert.Equal(FailureCode.OutputConflict, result.Failure!.Code);
        Assert.True(File.Exists(temp));                        // temp is left intact for cleanup
        Assert.Equal("already-here", File.ReadAllText(final)); // never overwritten
        Assert.Empty(await store.GetOpenJournalsAsync(default)); // failure journal closed (Failed)
        Assert.Empty(await store.GetOutputsForJobAsync(h.Job.JobId, default));
    }

    [Fact]
    public async Task Reconcile_Prepared_WithLegacyFinalOnly_RequiresAttention()
    {
        using var dir = new TempDir();
        var h = await CreateAsync(dir);
        await using var store = h.Store;

        var temp = dir.NewFile("a.tmp", "abcdef");     // 6 bytes
        var final = Path.Combine(dir.Path, "out.mp4");
        File.Move(temp, final); // simulate rename succeeded but journal stayed Prepared

        var entry = new CommitJournalEntry
        {
            CommitToken = "recon-tok",
            AttemptId = h.Attempt.AttemptId,
            JobId = h.Job.JobId,
            Phase = CommitPhase.Prepared,
            TempPath = temp,
            FinalPath = final,
            SizeBytes = 6,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        await store.UpsertJournalAsync(entry, default);

        var result = await h.Committer.ReconcileAsync(entry, default);
        Assert.False(result.Success);
        Assert.Equal(FailureCode.OutputConflict, result.Failure!.Code);
        Assert.Empty(await store.GetOutputsForJobAsync(h.Job.JobId, default));
        Assert.Single(await store.GetOpenJournalsAsync(default)); // kept for NeedsAttention
    }

    [Fact]
    public async Task Reconcile_Prepared_Unresolved_KeepsJournal_ReturnsFailure()
    {
        using var dir = new TempDir();
        var h = await CreateAsync(dir);
        await using var store = h.Store;

        var entry = new CommitJournalEntry
        {
            CommitToken = "recon-no-final",
            AttemptId = h.Attempt.AttemptId,
            JobId = h.Job.JobId,
            Phase = CommitPhase.Prepared,
            TempPath = Path.Combine(dir.Path, "a.tmp"),
            FinalPath = Path.Combine(dir.Path, "out.mp4"),
            SizeBytes = 6,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        await store.UpsertJournalAsync(entry, default);

        var result = await h.Committer.ReconcileAsync(entry, default);
        Assert.False(result.Success);
        Assert.Single(await store.GetOpenJournalsAsync(default)); // kept for NeedsAttention
        Assert.Empty(await store.GetOutputsForJobAsync(h.Job.JobId, default));
    }

    [Fact]
    public async Task Reconcile_Prepared_WithTemp_MovesAndPublishes()
    {
        using var dir = new TempDir();
        var h = await CreateAsync(dir);
        await using var store = h.Store;

        var temp = dir.NewFile("a.tmp", "abcdef"); // 6 bytes, final does not exist
        var final = Path.Combine(dir.Path, "out.mp4");
        var entry = new CommitJournalEntry
        {
            CommitToken = "recon-temp",
            AttemptId = h.Attempt.AttemptId,
            JobId = h.Job.JobId,
            Phase = CommitPhase.Prepared,
            TempPath = temp,
            FinalPath = final,
            SizeBytes = 6,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        await store.UpsertJournalAsync(entry, default);

        var result = await h.Committer.ReconcileAsync(entry, default);
        Assert.True(result.Success, result.Failure?.TechnicalDetail);
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(temp));
        Assert.Single(await store.GetOutputsForJobAsync(h.Job.JobId, default));
        Assert.Empty(await store.GetOpenJournalsAsync(default));
    }

    [Fact]
    public async Task Reconcile_Published_MissingFinal_FailsAndKeepsJournal()
    {
        using var dir = new TempDir();
        var h = await CreateAsync(dir);
        await using var store = h.Store;

        var entry = new CommitJournalEntry
        {
            CommitToken = "recon-published-missing",
            AttemptId = h.Attempt.AttemptId,
            JobId = h.Job.JobId,
            Phase = CommitPhase.Published,
            TempPath = Path.Combine(dir.Path, "a.tmp"),
            FinalPath = Path.Combine(dir.Path, "missing.mp4"),
            SizeBytes = 6,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        await store.UpsertJournalAsync(entry, default);

        var result = await h.Committer.ReconcileAsync(entry, default);
        Assert.False(result.Success);
        Assert.Equal(FailureCode.VerifyFailed, result.Failure!.Code);
        Assert.Single(await store.GetOpenJournalsAsync(default)); // left open for NeedsAttention
    }
}
