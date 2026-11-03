using TransFlux.Application.Contracts;
using TransFlux.Application.Services;
using TransFlux.Domain.Jobs;

namespace TransFlux.Infrastructure.Tests;

/// <summary>End-to-end recovery tests using a real SQLite database and real temporary files.
/// These cover the R01 failure mode: a journal or output row must never turn a missing/replaced
/// final file into a Succeeded result.</summary>
public sealed class RecoveryServiceIntegrationTests
{
    private const string PublishedContent = "0123456789abcdef"; // 16 bytes

    private static SqliteJobStore NewStore(TempDir dir)
        => new(Path.Combine(dir.Path, "state.db"));

    private static async Task<(Job Job, Attempt Attempt)> CreateCommittingJobAsync(SqliteJobStore store)
    {
        var job = TestData.NewJob(state: JobState.Committing, revision: 1);
        await store.CreateJobAsync(job, CancellationToken.None);
        var attempt = TestData.NewAttempt(job.JobId) with { State = AttemptState.Committing };
        await store.CreateAttemptAsync(attempt, CancellationToken.None);
        return (job, attempt);
    }

    private static Task RunRecoveryAsync(SqliteJobStore store)
        => RecoveryService.RunAsync(store,
            new OutputCommitter(store, new WindowsFileSystemService(), new SystemClock()),
            CancellationToken.None);

    /// <summary>Produces a real Published journal carrying a content hash: the commit crosses the
    /// rename and journal write, then a one-shot filesystem fault makes the post-move size check
    /// fail exactly the way a crashed/uncertain commit leaves evidence behind.</summary>
    private static async Task<(CommitJournalEntry Journal, string FinalPath)> CreatePublishedJournalAsync(
        SqliteJobStore store, TempDir dir, Job job, Attempt attempt)
    {
        var staging = Path.Combine(dir.Path, "staging");
        Directory.CreateDirectory(staging);
        var temp = Path.Combine(staging, "attempt.tmp");
        File.WriteAllText(temp, PublishedContent);
        var final = Path.Combine(dir.Path, "published.mp4");

        var faultyFs = new FailOnceOnFinalLengthFileSystem(final);
        var committer = new OutputCommitter(store, faultyFs, new SystemClock());
        var result = await committer.CommitAsync(new CommitRequest
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            TempPath = temp,
            FinalPath = final,
            ExpectedSize = PublishedContent.Length,
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(File.Exists(final));
        var journal = Assert.Single(await store.GetOpenJournalsAsync(CancellationToken.None));
        Assert.Equal(CommitPhase.Published, journal.Phase);
        Assert.Equal(final, journal.FinalPath);
        return (journal, final);
    }

    [Fact]
    public async Task PublishedJournal_MissingFinal_KeepsJournalAndMovesJobToNeedsAttention()
    {
        using var dir = new TempDir();
        await using var store = NewStore(dir);
        await store.InitializeAsync(default);
        var (job, attempt) = await CreateCommittingJobAsync(store);
        var (journal, final) = await CreatePublishedJournalAsync(store, dir, job, attempt);

        File.Delete(final);
        await RunRecoveryAsync(store);

        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.NotNull(await store.GetJournalAsync(journal.CommitToken, default));
        Assert.Empty(await store.GetOutputsForJobAsync(job.JobId, default));
    }

    [Fact]
    public async Task PublishedJournal_WithExistingOutputRecord_MissingFinal_StaysNeedsAttention()
    {
        using var dir = new TempDir();
        await using var store = NewStore(dir);
        await store.InitializeAsync(default);
        var (job, attempt) = await CreateCommittingJobAsync(store);
        var (journal, final) = await CreatePublishedJournalAsync(store, dir, job, attempt);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = final,
            Size = PublishedContent.Length,
            VerificationSummary = "stale record",
            CommittedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            CommitToken = journal.CommitToken,
        }, default);

