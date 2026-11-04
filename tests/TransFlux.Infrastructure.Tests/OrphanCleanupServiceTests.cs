using TransFlux.Application.Contracts;
using TransFlux.Application.Services;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;

namespace TransFlux.Infrastructure.Tests;

/// <summary>A05: the startup orphan sweep must remove only old terminal staging artifacts and
/// must never delete a user's final output file.</summary>
public sealed class OrphanCleanupServiceTests
{
    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private static async Task<(Job Job, ReservedOutput Reserved)> AddStagedJobAsync(
        SqliteJobStore store, OutputPathService paths, string sourcePath,
        JobState state, DateTimeOffset updatedUtc, int ordinal = 1)
    {
        var job = TestData.NewJob(state: state, revision: 1) with { UpdatedUtc = updatedUtc };
        await store.CreateJobAsync(job, CancellationToken.None);

        var reserve = await paths.ReserveAsync(new OutputReservationRequest
        {
            SourcePath = sourcePath,
            Container = ContainerFormat.Mp4,
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.True(reserve.IsSuccess);
        var reserved = reserve.Value!;

        var attempt = TestData.NewAttempt(job.JobId, ordinal,
            Path.Combine(reserved.WorkDirectory, $"attempt-{ordinal}.mp4"), reserved.FinalPath);
        File.WriteAllText(attempt.TempPath, "staged-output");
        await store.CreateAttemptAsync(attempt, CancellationToken.None);
        return (job, reserved);
    }

    [Fact]
    public async Task OldTerminalJobs_AreCleaned_AndFinalOutputFilesArePreserved()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(Path.Combine(dir.Path, "state.db"));
        await store.InitializeAsync(default);
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var paths = new OutputPathService(new WindowsFileSystemService(), clock);
        var source = dir.NewFile("source.mp4");

        var staged = new List<ReservedOutput>();
        foreach (var state in new[] { JobState.Succeeded, JobState.Failed, JobState.Canceled, JobState.Interrupted })
        {
            var (_, reserved) = await AddStagedJobAsync(store, paths, source, state, clock.UtcNow.AddDays(-3));
            File.WriteAllText(reserved.FinalPath, "user-final-output");
            staged.Add(reserved);
        }

        await OrphanCleanupService.RunAsync(store, paths, clock, CancellationToken.None);

        foreach (var reserved in staged)
        {
            Assert.False(Directory.Exists(reserved.WorkDirectory), "terminal staging directory must be deleted");
            Assert.False(File.Exists(reserved.FinalPath + ".transflux-reserve"), "stale reservation lock must be released");
            Assert.True(File.Exists(reserved.FinalPath), "the user's final output file must never be deleted");
        }
    }

    [Fact]
    public async Task ActiveNeedsAttentionRecentAndJournaledJobs_KeepTheirStaging()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(Path.Combine(dir.Path, "state.db"));
        await store.InitializeAsync(default);
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var paths = new OutputPathService(new WindowsFileSystemService(), clock);
        var source = dir.NewFile("source.mp4");

        var active = await AddStagedJobAsync(store, paths, source, JobState.Running, clock.UtcNow.AddDays(-3));
        var attention = await AddStagedJobAsync(store, paths, source, JobState.NeedsAttention, clock.UtcNow.AddDays(-3));
        var recent = await AddStagedJobAsync(store, paths, source, JobState.Succeeded, clock.UtcNow.AddHours(-1));
        var journaled = await AddStagedJobAsync(store, paths, source, JobState.Failed, clock.UtcNow.AddDays(-3));
        var attempt = Assert.Single(await store.GetAttemptsForJobAsync(journaled.Job.JobId, default));
        await store.UpsertJournalAsync(new CommitJournalEntry
        {
            CommitToken = Guid.NewGuid().ToString("N"),
            AttemptId = attempt.AttemptId,
            JobId = journaled.Job.JobId,
            Phase = CommitPhase.Prepared,
            TempPath = attempt.TempPath,
            FinalPath = attempt.ReservedFinalPath,
            SizeBytes = 1,
            UpdatedUtc = clock.UtcNow,
        }, default);

        await OrphanCleanupService.RunAsync(store, paths, clock, CancellationToken.None);

        foreach (var kept in new[] { active, attention, recent, journaled })
        {
            Assert.True(Directory.Exists(kept.Reserved.WorkDirectory), "non-eligible staging must be preserved");
            Assert.True(File.Exists(kept.Reserved.FinalPath + ".transflux-reserve"), "non-eligible reservation lock must be preserved");
        }
    }

    [Fact]
    public async Task UnavailableStore_DoesNotBreakStartup()
    {
        using var dir = new TempDir();
        var store = new SqliteJobStore(Path.Combine(dir.Path, "state.db"));
        await store.InitializeAsync(default);
        await store.DisposeAsync();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var paths = new OutputPathService(new WindowsFileSystemService(), clock);

        // A broken/unavailable store must never throw out of the maintenance sweep.
        await OrphanCleanupService.RunAsync(store, paths, clock, CancellationToken.None);
    }

    [Fact]
    public async Task AttemptOutsideTransfluxWorkLayout_IsNeverDeleted()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(Path.Combine(dir.Path, "state.db"));
        await store.InitializeAsync(default);
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var paths = new OutputPathService(new WindowsFileSystemService(), clock);

        var job = TestData.NewJob(state: JobState.Failed, revision: 1) with { UpdatedUtc = clock.UtcNow.AddDays(-3) };
        await store.CreateJobAsync(job, default);
        var notStaging = Path.Combine(dir.Path, "not-staging");
        Directory.CreateDirectory(notStaging);
        var temp = Path.Combine(notStaging, "attempt-1.mp4");
        File.WriteAllText(temp, "not-in-staging-layout");
        var finalPath = Path.Combine(dir.Path, "out.mp4");
        File.WriteAllText(finalPath, "user-final-output");
        File.WriteAllText(finalPath + ".transflux-reserve", "");
        var attempt = TestData.NewAttempt(job.JobId, 1, temp, finalPath);
        await store.CreateAttemptAsync(attempt, default);

        Assert.True(OutputPathService.IsWorkDirectory(Path.Combine(dir.Path, OutputStagingLayout.WorkFolderName, "token")));
        Assert.False(OutputPathService.IsWorkDirectory(notStaging));

        await OrphanCleanupService.RunAsync(store, paths, clock, CancellationToken.None);

        Assert.True(Directory.Exists(notStaging), "a directory outside .transflux-work must never be deleted");
        Assert.True(File.Exists(temp));
        Assert.True(File.Exists(finalPath));
        Assert.False(File.Exists(finalPath + ".transflux-reserve"), "the stale reservation lock is still safe to release");
    }
}
