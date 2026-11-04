using TransFlux.Application.Contracts;
using TransFlux.Application.Services;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Presets;

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
            VerificationSummary = "Verified MP4: 1 video / 1 audio",
        }, default);

        Assert.True(result.Success, result.Failure?.TechnicalDetail);
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(temp));

        var outputs = await store.GetOutputsForJobAsync(h.Job.JobId, default);
        Assert.Single(outputs);
        Assert.Equal(final, outputs[0].FinalPath);
        Assert.Equal(10, outputs[0].Size);
        Assert.Equal(result.CommitToken, outputs[0].CommitToken);
        Assert.Equal("Verified MP4: 1 video / 1 audio", outputs[0].VerificationSummary);
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
        // The Failed row is retained for crash-window recovery; startup reconciliation closes it.
        var failed = Assert.Single(await store.GetOpenJournalsAsync(default));
        Assert.Equal(CommitPhase.Failed, failed.Phase);
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

    [Fact]
    public async Task Commit_OutputRecordWriteFailsAfterRename_LeavesPublishedJournalAndFinalFile()
    {
        using var dir = new TempDir();
        var fs = new WindowsFileSystemService();
        var clock = new SystemClock();
        var inner = new SqliteJobStore(Path.Combine(dir.Path, "state.db"));
        await inner.InitializeAsync(default);
        await using var store = inner;
        var job = TestData.NewJob(state: JobState.Committing);
        await store.CreateJobAsync(job, default);
        var attempt = TestData.NewAttempt(job.JobId) with { State = AttemptState.Committing };
        await store.CreateAttemptAsync(attempt, default);

        var failingStore = new FailingSaveOutputStore(inner) { SaveOutputFailuresRemaining = 1 };
        var committer = new OutputCommitter(failingStore, fs, clock);
        var temp = dir.NewFile("a.tmp", "0123456789");
        var final = Path.Combine(dir.Path, "out.mp4");

        await Assert.ThrowsAsync<IOException>(() => committer.CommitAsync(new CommitRequest
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            TempPath = temp,
            FinalPath = final,
            ExpectedSize = 10,
        }, default));

        // The rename succeeded before the database write failed: the only output must survive.
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(temp));
        var journal = Assert.Single(await store.GetOpenJournalsAsync(default));
        Assert.Equal(CommitPhase.Published, journal.Phase);
        Assert.Empty(await store.GetOutputsForJobAsync(job.JobId, default));

        // A later startup can verify the file identity and finish the record.
        await RecoveryService.RunAsync(store, new OutputCommitter(store, fs, clock), default);
        var output = Assert.Single(await store.GetOutputsForJobAsync(job.JobId, default));
        Assert.Equal(final, output.FinalPath);
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(job.JobId, default))!.State);
    }

    /// <summary>Delegating store that fails the OutputRecord write once; used to inject the
    /// "rename succeeded but database write failed" crash window deterministically.</summary>
    private sealed class FailingSaveOutputStore : IJobStore, ICommitJournalContentStore
    {
        private readonly SqliteJobStore _inner;

        public FailingSaveOutputStore(SqliteJobStore inner) => _inner = inner;

        public int SaveOutputFailuresRemaining { get; set; }

        public Task InitializeAsync(CancellationToken ct) => _inner.InitializeAsync(ct);
        public Task<long> NextEnqueueOrderAsync(CancellationToken ct) => _inner.NextEnqueueOrderAsync(ct);
        public Task<bool> ReorderQueuedAsync(Guid jobId, Guid targetJobId, bool placeBefore, CancellationToken ct)
            => _inner.ReorderQueuedAsync(jobId, targetJobId, placeBefore, ct);
        public Task CreateJobAsync(Job job, CancellationToken ct) => _inner.CreateJobAsync(job, ct);
        public Task UpdateJobAsync(Job job, long expectedRevision, CancellationToken ct)
            => _inner.UpdateJobAsync(job, expectedRevision, ct);
        public Task<Job?> GetJobAsync(Guid id, CancellationToken ct) => _inner.GetJobAsync(id, ct);
        public Task<IReadOnlyList<Job>> GetJobsAsync(IReadOnlyCollection<JobState>? states, CancellationToken ct)
            => _inner.GetJobsAsync(states, ct);
        public Task CreateAttemptAsync(Attempt attempt, CancellationToken ct) => _inner.CreateAttemptAsync(attempt, ct);
        public Task UpdateAttemptAsync(Attempt attempt, CancellationToken ct) => _inner.UpdateAttemptAsync(attempt, ct);
        public Task<Attempt?> GetAttemptAsync(Guid id, CancellationToken ct) => _inner.GetAttemptAsync(id, ct);
        public Task<IReadOnlyList<Attempt>> GetAttemptsForJobAsync(Guid jobId, CancellationToken ct)
            => _inner.GetAttemptsForJobAsync(jobId, ct);

        public Task SaveOutputAsync(OutputRecord record, CancellationToken ct)
        {
            if (SaveOutputFailuresRemaining > 0)
            {
                SaveOutputFailuresRemaining--;
                throw new IOException("Injected output record write failure.");
            }
            return _inner.SaveOutputAsync(record, ct);
        }

        public Task<IReadOnlyList<OutputRecord>> GetOutputsAsync(CancellationToken ct) => _inner.GetOutputsAsync(ct);
        public Task<IReadOnlyList<OutputRecord>> GetOutputsForJobAsync(Guid jobId, CancellationToken ct)
            => _inner.GetOutputsForJobAsync(jobId, ct);
        public Task UpsertJournalAsync(CommitJournalEntry entry, CancellationToken ct) => _inner.UpsertJournalAsync(entry, ct);
        public Task<CommitJournalEntry?> GetJournalAsync(string token, CancellationToken ct) => _inner.GetJournalAsync(token, ct);
        public Task<IReadOnlyList<CommitJournalEntry>> GetOpenJournalsAsync(CancellationToken ct) => _inner.GetOpenJournalsAsync(ct);
        public Task DeleteJournalAsync(string token, CancellationToken ct) => _inner.DeleteJournalAsync(token, ct);
        public Task<IReadOnlyList<Preset>> GetPresetsAsync(CancellationToken ct) => _inner.GetPresetsAsync(ct);
        public Task UpsertPresetAsync(Preset preset, CancellationToken ct) => _inner.UpsertPresetAsync(preset, ct);
        public Task DeletePresetAsync(Guid presetId, CancellationToken ct) => _inner.DeletePresetAsync(presetId, ct);
        public Task SaveCapabilityAsync(string cacheKey, string payloadJson, DateTimeOffset testedUtc, CancellationToken ct)
            => _inner.SaveCapabilityAsync(cacheKey, payloadJson, testedUtc, ct);
        public Task<(string PayloadJson, DateTimeOffset TestedUtc)?> GetCapabilityAsync(string cacheKey, CancellationToken ct)
            => _inner.GetCapabilityAsync(cacheKey, ct);
        public Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct) => _inner.GetSettingsAsync(ct);
        public Task SaveSettingAsync(string key, string valueJson, CancellationToken ct) => _inner.SaveSettingAsync(key, valueJson, ct);
        public Task<IReadOnlyList<Job>> MarkActiveJobsInterruptedAsync(CancellationToken ct) => _inner.MarkActiveJobsInterruptedAsync(ct);

        Task ICommitJournalContentStore.UpsertJournalWithContentHashAsync(CommitJournalEntry entry, string contentSha256, CancellationToken ct)
            => ((ICommitJournalContentStore)_inner).UpsertJournalWithContentHashAsync(entry, contentSha256, ct);

        Task<string?> ICommitJournalContentStore.GetJournalContentHashAsync(string token, CancellationToken ct)
            => ((ICommitJournalContentStore)_inner).GetJournalContentHashAsync(token, ct);
    }
}
