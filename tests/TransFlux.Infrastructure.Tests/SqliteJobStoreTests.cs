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
            Assert.Equal(new long[] { 1, 2, 3, 4 }, versions.ToArray());
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
    public async Task MarkActiveJobsInterrupted_CancelRequestedBecomesCanceled()
    {
        using var dir = new TempDir();
        var db = DbPath(dir);
        await using var store = new SqliteJobStore(db);
        await store.InitializeAsync(default);
        var canceled = TestData.NewJob(state: JobState.CancelRequested);
        var preflighting = TestData.NewJob(state: JobState.Preflighting);
        await store.CreateJobAsync(canceled, default);
        await store.CreateJobAsync(preflighting, default);

        await store.MarkActiveJobsInterruptedAsync(default);

        Assert.Equal(JobState.Canceled, (await store.GetJobAsync(canceled.JobId, default))!.State);
        Assert.Equal(JobState.Interrupted, (await store.GetJobAsync(preflighting.JobId, default))!.State);
    }

    [Fact]
    public async Task Initialize_RejectsNewerSchemaVersion()
    {
        using var dir = new TempDir();
        var db = DbPath(dir);
        await using (var store = new SqliteJobStore(db))
        {
            await store.InitializeAsync(default);
        }
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO schema_migrations(version, applied_utc, checksum) VALUES(99, '2026-01-01T00:00:00Z', 'unknown');";
            cmd.ExecuteNonQuery();
        }

        await using (var store = new SqliteJobStore(db))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(default));
            Assert.Contains("newer", ex.Message);
        }
    }

    [Fact]
    public async Task Initialize_RejectsAppliedMigrationChecksumDrift()
    {
        using var dir = new TempDir();
        var db = DbPath(dir);
        await using (var store = new SqliteJobStore(db))
        {
            await store.InitializeAsync(default);
        }
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE schema_migrations SET checksum='deadbeef' WHERE version=1;";
            cmd.ExecuteNonQuery();
        }

        await using (var store = new SqliteJobStore(db))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(default));
            Assert.Contains("checksum", ex.Message);
        }
    }

    [Fact]
    public async Task InitializeWithRecovery_MovesCorruptDatabaseAside_AndStartsFresh()
    {
        using var dir = new TempDir();
        var db = DbPath(dir);
        File.WriteAllText(db, "this is not a sqlite database");

        await using var store = new SqliteJobStore(db);
        var moved = await store.InitializeWithRecoveryAsync(default);

        Assert.NotNull(moved);
        Assert.True(File.Exists(moved));
        var job = TestData.NewJob();
        await store.CreateJobAsync(job, default);
        Assert.NotNull(await store.GetJobAsync(job.JobId, default));
    }

    [Fact]
    public async Task InitializeWithRecovery_DoesNotMoveLockedDatabase()
    {
        using var dir = new TempDir();
        var db = DbPath(dir);
        await File.WriteAllTextAsync(db, "locked but not proven corrupt");
        using var hold = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await using var store = new SqliteJobStore(db);
        await Assert.ThrowsAnyAsync<Exception>(() => store.InitializeWithRecoveryAsync(default));

        // A sharing/busy failure is not corruption: the live file must stay in place, not be
        // renamed to *.corrupt-* (which would split or lose the user's data).
        Assert.True(File.Exists(db));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.corrupt-*"));
    }

    [Fact]
    public async Task InitializeWithRecovery_RestoresBackupWhenAvailable()
    {
        using var dir = new TempDir();
        var db = DbPath(dir);
        var job = TestData.NewJob();
        await using (var store = new SqliteJobStore(db))
        {
            await store.InitializeAsync(default);
            await store.CreateJobAsync(job, default);
        }
        File.Copy(db, db + ".bak", overwrite: true);
        File.WriteAllText(db, "corrupt-database-bytes");

        await using (var store = new SqliteJobStore(db))
        {
            var moved = await store.InitializeWithRecoveryAsync(default);
            Assert.NotNull(moved);
            Assert.NotNull(await store.GetJobAsync(job.JobId, default));
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
        // Failed journals are deliberately returned: recovery must be able to close a row left
        // behind when the process died between marking a commit failure and deleting it.
        Assert.Equal(3, open.Count);
        Assert.Contains(open, j => j.CommitToken == "t-fail" && j.Phase == CommitPhase.Failed);

        var one = await store.GetJournalAsync("t-pub", default);
        Assert.NotNull(one);
        Assert.Equal(CommitPhase.Published, one!.Phase);

        await store.DeleteJournalAsync("t-prep", default);
        open = await store.GetOpenJournalsAsync(default);
        Assert.Equal(2, open.Count);
        Assert.DoesNotContain(open, j => j.CommitToken == "t-prep");
        Assert.Contains(open, j => j.CommitToken == "t-pub");
    }

    [Fact]
    public async Task MarkActiveJobsInterrupted_OnlyTouchesActive()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);

        var running = TestData.NewJob(state: JobState.Running, revision: 0, enqueueOrder: 1);
        var runningAttempt = TestData.NewAttempt(running.JobId) with { State = AttemptState.Running };
        running = running with { ActiveAttemptId = runningAttempt.AttemptId };
        var done = TestData.NewJob(state: JobState.Succeeded, revision: 3, enqueueOrder: 2);
        await store.CreateJobAsync(running, default);
        await store.CreateAttemptAsync(runningAttempt, default);
        await store.CreateJobAsync(done, default);

        var interrupted = await store.MarkActiveJobsInterruptedAsync(default);
        Assert.Single(interrupted);
        Assert.Equal(running.JobId, interrupted[0].JobId);
        Assert.Equal(JobState.Interrupted, interrupted[0].State);

        var reloadedRunning = await store.GetJobAsync(running.JobId, default);
        Assert.Equal(JobState.Interrupted, reloadedRunning!.State);
        Assert.Equal(1, reloadedRunning.Revision);
        Assert.Null(reloadedRunning.ActiveAttemptId);
        var reloadedAttempt = await store.GetAttemptAsync(runningAttempt.AttemptId, default);
        Assert.Equal(AttemptState.Interrupted, reloadedAttempt!.State);
        Assert.NotNull(reloadedAttempt.EndedUtc);

        var reloadedDone = await store.GetJobAsync(done.JobId, default);
        Assert.Equal(JobState.Succeeded, reloadedDone!.State);
        Assert.Equal(3, reloadedDone.Revision);
    }

    [Fact]
    public async Task MarkActiveJobsInterrupted_RecoversNeedsAttentionWithVerifiedOutputAndNoOpenJournal()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);

        var updated = new DateTimeOffset(2026, 1, 2, 4, 0, 0, TimeSpan.Zero);
        var final = dir.NewFile("recovered.mp4", new string('x', 42));
        var job = TestData.NewJob(state: JobState.NeedsAttention, revision: 4, enqueueOrder: 1) with
        {
            UpdatedUtc = updated,
        };
        var attempt = TestData.NewAttempt(job.JobId) with { State = AttemptState.Failed };
        job = job with { ActiveAttemptId = attempt.AttemptId };
        await store.CreateJobAsync(job, default);
        await store.CreateAttemptAsync(attempt, default);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = final,
            Size = 42,
            VerificationSummary = "recovered",
            CommittedUtc = updated.AddMinutes(1),
            CommitToken = "recover-token",
        }, default);

        await store.MarkActiveJobsInterruptedAsync(default);

        var recoveredJob = await store.GetJobAsync(job.JobId, default);
        Assert.Equal(JobState.Succeeded, recoveredJob!.State);
        Assert.Null(recoveredJob.ActiveAttemptId);
        var recoveredAttempt = await store.GetAttemptAsync(attempt.AttemptId, default);
        Assert.Equal(AttemptState.Succeeded, recoveredAttempt!.State);
        Assert.Equal(updated.AddMinutes(1), recoveredAttempt.EndedUtc);
    }

    [Fact]
    public async Task MarkActiveJobsInterrupted_NeedsAttentionWithMissingFinal_StaysNeedsAttention()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);

        var updated = new DateTimeOffset(2026, 1, 2, 4, 0, 0, TimeSpan.Zero);
        var job = TestData.NewJob(state: JobState.NeedsAttention, revision: 4, enqueueOrder: 1) with
        {
            UpdatedUtc = updated,
        };
        var attempt = TestData.NewAttempt(job.JobId) with { State = AttemptState.Failed };
        job = job with { ActiveAttemptId = attempt.AttemptId };
        await store.CreateJobAsync(job, default);
        await store.CreateAttemptAsync(attempt, default);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = Path.Combine(dir.Path, "missing.mp4"),
            Size = 42,
            VerificationSummary = "recorded but gone",
            CommittedUtc = updated.AddMinutes(1),
            CommitToken = "recover-token",
        }, default);

        await store.MarkActiveJobsInterruptedAsync(default);

        var recoveredJob = await store.GetJobAsync(job.JobId, default);
        Assert.Equal(JobState.NeedsAttention, recoveredJob!.State);
        var unchangedAttempt = await store.GetAttemptAsync(attempt.AttemptId, default);
        Assert.Equal(AttemptState.Failed, unchangedAttempt!.State);
        Assert.Single(await store.GetOutputsForJobAsync(job.JobId, default));
    }

    [Fact]
    public async Task MarkActiveJobsInterrupted_NeedsAttentionWithSizeMismatch_StaysNeedsAttention()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);

        var updated = new DateTimeOffset(2026, 1, 2, 4, 0, 0, TimeSpan.Zero);
        var final = dir.NewFile("wrong-size.mp4", new string('x', 10));
        var job = TestData.NewJob(state: JobState.NeedsAttention, revision: 4, enqueueOrder: 1) with
        {
            UpdatedUtc = updated,
        };
        var attempt = TestData.NewAttempt(job.JobId) with { State = AttemptState.Failed };
        job = job with { ActiveAttemptId = attempt.AttemptId };
        await store.CreateJobAsync(job, default);
        await store.CreateAttemptAsync(attempt, default);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = final,
            Size = 42,
            VerificationSummary = "size mismatch",
            CommittedUtc = updated.AddMinutes(1),
            CommitToken = "recover-token",
        }, default);

        await store.MarkActiveJobsInterruptedAsync(default);

        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(job.JobId, default))!.State);
    }

    [Fact]
    public async Task Attempt_RoundTrips_ExecutedCandidateId()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);
        var job = TestData.NewJob();
        await store.CreateJobAsync(job, default);

        var attempt = TestData.NewAttempt(job.JobId) with { ExecutedCandidateId = "cpu-x264" };
        await store.CreateAttemptAsync(attempt, default);

        var loaded = await store.GetAttemptAsync(attempt.AttemptId, default);
        Assert.Equal("cpu-x264", loaded!.ExecutedCandidateId);

        await store.UpdateAttemptAsync(loaded with { ExecutedCandidateId = "nvidia:h264_nvenc:gpu0" }, default);
        Assert.Equal("nvidia:h264_nvenc:gpu0",
            (await store.GetAttemptAsync(attempt.AttemptId, default))!.ExecutedCandidateId);
    }

    [Fact]
    public async Task Initialize_ReappliesMigration4_WhenExecutedCandidateColumnIsMissing()
    {
        using var dir = new TempDir();
        var db = DbPath(dir);
        await using (var store = new SqliteJobStore(db))
        {
            await store.InitializeAsync(default);
        }

        // Simulate a database created before migration 4: remove its column and migration row.
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE attempts DROP COLUMN executed_candidate_id; DELETE FROM schema_migrations WHERE version=4;";
            cmd.ExecuteNonQuery();
        }

        await using (var store = new SqliteJobStore(db))
        {
            await store.InitializeAsync(default);
        }

        using var check = new SqliteConnection($"Data Source={db}");
        check.Open();
        using (var cmd = check.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('attempts') WHERE name='executed_candidate_id';";
            Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
        using (var cmd = check.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=4;";
            Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
    }

    [Fact]
    public async Task MarkActiveJobsInterrupted_NeedsAttentionWithJournalHashMismatch_StaysNeedsAttention()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);

        var updated = new DateTimeOffset(2026, 1, 2, 4, 0, 0, TimeSpan.Zero);
        var final = dir.NewFile("hash-mismatch.mp4", new string('x', 42));
        var job = TestData.NewJob(state: JobState.NeedsAttention, revision: 4, enqueueOrder: 1) with
        {
            UpdatedUtc = updated,
        };
        var attempt = TestData.NewAttempt(job.JobId) with { State = AttemptState.Failed };
        job = job with { ActiveAttemptId = attempt.AttemptId };
        await store.CreateJobAsync(job, default);
        await store.CreateAttemptAsync(attempt, default);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = final,
            Size = 42,
            CommittedUtc = updated.AddMinutes(1),
            CommitToken = "hash-token",
        }, default);

        var wrongHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("different content")));
        var journal = new CommitJournalEntry
        {
            CommitToken = "hash-token",
            AttemptId = attempt.AttemptId,
            JobId = job.JobId,
            Phase = CommitPhase.Failed,
            TempPath = Path.Combine(dir.Path, "gone.tmp"),
            FinalPath = final,
            SizeBytes = 42,
            UpdatedUtc = updated,
        };
        await ((ICommitJournalContentStore)store)
            .UpsertJournalWithContentHashAsync(journal, wrongHash, default);

        await store.MarkActiveJobsInterruptedAsync(default);

        Assert.Equal(JobState.NeedsAttention, (await store.GetJobAsync(job.JobId, default))!.State);
    }

    [Fact]
    public async Task MarkActiveJobsInterrupted_NeedsAttentionWithMatchingJournalHash_RecoversSucceeded()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(DbPath(dir));
        await store.InitializeAsync(default);

        var updated = new DateTimeOffset(2026, 1, 2, 4, 0, 0, TimeSpan.Zero);
        var final = dir.NewFile("hash-match.mp4", new string('x', 42));
        var job = TestData.NewJob(state: JobState.NeedsAttention, revision: 4, enqueueOrder: 1) with
        {
            UpdatedUtc = updated,
        };
        var attempt = TestData.NewAttempt(job.JobId) with { State = AttemptState.Failed };
        job = job with { ActiveAttemptId = attempt.AttemptId };
        await store.CreateJobAsync(job, default);
        await store.CreateAttemptAsync(attempt, default);
        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = job.JobId,
            AttemptId = attempt.AttemptId,
            FinalPath = final,
            Size = 42,
            CommittedUtc = updated.AddMinutes(1),
            CommitToken = "hash-token-ok",
        }, default);

        var matchingHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(final)));
        var journal = new CommitJournalEntry
        {
            CommitToken = "hash-token-ok",
            AttemptId = attempt.AttemptId,
            JobId = job.JobId,
            Phase = CommitPhase.Failed,
            TempPath = Path.Combine(dir.Path, "gone.tmp"),
            FinalPath = final,
            SizeBytes = 42,
            UpdatedUtc = updated,
        };
        await ((ICommitJournalContentStore)store)
            .UpsertJournalWithContentHashAsync(journal, matchingHash, default);

        await store.MarkActiveJobsInterruptedAsync(default);

        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(job.JobId, default))!.State);
    }
}
