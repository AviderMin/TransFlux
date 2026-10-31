using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Presets;

namespace TransFlux.Infrastructure.Tests;

public sealed class HistoryServiceTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static string DbPath(TempDir dir) => Path.Combine(dir.Path, "state.db");

    [Fact]
    public async Task GetRecentAsync_ReturnsTerminalJobs_NewestFirst_AndMapsFields()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);
        var history = new HistoryService(store);

        var older = TestData.NewJob(state: JobState.Succeeded) with
        {
            UpdatedUtc = Created.AddMinutes(1),
            SourcePath = """C:\media\older.mp4""",
        };
        var failed = TestData.NewJob(state: JobState.Failed) with
        {
            UpdatedUtc = Created.AddMinutes(2),
            SourcePath = null, // falls back to sources.normalized_path
            RetryOfJobId = older.JobId,
        };
        var canceled = TestData.NewJob(state: JobState.Canceled) with { UpdatedUtc = Created.AddMinutes(3) };
        var interrupted = TestData.NewJob(state: JobState.Interrupted) with { UpdatedUtc = Created.AddMinutes(4) };
        var active = TestData.NewJob(state: JobState.Running) with { UpdatedUtc = Created.AddMinutes(5) };

        foreach (var job in new[] { older, failed, canceled, interrupted, active })
            await store.CreateJobAsync(job, default);

        var olderAttempt = TestData.NewAttempt(older.JobId);
        await store.CreateAttemptAsync(olderAttempt, default);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = older.JobId,
            AttemptId = olderAttempt.AttemptId,
            FinalPath = """C:\out\older.mp4""",
            Size = 4321,
            CommittedUtc = Created.AddSeconds(30),
            CommitToken = "tok-older",
        }, default);

        // The failed job has two attempts; the latest ordinal wins for the failure code.
        await store.CreateAttemptAsync(TestData.NewAttempt(failed.JobId, ordinal: 1) with
        {
            Failure = FailureInfo.Of(FailureCode.ProcessFailed, "ERR_PROCESS"),
        }, default);
        await store.CreateAttemptAsync(TestData.NewAttempt(failed.JobId, ordinal: 2) with
        {
            Failure = FailureInfo.Of(FailureCode.VerifyFailed, "ERR_VERIFY"),
        }, default);

        var page = await history.GetRecentAsync(3, default);
        Assert.Equal(new[] { interrupted.JobId, canceled.JobId, failed.JobId }, page.Select(h => h.JobId).ToArray());
        Assert.Equal(2, (await history.GetRecentAsync(2, default)).Count);
        Assert.Empty(await history.GetRecentAsync(0, default));
        Assert.Empty(await history.GetRecentAsync(-1, default));

        var all = await history.GetRecentAsync(10, default);
        Assert.Equal(
            new[] { interrupted.JobId, canceled.JobId, failed.JobId, older.JobId },
            all.Select(h => h.JobId).ToArray());
        Assert.DoesNotContain(all, h => h.JobId == active.JobId);

        var failedEntry = all.Single(h => h.JobId == failed.JobId);
        Assert.Equal(JobState.Failed, failedEntry.State);
        Assert.Equal("""C:\media\input.mp4""", failedEntry.SourcePath); // jobs.source_path is NULL -> sources.normalized_path
        Assert.Null(failedEntry.OutputPath);
        Assert.Null(failedEntry.OutputSizeBytes);
        Assert.Equal(FailureCode.VerifyFailed, failedEntry.FailureCode);
        Assert.Equal(older.JobId, failedEntry.RetryOfJobId);
        Assert.Equal(Created, failedEntry.CreatedUtc);
        Assert.Equal(Created.AddMinutes(2), failedEntry.CompletedUtc);

        var olderEntry = all.Single(h => h.JobId == older.JobId);
        Assert.Equal(JobState.Succeeded, olderEntry.State);
        Assert.Equal("""C:\media\older.mp4""", olderEntry.SourcePath);
        Assert.Equal("""C:\out\older.mp4""", olderEntry.OutputPath);
        Assert.Equal(4321, olderEntry.OutputSizeBytes);
        Assert.Null(olderEntry.FailureCode);
        Assert.Null(olderEntry.RetryOfJobId);
        Assert.Equal(Created, olderEntry.CreatedUtc);
        Assert.Equal(Created.AddMinutes(1), olderEntry.CompletedUtc);
    }

    [Fact]
    public async Task ClearAsync_RemovesTerminalRecords_ButKeepsActiveJobsAndUnrelatedData()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);
        var history = new HistoryService(store);

        var succeeded = TestData.NewJob(state: JobState.Succeeded) with { UpdatedUtc = Created.AddMinutes(1) };
        var failed = TestData.NewJob(state: JobState.Failed) with { UpdatedUtc = Created.AddMinutes(2) };
        var active = TestData.NewJob(state: JobState.Running) with { UpdatedUtc = Created.AddMinutes(3) };
        await store.CreateJobAsync(succeeded, default);
        await store.CreateJobAsync(failed, default);
        await store.CreateJobAsync(active, default);

        var succeededAttempt = TestData.NewAttempt(succeeded.JobId);
        var failedAttempt = TestData.NewAttempt(failed.JobId);
        var activeAttempt = TestData.NewAttempt(active.JobId);
        await store.CreateAttemptAsync(succeededAttempt, default);
        await store.CreateAttemptAsync(failedAttempt, default);
        await store.CreateAttemptAsync(activeAttempt, default);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = succeeded.JobId,
            AttemptId = succeededAttempt.AttemptId,
            FinalPath = """C:\out\done.mp4""",
            Size = 10,
            CommittedUtc = Created.AddSeconds(1),
            CommitToken = "tok-succeeded",
        }, default);

        var preset = new Preset
        {
            PresetId = Guid.NewGuid(),
            Name = "保留的预设",
            Template = new PresetRequestTemplate(),
        };
        await store.UpsertPresetAsync(preset, default);
        await store.SaveSettingAsync("theme", """{"theme":"Dark"}""", default);
        await store.SaveCapabilityAsync("cap-key", """{"ok":true}""", Created, default);

        await history.ClearAsync(default);

        Assert.Empty(await history.GetRecentAsync(10, default));
        Assert.Null(await store.GetJobAsync(succeeded.JobId, default));
        Assert.Null(await store.GetJobAsync(failed.JobId, default));
        Assert.NotNull(await store.GetJobAsync(active.JobId, default));

        var remainingJobs = await store.GetJobsAsync(null, default);
        Assert.Single(remainingJobs);
        Assert.Equal(active.JobId, remainingJobs[0].JobId);

        Assert.Empty(await store.GetAttemptsForJobAsync(succeeded.JobId, default));
        Assert.Empty(await store.GetAttemptsForJobAsync(failed.JobId, default));
        Assert.Single(await store.GetAttemptsForJobAsync(active.JobId, default));
        Assert.Empty(await store.GetOutputsForJobAsync(succeeded.JobId, default));
        Assert.Empty(await store.GetOutputsAsync(default));

        Assert.Contains(await store.GetPresetsAsync(default), p => p.PresetId == preset.PresetId);
        Assert.Equal("""{"theme":"Dark"}""", (await store.GetSettingsAsync(default))["theme"]);
        Assert.NotNull(await store.GetCapabilityAsync("cap-key", default));

        // Clearing twice is a no-op.
        await history.ClearAsync(default);
    }

    [Fact]
    public async Task ClearAsync_NeverDeletesOutputFiles()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);
        var history = new HistoryService(store);

        var job = TestData.NewJob(state: JobState.Succeeded) with { UpdatedUtc = Created.AddMinutes(1) };
        await store.CreateJobAsync(job, default);
        var attempt = TestData.NewAttempt(job.JobId);
        await store.CreateAttemptAsync(attempt, default);

        var outputPath = dir.NewFile("converted.mp4", "output-bytes");
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = outputPath,
            Size = new FileInfo(outputPath).Length,
            CommittedUtc = Created.AddSeconds(1),
            CommitToken = "tok-file",
        }, default);

        await history.ClearAsync(default);

        Assert.True(File.Exists(outputPath));
        Assert.Equal("output-bytes", File.ReadAllText(outputPath));
        Assert.Empty(await history.GetRecentAsync(10, default));
    }
}