        File.Delete(final);
        await RunRecoveryAsync(store);

        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.NotNull(await store.GetJournalAsync(journal.CommitToken, default));
        Assert.Single(await store.GetOutputsForJobAsync(job.JobId, default)); // historical fact kept
    }

    [Fact]
    public async Task PublishedJournal_SameLengthReplacement_KeepsJournalAndMovesJobToNeedsAttention()
    {
        using var dir = new TempDir();
        await using var store = NewStore(dir);
        await store.InitializeAsync(default);
        var (job, attempt) = await CreateCommittingJobAsync(store);
        var (journal, final) = await CreatePublishedJournalAsync(store, dir, job, attempt);

        // Same length, different bytes: size checks alone must not accept it.
        File.WriteAllText(final, new string('Z', PublishedContent.Length));
        await RunRecoveryAsync(store);

        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.NotNull(await store.GetJournalAsync(journal.CommitToken, default));
        Assert.Empty(await store.GetOutputsForJobAsync(job.JobId, default));
    }

    [Fact]
    public async Task PublishedJournal_Normal_RecordsOutputAndIsIdempotent()
    {
        using var dir = new TempDir();
        await using var store = NewStore(dir);
        await store.InitializeAsync(default);
        var (job, attempt) = await CreateCommittingJobAsync(store);
        var (journal, final) = await CreatePublishedJournalAsync(store, dir, job, attempt);

        await RunRecoveryAsync(store);

        Assert.Null(await store.GetJournalAsync(journal.CommitToken, default));
        var output = Assert.Single(await store.GetOutputsForJobAsync(job.JobId, default));
        Assert.Equal(final, output.FinalPath);
        Assert.Equal(PublishedContent.Length, output.Size);
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.Equal(AttemptState.Succeeded, (await store.GetAttemptAsync(attempt.AttemptId, default))!.State);

        // A second startup pass must not duplicate outputs or flap the job state.
        await RunRecoveryAsync(store);
        Assert.Single(await store.GetOutputsForJobAsync(job.JobId, default));
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(job.JobId, default))!.State);
    }

    [Fact]
    public async Task PublishedJournal_LegacyWithoutHash_KeepsJournalAndMovesJobToNeedsAttention()
    {
        using var dir = new TempDir();
        await using var store = NewStore(dir);
        await store.InitializeAsync(default);
        var (job, attempt) = await CreateCommittingJobAsync(store);
        var final = dir.NewFile("legacy.mp4", "abcdef"); // 6 bytes, no journal identity
        var journal = new CommitJournalEntry
        {
            CommitToken = "legacy-published",
            AttemptId = attempt.AttemptId,
            JobId = job.JobId,
            Phase = CommitPhase.Published,
            TempPath = Path.Combine(dir.Path, "gone.tmp"),
            FinalPath = final,
            SizeBytes = 6,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        await store.UpsertJournalAsync(journal, default);

        await RunRecoveryAsync(store);

        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.NotNull(await store.GetJournalAsync(journal.CommitToken, default));
        Assert.Empty(await store.GetOutputsForJobAsync(job.JobId, default));
    }

    [Fact]
    public async Task PreparedJournal_WrongStoredHash_KeepsJournalAndStagedTemp()
    {
        using var dir = new TempDir();
        await using var store = NewStore(dir);
        await store.InitializeAsync(default);
        var (job, attempt) = await CreateCommittingJobAsync(store);
        var temp = dir.NewFile("staged.tmp", "abcdef"); // 6 bytes
        var final = Path.Combine(dir.Path, "out.mp4");
        var journal = new CommitJournalEntry
        {
            CommitToken = "wrong-hash",
            AttemptId = attempt.AttemptId,
            JobId = job.JobId,
            Phase = CommitPhase.Prepared,
            TempPath = temp,
            FinalPath = final,
            SizeBytes = 6,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        await ((ICommitJournalContentStore)store)
            .UpsertJournalWithContentHashAsync(journal, new string('0', 64), CancellationToken.None);

        await RunRecoveryAsync(store);

        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.NotNull(await store.GetJournalAsync(journal.CommitToken, default));
        Assert.True(File.Exists(temp), "an identity-mismatched temp must be preserved for inspection");
        Assert.False(File.Exists(final));
        Assert.Empty(await store.GetOutputsForJobAsync(job.JobId, default));
    }

    [Fact]
    public async Task CommittingJob_OutputRecordMissingFinal_StaysNeedsAttention()
    {
        using var dir = new TempDir();
        await using var store = NewStore(dir);
        await store.InitializeAsync(default);
        var (job, attempt) = await CreateCommittingJobAsync(store);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = Path.Combine(dir.Path, "missing.mp4"),
            Size = 42,
            VerificationSummary = "recorded",
            CommittedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            CommitToken = "missing-output",
        }, default);

        await RunRecoveryAsync(store);

        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.Single(await store.GetOutputsForJobAsync(job.JobId, default));
    }

    [Fact]
    public async Task CommittingJob_OutputRecordJournalHashMismatch_StaysNeedsAttention()
    {
        using var dir = new TempDir();
        await using var store = NewStore(dir);
        await store.InitializeAsync(default);
        var (job, attempt) = await CreateCommittingJobAsync(store);
        var final = dir.NewFile("hash-mismatch.mp4", new string('x', 42));
        var record = new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = final,
            Size = 42,
            VerificationSummary = "recorded",
            CommittedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            CommitToken = "record-hash",
        };
        await store.SaveOutputAsync(record, default);
        var wrongHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("different content")));
        var journal = new CommitJournalEntry
        {
            CommitToken = "record-hash",
            AttemptId = attempt.AttemptId,
            JobId = job.JobId,
            Phase = CommitPhase.Failed,
            TempPath = Path.Combine(dir.Path, "gone.tmp"),
            FinalPath = final,
            SizeBytes = 42,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        await ((ICommitJournalContentStore)store)
            .UpsertJournalWithContentHashAsync(journal, wrongHash, default);

        await RunRecoveryAsync(store);

        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.Single(await store.GetOutputsForJobAsync(job.JobId, default));
    }

    [Fact]
    public async Task CommittingJob_OutputRecordWithMatchingFile_RecoversSucceeded()
    {
        using var dir = new TempDir();
        await using var store = NewStore(dir);
        await store.InitializeAsync(default);
        var (job, attempt) = await CreateCommittingJobAsync(store);
        var final = dir.NewFile("present.mp4", new string('x', 42));
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = final,
            Size = 42,
            VerificationSummary = "recorded",
            CommittedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            CommitToken = "present-output",
        }, default);

        await RunRecoveryAsync(store);

        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(job.JobId, default))!.State);
        Assert.Equal(AttemptState.Succeeded, (await store.GetAttemptAsync(attempt.AttemptId, default))!.State);
    }

    private sealed class FailOnceOnFinalLengthFileSystem : IFileSystemService
    {
        private readonly WindowsFileSystemService _inner = new();
        private readonly string _finalPath;
        private int _failuresRemaining = 1;

        public FailOnceOnFinalLengthFileSystem(string finalPath)
            => _finalPath = Path.GetFullPath(finalPath);

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public long GetAvailableFreeSpace(string path) => _inner.GetAvailableFreeSpace(path);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public string NormalizePath(string path) => _inner.NormalizePath(path);
        public bool PathsReferToSameFile(string a, string b) => _inner.PathsReferToSameFile(a, b);
        public Task<FileIdentity?> GetIdentityAsync(string path, CancellationToken ct) => _inner.GetIdentityAsync(path, ct);
        public FileIdentity GetIdentity(string path) => _inner.GetIdentity(path);
        public IReadOnlyList<string> EnumerateFiles(string directory, bool recursive) => _inner.EnumerateFiles(directory, recursive);
        public string GetFileNameWithoutExtension(string path) => _inner.GetFileNameWithoutExtension(path);
        public string GetExtension(string path) => _inner.GetExtension(path);
        public DateTimeOffset GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);
        public void MoveNoReplace(string source, string destination) => _inner.MoveNoReplace(source, destination);

        public long GetFileLength(string path)
        {
            if (_failuresRemaining > 0 && string.Equals(
                    Path.GetFullPath(path), _finalPath, StringComparison.OrdinalIgnoreCase))
            {
                _failuresRemaining--;
                throw new IOException("Injected final length read failure.");
            }
            return _inner.GetFileLength(path);
        }
    }
}
