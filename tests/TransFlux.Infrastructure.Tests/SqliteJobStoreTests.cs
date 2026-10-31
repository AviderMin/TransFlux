using Microsoft.Data.Sqlite;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Presets;

namespace TransFlux.Infrastructure.Tests;

public sealed class SqliteJobStoreTests
{
    private static string DbPath(TempDir dir) => Path.Combine(dir.Path, "state.db");

    [Fact]
    public async Task Initialize_AppliesMigrations_And_EnablesWal()
    {
        using var dir = new TempDir();
        var db = DbPath(dir);
        await using (var store = new SqliteJobStore(db))
        {
            await store.InitializeAsync(default);
            var job = TestData.NewJob();
            await store.CreateJobAsync(job, default);
            Assert.NotNull(await store.GetJobAsync(job.JobId, default));
        }

        using var conn = new SqliteConnection($"Data Source={db}");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
            var versions = new List<long>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) versions.Add(reader.GetInt64(0));
            Assert.Equal(new long[] { 1, 2 }, versions.ToArray());
        }

        using (var modeCmd = conn.CreateCommand())
        {
            modeCmd.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", (modeCmd.ExecuteScalar() as string)?.ToLowerInvariant());
        }

        using (var tables = conn.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            var names = new List<string>();
            using var tr = tables.ExecuteReader();
            while (tr.Read()) names.Add(tr.GetString(0));
            foreach (var expected in new[]
                     {
                         "schema_migrations", "sources", "jobs", "attempts", "outputs",
                         "commit_journal", "presets", "capability_cache", "settings",
                     })
            {
                Assert.Contains(expected, names);
            }
        }
    }

    [Fact]
    public async Task Job_RoundTrips_And_PersistsAcrossReopen()
    {
        using var dir = new TempDir();
        var db = DbPath(dir);
        var job = TestData.NewJob(enqueueOrder: 5, state: JobState.Running, revision: 2, sourcePath: @"D:\media\电影 & clips\in.mp4");

        await using (var store = new SqliteJobStore(db))
        {
            await store.InitializeAsync(default);
            await store.CreateJobAsync(job, default);
        }

        await using (var store = new SqliteJobStore(db))
        {
            await store.InitializeAsync(default);
            var loaded = await store.GetJobAsync(job.JobId, default);
            Assert.NotNull(loaded);
            Assert.Equal(job.JobId, loaded!.JobId);
            Assert.Equal(job.RequestSnapshot.SourceSnapshotId, loaded.RequestSnapshot.SourceSnapshotId);
            Assert.Equal(job.EnqueueOrder, loaded.EnqueueOrder);
            Assert.Equal(JobState.Running, loaded.State);
            Assert.Equal(2, loaded.Revision);
            Assert.Equal(job.SourcePath, loaded.SourcePath);
            Assert.Equal(job.SourceFingerprint.SizeBytes, loaded.SourceFingerprint.SizeBytes);
            Assert.Equal(job.SourceFingerprint.ModifiedUtc, loaded.SourceFingerprint.ModifiedUtc);

            var next = await store.NextEnqueueOrderAsync(default);
            Assert.Equal(6, next);
        }
    }

    [Fact]
    public async Task Conditional_Update_Enforces_Revision()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);

        var job = TestData.NewJob(revision: 0);
        await store.CreateJobAsync(job, default);

        var next = job with { State = JobState.Preflighting, Revision = 1, UpdatedUtc = DateTimeOffset.UtcNow };
        await store.UpdateJobAsync(next, 0, default);

        var loaded = await store.GetJobAsync(job.JobId, default);
        Assert.Equal(JobState.Preflighting, loaded!.State);
        Assert.Equal(1, loaded.Revision);

        // Stale expected revision must fail (lost race).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateJobAsync(next with { State = JobState.Running, Revision = 2 }, 0, default));

        // A valid CAS against the current revision succeeds and the store bumps Revision.
        await store.UpdateJobAsync(next with { State = JobState.Running, Revision = 1 }, 1, default);
        var loaded2 = await store.GetJobAsync(job.JobId, default);
        Assert.Equal(JobState.Running, loaded2!.State);
        Assert.Equal(2, loaded2.Revision);
    }

    [Fact]
    public async Task Attempt_Output_Preset_Settings_Capability_RoundTrip()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);

        var job = TestData.NewJob();
        await store.CreateJobAsync(job, default);

        var attempt = TestData.NewAttempt(job.JobId);
        await store.CreateAttemptAsync(attempt, default);
        var loadedAttempt = await store.GetAttemptAsync(attempt.AttemptId, default);
        Assert.NotNull(loadedAttempt);
        Assert.Equal(attempt.TempPath, loadedAttempt!.TempPath);
        Assert.Equal(attempt.PlanSnapshot.PlanId, loadedAttempt.PlanSnapshot.PlanId);

        await store.UpdateAttemptAsync(attempt with { State = AttemptState.Succeeded, ExitCode = 0 }, default);
        var updatedAttempt = await store.GetAttemptAsync(attempt.AttemptId, default);
        Assert.Equal(AttemptState.Succeeded, updatedAttempt!.State);
        Assert.Equal(0, updatedAttempt.ExitCode);
        Assert.Single(await store.GetAttemptsForJobAsync(job.JobId, default));

        var record = new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = @"C:\out\x.mp4",
            Size = 99,
            VerificationSummary = "ok",
            CommittedUtc = DateTimeOffset.UtcNow,
            CommitToken = "tok-1",
        };
        await store.SaveOutputAsync(record, default);
        var outputs = await store.GetOutputsForJobAsync(job.JobId, default);
        Assert.Single(outputs);
        Assert.Equal(99, outputs[0].Size);
        Assert.Equal("tok-1", outputs[0].CommitToken);
        Assert.Single(await store.GetOutputsAsync(default));

        // Upsert by attempt id keeps a single row.
        await store.SaveOutputAsync(record with { Size = 100 }, default);
        outputs = await store.GetOutputsForJobAsync(job.JobId, default);
        Assert.Single(outputs);
        Assert.Equal(100, outputs[0].Size);

        var presetId = Guid.NewGuid();
        var preset = new Preset
        {
            PresetId = presetId,
            Name = "我的预设",
            Template = new PresetRequestTemplate(),
        };
        await store.UpsertPresetAsync(preset, default);
        Assert.Contains(await store.GetPresetsAsync(default), p => p.PresetId == presetId && p.Name == "我的预设");
        await store.DeletePresetAsync(presetId, default);
        Assert.DoesNotContain(await store.GetPresetsAsync(default), p => p.PresetId == presetId);

        await store.SaveSettingAsync("k", "{\"a\":1}", default);
        var settings = await store.GetSettingsAsync(default);
        Assert.Equal("{\"a\":1}", settings["k"]);
        await store.SaveSettingAsync("k", "2", default);
        settings = await store.GetSettingsAsync(default);
        Assert.Equal("2", settings["k"]);

        await store.SaveCapabilityAsync("ck", "{\"x\":1}", DateTimeOffset.UtcNow, default);
        var cap = await store.GetCapabilityAsync("ck", default);
        Assert.NotNull(cap);
        Assert.Equal("{\"x\":1}", cap!.Value.PayloadJson);
        Assert.Null(await store.GetCapabilityAsync("missing", default));
    }

    [Fact]
    public async Task Journal_OpenFilter_And_Delete()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);

        var job = TestData.NewJob();
        await store.CreateJobAsync(job, default);
        var attempt = TestData.NewAttempt(job.JobId);
        await store.CreateAttemptAsync(attempt, default);

        var prepared = new CommitJournalEntry
        {
            CommitToken = "t-prep",
            AttemptId = attempt.AttemptId,
            JobId = job.JobId,
            Phase = CommitPhase.Prepared,
            TempPath = "t",
            FinalPath = "f",
            SizeBytes = 1,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        await store.UpsertJournalAsync(prepared, default);
        await store.UpsertJournalAsync(prepared with { CommitToken = "t-pub", Phase = CommitPhase.Published }, default);
        await store.UpsertJournalAsync(prepared with { CommitToken = "t-fail", Phase = CommitPhase.Failed }, default);

        var open = await store.GetOpenJournalsAsync(default);
        Assert.Equal(2, open.Count);
        Assert.DoesNotContain(open, j => j.CommitToken == "t-fail");

        var one = await store.GetJournalAsync("t-pub", default);
        Assert.NotNull(one);
        Assert.Equal(CommitPhase.Published, one!.Phase);

        await store.DeleteJournalAsync("t-prep", default);
        open = await store.GetOpenJournalsAsync(default);
        Assert.Single(open);
        Assert.Equal("t-pub", open[0].CommitToken);
    }

    [Fact]
    public async Task MarkActiveJobsInterrupted_OnlyTouchesActive()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);

        var running = TestData.NewJob(state: JobState.Running, revision: 0, enqueueOrder: 1);
        var done = TestData.NewJob(state: JobState.Succeeded, revision: 3, enqueueOrder: 2);
        await store.CreateJobAsync(running, default);
        await store.CreateJobAsync(done, default);

        var interrupted = await store.MarkActiveJobsInterruptedAsync(default);
        Assert.Single(interrupted);
        Assert.Equal(running.JobId, interrupted[0].JobId);
        Assert.Equal(JobState.Interrupted, interrupted[0].State);

        var reloadedRunning = await store.GetJobAsync(running.JobId, default);
        Assert.Equal(JobState.Interrupted, reloadedRunning!.State);
        Assert.Equal(1, reloadedRunning.Revision);

        var reloadedDone = await store.GetJobAsync(done.JobId, default);
        Assert.Equal(JobState.Succeeded, reloadedDone!.State);
        Assert.Equal(3, reloadedDone.Revision);
    }
}
