using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;

namespace TransFlux.Infrastructure;

/// <summary>SQLite-backed job store (docs/07 schema). WAL, foreign keys, FULL synchronous durability,
/// single-writer serialization, conditional (revision-checked) job updates and migration backups.</summary>
public sealed class SqliteJobStore : IJobStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = CanonicalJson.Options;

    private const string JobColumns =
        "id, request_json, source_fingerprint_json, enqueue_order, state, revision, active_attempt_id, created_utc, updated_utc, retry_of, request_hash, source_path";

    private const string AttemptColumns =
        "id, job_id, ordinal, plan_json, core_hash, device_identity, temp_path, reserved_final_path, state, started_utc, ended_utc, exit_code, failure_json, fallback_reason, stderr_tail";

    private const string OutputColumns =
        "job_id, attempt_id, final_path, size, verification_json, committed_utc, commit_token";

    private const string JournalColumns =
        "commit_token, attempt_id, job_id, phase, temp_path, final_path, size_bytes, updated_utc";

    private static readonly int[] ActiveStates =
    {
        (int)JobState.Preflighting, (int)JobState.Running, (int)JobState.Verifying,
        (int)JobState.Committing, (int)JobState.Recovering, (int)JobState.CancelRequested,
    };

    private static readonly int[] TerminalStates =
    {
        (int)JobState.Succeeded, (int)JobState.Failed, (int)JobState.Canceled, (int)JobState.Interrupted,
    };

    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _initialized;
    private long _lastEnqueueOrder;

    public SqliteJobStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var existed = File.Exists(_databasePath) && new FileInfo(_databasePath).Length > 0;

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };
            var conn = new SqliteConnection(builder.ToString());
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await SetPragmaAsync(conn, "journal_mode", "WAL", ct).ConfigureAwait(false);
            await ExecAsync(conn, "PRAGMA foreign_keys=ON;", ct).ConfigureAwait(false);
            await ExecAsync(conn, "PRAGMA busy_timeout=5000;", ct).ConfigureAwait(false);
            await ExecAsync(conn, "PRAGMA synchronous=FULL;", ct).ConfigureAwait(false);

            await ExecAsync(conn,
                "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL, checksum TEXT NOT NULL);",
                ct).ConfigureAwait(false);

            var applied = new HashSet<int>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT version FROM schema_migrations;";
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) applied.Add(reader.GetInt32(0));
            }

            var pending = Migrations.Where(m => !applied.Contains(m.Version)).OrderBy(m => m.Version).ToList();
            if (pending.Count > 0 && existed)
                BackupDatabase(conn);

            foreach (var migration in pending)
            {
                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = migration.Sql;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO schema_migrations(version, applied_utc, checksum) VALUES($v, $u, $c);";
                    cmd.Parameters.AddWithValue("$v", migration.Version);
                    cmd.Parameters.AddWithValue("$u", Fmt(DateTimeOffset.UtcNow));
                    cmd.Parameters.AddWithValue("$c", migration.Checksum);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                tx.Commit();
            }

            _connection = conn;
            _lastEnqueueOrder = await ReadMaxOrderAsync(conn, ct).ConfigureAwait(false);
            _initialized = true;
        }
        catch
        {
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task SetPragmaAsync(SqliteConnection conn, string pragma, string value, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma}={value};";
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> ReadMaxOrderAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(enqueue_order), 0) FROM jobs;";
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private void BackupDatabase(SqliteConnection conn)
    {
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            File.Copy(_databasePath, _databasePath + ".bak", overwrite: true);
        }
        catch
        {
            // Backup is best-effort; migration still runs inside a transaction.
        }
    }

    private async Task<T> InGateAsync<T>(Func<SqliteConnection, Task<T>> body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = _connection ?? throw new InvalidOperationException("SqliteJobStore is not initialized; call InitializeAsync first.");
            return await body(conn).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InGateAsync(Func<SqliteConnection, Task> body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = _connection ?? throw new InvalidOperationException("SqliteJobStore is not initialized; call InitializeAsync first.");
            await body(conn).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Jobs ----

    public async Task<long> NextEnqueueOrderAsync(CancellationToken ct)
    {
        return await InGateAsync(async conn =>
        {
            var max = await ReadMaxOrderAsync(conn, ct).ConfigureAwait(false);
            if (max > _lastEnqueueOrder) _lastEnqueueOrder = max;
            return ++_lastEnqueueOrder;
        }, ct).ConfigureAwait(false);
    }

    public Task CreateJobAsync(Job job, CancellationToken ct)
    {
        ValidateJob(job);
        return InGateAsync(async conn =>
        {
            using var tx = conn.BeginTransaction();
            await InsertSourceAsync(conn, tx, job, ct).ConfigureAwait(false);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO jobs(" + JobColumns + ",source_id) VALUES($id,$req,$sfp,$order,$state,$rev,$aid,$created,$updated,$retry,$rh,$sp,$src);";
                cmd.Parameters.AddWithValue("$id", job.JobId.ToString());
                cmd.Parameters.AddWithValue("$req", CanonicalJson.Serialize(job.RequestSnapshot));
                cmd.Parameters.AddWithValue("$sfp", CanonicalJson.Serialize(job.SourceFingerprint));
                cmd.Parameters.AddWithValue("$order", job.EnqueueOrder);
                cmd.Parameters.AddWithValue("$state", (int)job.State);
                cmd.Parameters.AddWithValue("$rev", job.Revision);
                AddNullableGuid(cmd, "$aid", job.ActiveAttemptId);
                cmd.Parameters.AddWithValue("$created", Fmt(job.CreatedUtc));
                cmd.Parameters.AddWithValue("$updated", Fmt(job.UpdatedUtc));
                AddNullableGuid(cmd, "$retry", job.RetryOfJobId);
                cmd.Parameters.AddWithValue("$rh", job.RequestHash);
                cmd.Parameters.AddWithValue("$sp", (object?)job.SourcePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$src", job.RequestSnapshot.SourceSnapshotId.ToString());
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            tx.Commit();
        }, ct);
    }

    public Task UpdateJobAsync(Job job, long expectedRevision, CancellationToken ct)
    {
        ValidateJob(job);
        return InGateAsync(async conn =>
        {
            using var tx = conn.BeginTransaction();
            await InsertSourceAsync(conn, tx, job, ct).ConfigureAwait(false);
            var newRevision = Math.Max(job.Revision, expectedRevision + 1);
            var updated = job.UpdatedUtc == default ? DateTimeOffset.UtcNow : job.UpdatedUtc;
            int rows;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "UPDATE jobs SET request_json=$req, source_id=$src, state=$state, revision=$rev, enqueue_order=$order, " +
                    "retry_of=$retry, active_attempt_id=$aid, updated_utc=$updated, request_hash=$rh, source_path=$sp, source_fingerprint_json=$sfp " +
                    "WHERE id=$id AND revision=$expected;";
                cmd.Parameters.AddWithValue("$req", CanonicalJson.Serialize(job.RequestSnapshot));
                cmd.Parameters.AddWithValue("$src", job.RequestSnapshot.SourceSnapshotId.ToString());
                cmd.Parameters.AddWithValue("$state", (int)job.State);
                cmd.Parameters.AddWithValue("$rev", newRevision);
                cmd.Parameters.AddWithValue("$order", job.EnqueueOrder);
                AddNullableGuid(cmd, "$retry", job.RetryOfJobId);
                AddNullableGuid(cmd, "$aid", job.ActiveAttemptId);
                cmd.Parameters.AddWithValue("$updated", Fmt(updated));
                cmd.Parameters.AddWithValue("$rh", job.RequestHash);
                cmd.Parameters.AddWithValue("$sp", (object?)job.SourcePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sfp", CanonicalJson.Serialize(job.SourceFingerprint));
                cmd.Parameters.AddWithValue("$id", job.JobId.ToString());
                cmd.Parameters.AddWithValue("$expected", expectedRevision);
                rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            if (rows == 0)
            {
                tx.Rollback();
                throw new InvalidOperationException(
                    $"Job {job.JobId} revision mismatch: expected revision {expectedRevision} was not found (concurrent update?).");
            }
            tx.Commit();
        }, ct);
    }

    public Task<Job?> GetJobAsync(Guid id, CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT " + JobColumns + " FROM jobs WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadJob(reader) : null;
        }, ct);

    public Task<IReadOnlyList<Job>> GetJobsAsync(IReadOnlyCollection<JobState>? states, CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            if (states is null || states.Count == 0)
            {
                cmd.CommandText = "SELECT " + JobColumns + " FROM jobs ORDER BY enqueue_order;";
            }
            else
            {
                var names = new List<string>(states.Count);
                var i = 0;
                foreach (var state in states)
                {
                    var name = "$s" + i++;
                    names.Add(name);
                    cmd.Parameters.AddWithValue(name, (int)state);
                }
                cmd.CommandText = "SELECT " + JobColumns + " FROM jobs WHERE state IN (" + string.Join(",", names) + ") ORDER BY enqueue_order;";
            }
            var list = new List<Job>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadJob(reader));
            return (IReadOnlyList<Job>)list;
        }, ct);

    // ---- Attempts ----

    public Task CreateAttemptAsync(Attempt attempt, CancellationToken ct)
    {
        if (attempt is null) throw new ArgumentNullException(nameof(attempt));
        return InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO attempts(" + AttemptColumns + ") VALUES($id,$job,$ord,$plan,$core,$dev,$temp,$final,$state,$started,$ended,$exit,$fail,$fallback,$stderr);";
            cmd.Parameters.AddWithValue("$id", attempt.AttemptId.ToString());
            cmd.Parameters.AddWithValue("$job", attempt.JobId.ToString());
            cmd.Parameters.AddWithValue("$ord", attempt.Ordinal);
            cmd.Parameters.AddWithValue("$plan", CanonicalJson.Serialize(attempt.PlanSnapshot));
            cmd.Parameters.AddWithValue("$core", attempt.CoreHash);
            cmd.Parameters.AddWithValue("$dev", (object?)attempt.DeviceIdentity ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$temp", attempt.TempPath);
            cmd.Parameters.AddWithValue("$final", attempt.ReservedFinalPath);
            cmd.Parameters.AddWithValue("$state", (int)attempt.State);
            AddNullableDate(cmd, "$started", attempt.StartedUtc);
            AddNullableDate(cmd, "$ended", attempt.EndedUtc);
            cmd.Parameters.AddWithValue("$exit", (object?)attempt.ExitCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fail", attempt.Failure is null ? DBNull.Value : CanonicalJson.Serialize(attempt.Failure));
            cmd.Parameters.AddWithValue("$fallback", (object?)attempt.FallbackReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stderr", (object?)attempt.StderrTail ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    public Task UpdateAttemptAsync(Attempt attempt, CancellationToken ct)
    {
        if (attempt is null) throw new ArgumentNullException(nameof(attempt));
        return InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE attempts SET job_id=$job, ordinal=$ord, plan_json=$plan, core_hash=$core, device_identity=$dev, " +
                "temp_path=$temp, reserved_final_path=$final, state=$state, started_utc=$started, ended_utc=$ended, " +
                "exit_code=$exit, failure_json=$fail, fallback_reason=$fallback, stderr_tail=$stderr WHERE id=$id;";
            cmd.Parameters.AddWithValue("$job", attempt.JobId.ToString());
            cmd.Parameters.AddWithValue("$ord", attempt.Ordinal);
            cmd.Parameters.AddWithValue("$plan", CanonicalJson.Serialize(attempt.PlanSnapshot));
            cmd.Parameters.AddWithValue("$core", attempt.CoreHash);
            cmd.Parameters.AddWithValue("$dev", (object?)attempt.DeviceIdentity ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$temp", attempt.TempPath);
            cmd.Parameters.AddWithValue("$final", attempt.ReservedFinalPath);
            cmd.Parameters.AddWithValue("$state", (int)attempt.State);
            AddNullableDate(cmd, "$started", attempt.StartedUtc);
            AddNullableDate(cmd, "$ended", attempt.EndedUtc);
            cmd.Parameters.AddWithValue("$exit", (object?)attempt.ExitCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fail", attempt.Failure is null ? DBNull.Value : CanonicalJson.Serialize(attempt.Failure));
            cmd.Parameters.AddWithValue("$fallback", (object?)attempt.FallbackReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stderr", (object?)attempt.StderrTail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", attempt.AttemptId.ToString());
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
                throw new InvalidOperationException($"Attempt {attempt.AttemptId} does not exist.");
        }, ct);
    }

    public Task<Attempt?> GetAttemptAsync(Guid id, CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT " + AttemptColumns + " FROM attempts WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadAttempt(reader) : null;
        }, ct);

    public Task<IReadOnlyList<Attempt>> GetAttemptsForJobAsync(Guid jobId, CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT " + AttemptColumns + " FROM attempts WHERE job_id=$job ORDER BY ordinal;";
            cmd.Parameters.AddWithValue("$job", jobId.ToString());
            var list = new List<Attempt>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadAttempt(reader));
            return (IReadOnlyList<Attempt>)list;
        }, ct);

    // ---- Outputs ----

    public Task SaveOutputAsync(OutputRecord record, CancellationToken ct)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        return InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO outputs(" + OutputColumns + ") VALUES($job,$attempt,$path,$size,$verify,$committed,$token) " +
                "ON CONFLICT(attempt_id) DO UPDATE SET job_id=excluded.job_id, final_path=excluded.final_path, size=excluded.size, " +
                "verification_json=excluded.verification_json, committed_utc=excluded.committed_utc, commit_token=excluded.commit_token;";
            cmd.Parameters.AddWithValue("$job", record.JobId.ToString());
            cmd.Parameters.AddWithValue("$attempt", record.AttemptId.ToString());
            cmd.Parameters.AddWithValue("$path", record.FinalPath);
            cmd.Parameters.AddWithValue("$size", record.Size);
            cmd.Parameters.AddWithValue("$verify", record.VerificationSummary);
            cmd.Parameters.AddWithValue("$committed", Fmt(record.CommittedUtc));
            cmd.Parameters.AddWithValue("$token", record.CommitToken);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<OutputRecord>> GetOutputsAsync(CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT " + OutputColumns + " FROM outputs ORDER BY committed_utc;";
            var list = new List<OutputRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadOutput(reader));
            return (IReadOnlyList<OutputRecord>)list;
        }, ct);

    public Task<IReadOnlyList<OutputRecord>> GetOutputsForJobAsync(Guid jobId, CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT " + OutputColumns + " FROM outputs WHERE job_id=$job ORDER BY committed_utc;";
            cmd.Parameters.AddWithValue("$job", jobId.ToString());
            var list = new List<OutputRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadOutput(reader));
            return (IReadOnlyList<OutputRecord>)list;
        }, ct);

    // ---- History ----

    /// <summary>Reads terminal jobs as user-facing history rows (newest first). Internal so the
    /// public port surface stays on <see cref="IJobStore"/>; <see cref="HistoryService"/> adapts it.</summary>
    internal Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int limit, CancellationToken ct)
    {
        if (limit <= 0)
            return Task.FromResult<IReadOnlyList<HistoryEntry>>(Array.Empty<HistoryEntry>());

        return InGateAsync(async conn =>
        {
            var stateList = string.Join(",", TerminalStates);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT j.id, COALESCE(j.source_path, s.normalized_path), o.final_path, j.state, j.created_utc, j.updated_utc, " +
                "o.size, a.failure_json, j.retry_of FROM jobs j " +
                "LEFT JOIN sources s ON s.id = j.source_id " +
                "LEFT JOIN outputs o ON o.attempt_id = (" +
                "SELECT o2.attempt_id FROM outputs o2 WHERE o2.job_id = j.id ORDER BY o2.committed_utc DESC LIMIT 1) " +
                "LEFT JOIN attempts a ON a.id = (" +
                "SELECT a2.id FROM attempts a2 WHERE a2.job_id = j.id ORDER BY a2.ordinal DESC LIMIT 1) " +
                "WHERE j.state IN (" + stateList + ") ORDER BY j.updated_utc DESC, j.enqueue_order DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            var list = new List<HistoryEntry>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                FailureCode? failureCode = null;
                if (!reader.IsDBNull(7))
                {
                    var failure = JsonSerializer.Deserialize<FailureInfo>(reader.GetString(7), Json);
                    if (failure is not null) failureCode = failure.Code;
                }

                var updatedUtc = ParseDate(reader.GetString(5));
                list.Add(new HistoryEntry
                {
                    JobId = Guid.Parse(reader.GetString(0)),
                    SourcePath = reader.IsDBNull(1) ? null : reader.GetString(1),
                    OutputPath = reader.IsDBNull(2) ? null : reader.GetString(2),
                    State = (JobState)reader.GetInt32(3),
                    CreatedUtc = ParseDate(reader.GetString(4)),
                    CompletedUtc = updatedUtc,
                    OutputSizeBytes = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    FailureCode = failureCode,
                    RetryOfJobId = reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
                });
            }
            return (IReadOnlyList<HistoryEntry>)list;
        }, ct);
    }

    /// <summary>Deletes terminal jobs plus their attempts/outputs in one short transaction.
    /// Returns the number of job rows removed. Output files on disk are never touched.</summary>
    internal Task<int> ClearHistoryAsync(CancellationToken ct)
        => InGateAsync(async conn =>
        {
            var stateList = string.Join(",", TerminalStates);
            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM outputs WHERE job_id IN (SELECT id FROM jobs WHERE state IN (" + stateList + "));";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM attempts WHERE job_id IN (SELECT id FROM jobs WHERE state IN (" + stateList + "));";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            int deletedJobs;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM jobs WHERE state IN (" + stateList + ");";
                deletedJobs = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
            return deletedJobs;
        }, ct);

    // ---- Commit journal ----

    public Task UpsertJournalAsync(CommitJournalEntry entry, CancellationToken ct)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        return InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO commit_journal(" + JournalColumns + ") VALUES($token,$attempt,$job,$phase,$temp,$final,$size,$updated) " +
                "ON CONFLICT(commit_token) DO UPDATE SET attempt_id=excluded.attempt_id, job_id=excluded.job_id, phase=excluded.phase, " +
                "temp_path=excluded.temp_path, final_path=excluded.final_path, size_bytes=excluded.size_bytes, updated_utc=excluded.updated_utc;";
            cmd.Parameters.AddWithValue("$token", entry.CommitToken);
            cmd.Parameters.AddWithValue("$attempt", entry.AttemptId.ToString());
            cmd.Parameters.AddWithValue("$job", entry.JobId.ToString());
            cmd.Parameters.AddWithValue("$phase", (int)entry.Phase);
            cmd.Parameters.AddWithValue("$temp", entry.TempPath);
            cmd.Parameters.AddWithValue("$final", entry.FinalPath);
            cmd.Parameters.AddWithValue("$size", entry.SizeBytes);
            cmd.Parameters.AddWithValue("$updated", Fmt(entry.UpdatedUtc));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<CommitJournalEntry?> GetJournalAsync(string token, CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT " + JournalColumns + " FROM commit_journal WHERE commit_token=$token;";
            cmd.Parameters.AddWithValue("$token", token);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadJournal(reader) : null;
        }, ct);

    public Task<IReadOnlyList<CommitJournalEntry>> GetOpenJournalsAsync(CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT " + JournalColumns + " FROM commit_journal WHERE phase IN ($p0, $p1) ORDER BY updated_utc;";
            cmd.Parameters.AddWithValue("$p0", (int)CommitPhase.Prepared);
            cmd.Parameters.AddWithValue("$p1", (int)CommitPhase.Published);
            var list = new List<CommitJournalEntry>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadJournal(reader));
            return (IReadOnlyList<CommitJournalEntry>)list;
        }, ct);

    public Task DeleteJournalAsync(string token, CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM commit_journal WHERE commit_token=$token;";
            cmd.Parameters.AddWithValue("$token", token);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    // ---- Presets ----

    public Task<IReadOnlyList<Preset>> GetPresetsAsync(CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload_json FROM presets ORDER BY built_in DESC, id;";
            var list = new List<Preset>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var preset = JsonSerializer.Deserialize<Preset>(reader.GetString(0), Json);
                if (preset is not null) list.Add(preset);
            }
            return (IReadOnlyList<Preset>)list;
        }, ct);

    public Task UpsertPresetAsync(Preset preset, CancellationToken ct)
    {
        if (preset is null) throw new ArgumentNullException(nameof(preset));
        return InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO presets(id, revision, schema_version, payload_json, built_in) VALUES($id,$rev,$schema,$payload,$builtin) " +
                "ON CONFLICT(id) DO UPDATE SET revision=excluded.revision, schema_version=excluded.schema_version, " +
                "payload_json=excluded.payload_json, built_in=excluded.built_in;";
            cmd.Parameters.AddWithValue("$id", preset.PresetId.ToString());
            cmd.Parameters.AddWithValue("$rev", preset.Revision);
            cmd.Parameters.AddWithValue("$schema", preset.SchemaVersion);
            cmd.Parameters.AddWithValue("$payload", CanonicalJson.Serialize(preset));
            cmd.Parameters.AddWithValue("$builtin", preset.BuiltIn ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    public Task DeletePresetAsync(Guid presetId, CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM presets WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", presetId.ToString());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    // ---- Capability cache ----

    public Task SaveCapabilityAsync(string cacheKey, string payloadJson, DateTimeOffset testedUtc, CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO capability_cache(cache_key, payload_json, tested_utc) VALUES($key,$payload,$tested) " +
                "ON CONFLICT(cache_key) DO UPDATE SET payload_json=excluded.payload_json, tested_utc=excluded.tested_utc;";
            cmd.Parameters.AddWithValue("$key", cacheKey);
            cmd.Parameters.AddWithValue("$payload", payloadJson);
            cmd.Parameters.AddWithValue("$tested", Fmt(testedUtc));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task<(string PayloadJson, DateTimeOffset TestedUtc)?> GetCapabilityAsync(string cacheKey, CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload_json, tested_utc FROM capability_cache WHERE cache_key=$key;";
            cmd.Parameters.AddWithValue("$key", cacheKey);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return ((string, DateTimeOffset)?)null;
            return (reader.GetString(0), ParseDate(reader.GetString(1)));
        }, ct);

    // ---- Settings ----

    public Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value_json FROM settings;";
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) map[reader.GetString(0)] = reader.GetString(1);
            return (IReadOnlyDictionary<string, string>)map;
        }, ct);

    public Task SaveSettingAsync(string key, string valueJson, CancellationToken ct)
        => InGateAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO settings(key, schema_version, value_json) VALUES($key, 1, $value) " +
                "ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json;";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", valueJson);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    // ---- Recovery ----

    public Task<IReadOnlyList<Job>> MarkActiveJobsInterruptedAsync(CancellationToken ct)
        => InGateAsync(async conn =>
        {
            var stateList = string.Join(",", ActiveStates);
            var active = new List<Job>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT " + JobColumns + " FROM jobs WHERE state IN (" + stateList + ") ORDER BY enqueue_order;";
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) active.Add(ReadJob(reader));
            }
            if (active.Count == 0) return (IReadOnlyList<Job>)active;

            var now = DateTimeOffset.UtcNow;
            var updated = new List<Job>(active.Count);
            using var tx = conn.BeginTransaction();
            foreach (var job in active)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE jobs SET state=$state, revision=revision+1, updated_utc=$updated WHERE id=$id AND state=$oldState;";
                cmd.Parameters.AddWithValue("$state", (int)JobState.Interrupted);
                cmd.Parameters.AddWithValue("$updated", Fmt(now));
                cmd.Parameters.AddWithValue("$id", job.JobId.ToString());
                cmd.Parameters.AddWithValue("$oldState", (int)job.State);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                updated.Add(job with { State = JobState.Interrupted, Revision = job.Revision + 1, UpdatedUtc = now });
            }
            tx.Commit();
            return (IReadOnlyList<Job>)updated;
        }, ct);

    // ---- Helpers ----

    private static async Task InsertSourceAsync(SqliteConnection conn, SqliteTransaction tx, Job job, CancellationToken ct)
    {
        // Upsert the source fingerprint row (the frozen contract has no dedicated source APIs).
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT OR IGNORE INTO sources(id, fingerprint, probe_json, schema_version, normalized_path, size_bytes, modified_utc) " +
            "VALUES($id,$fp,NULL,1,$np,$sz,$mod);";
        cmd.Parameters.AddWithValue("$id", job.RequestSnapshot.SourceSnapshotId.ToString());
        cmd.Parameters.AddWithValue("$fp", CanonicalJson.Serialize(job.SourceFingerprint));
        cmd.Parameters.AddWithValue("$np", job.SourceFingerprint.NormalizedPath);
        cmd.Parameters.AddWithValue("$sz", job.SourceFingerprint.SizeBytes);
        cmd.Parameters.AddWithValue("$mod", Fmt(job.SourceFingerprint.ModifiedUtc));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void ValidateJob(Job job)
    {
        if (job is null) throw new ArgumentNullException(nameof(job));
        if (job.RequestSnapshot is null) throw new ArgumentException("Job.RequestSnapshot is required.", nameof(job));
        if (job.SourceFingerprint is null) throw new ArgumentException("Job.SourceFingerprint is required.", nameof(job));
    }

    private static Job ReadJob(SqliteDataReader r) => new()
    {
        JobId = Guid.Parse(r.GetString(0)),
        RequestSnapshot = JsonSerializer.Deserialize<ConversionRequest>(r.GetString(1), Json)!,
        SourceFingerprint = JsonSerializer.Deserialize<SourceFingerprint>(r.GetString(2), Json)!,
        EnqueueOrder = r.GetInt64(3),
        State = (JobState)r.GetInt32(4),
        Revision = r.GetInt64(5),
        ActiveAttemptId = r.IsDBNull(6) ? null : Guid.Parse(r.GetString(6)),
        CreatedUtc = ParseDate(r.GetString(7)),
        UpdatedUtc = ParseDate(r.GetString(8)),
        RetryOfJobId = r.IsDBNull(9) ? null : Guid.Parse(r.GetString(9)),
        RequestHash = r.GetString(10),
        SourcePath = r.IsDBNull(11) ? null : r.GetString(11),
    };

    private static Attempt ReadAttempt(SqliteDataReader r) => new()
    {
        AttemptId = Guid.Parse(r.GetString(0)),
        JobId = Guid.Parse(r.GetString(1)),
        Ordinal = r.GetInt32(2),
        PlanSnapshot = JsonSerializer.Deserialize<ValidatedPlan>(r.GetString(3), Json)!,
        CoreHash = r.GetString(4),
        DeviceIdentity = r.IsDBNull(5) ? null : r.GetString(5),
        TempPath = r.GetString(6),
        ReservedFinalPath = r.GetString(7),
        State = (AttemptState)r.GetInt32(8),
        StartedUtc = r.IsDBNull(9) ? null : ParseDate(r.GetString(9)),
        EndedUtc = r.IsDBNull(10) ? null : ParseDate(r.GetString(10)),
        ExitCode = r.IsDBNull(11) ? null : r.GetInt32(11),
        Failure = r.IsDBNull(12) ? null : JsonSerializer.Deserialize<FailureInfo>(r.GetString(12), Json),
        FallbackReason = r.IsDBNull(13) ? null : r.GetString(13),
        StderrTail = r.IsDBNull(14) ? null : r.GetString(14),
    };

    private static OutputRecord ReadOutput(SqliteDataReader r) => new()
    {
        JobId = Guid.Parse(r.GetString(0)),
        AttemptId = Guid.Parse(r.GetString(1)),
        FinalPath = r.GetString(2),
        Size = r.GetInt64(3),
        VerificationSummary = r.IsDBNull(4) ? string.Empty : r.GetString(4),
        CommittedUtc = ParseDate(r.GetString(5)),
        CommitToken = r.GetString(6),
    };

    private static CommitJournalEntry ReadJournal(SqliteDataReader r) => new()
    {
        CommitToken = r.GetString(0),
        AttemptId = Guid.Parse(r.GetString(1)),
        JobId = Guid.Parse(r.GetString(2)),
        Phase = (CommitPhase)r.GetInt32(3),
        TempPath = r.GetString(4),
        FinalPath = r.GetString(5),
        SizeBytes = r.GetInt64(6),
        UpdatedUtc = ParseDate(r.GetString(7)),
    };

    private static void AddNullableGuid(SqliteCommand cmd, string name, Guid? value)
        => cmd.Parameters.AddWithValue(name, value.HasValue ? value.Value.ToString() : DBNull.Value);

    private static void AddNullableDate(SqliteCommand cmd, string name, DateTimeOffset? value)
        => cmd.Parameters.AddWithValue(name, value.HasValue ? Fmt(value.Value) : DBNull.Value);

    private static string Fmt(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
            _initialized = false;
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }

    private sealed record Migration(int Version, string Sql)
    {
        public string Checksum => CanonicalJson.Sha256Hex(Sql);
    }

    private static readonly Migration[] Migrations =
    {
        new(1, @"
CREATE TABLE sources (
    id TEXT PRIMARY KEY,
    fingerprint TEXT NOT NULL,
    probe_json TEXT,
    schema_version INTEGER NOT NULL DEFAULT 1,
    normalized_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    modified_utc TEXT NOT NULL
);

CREATE TABLE jobs (
    id TEXT PRIMARY KEY,
    request_json TEXT NOT NULL,
    source_id TEXT NOT NULL,
    state INTEGER NOT NULL,
    revision INTEGER NOT NULL,
    enqueue_order INTEGER NOT NULL,
    retry_of TEXT,
    active_attempt_id TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    request_hash TEXT NOT NULL DEFAULT '',
    source_path TEXT,
    source_fingerprint_json TEXT NOT NULL,
    FOREIGN KEY(source_id) REFERENCES sources(id)
);

CREATE TABLE attempts (
    id TEXT PRIMARY KEY,
    job_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    plan_json TEXT NOT NULL,
    core_hash TEXT NOT NULL DEFAULT '',
    device_identity TEXT,
    temp_path TEXT NOT NULL,
    reserved_final_path TEXT NOT NULL,
    state INTEGER NOT NULL,
    started_utc TEXT,
    ended_utc TEXT,
    exit_code INTEGER,
    failure_json TEXT,
    fallback_reason TEXT,
    stderr_tail TEXT,
    FOREIGN KEY(job_id) REFERENCES jobs(id),
    UNIQUE(job_id, ordinal)
);

CREATE TABLE outputs (
    attempt_id TEXT PRIMARY KEY,
    job_id TEXT NOT NULL,
    final_path TEXT NOT NULL,
    size INTEGER NOT NULL,
    verification_json TEXT NOT NULL DEFAULT '',
    committed_utc TEXT NOT NULL,
    commit_token TEXT NOT NULL
);

CREATE TABLE commit_journal (
    commit_token TEXT PRIMARY KEY,
    attempt_id TEXT NOT NULL,
    job_id TEXT NOT NULL,
    phase INTEGER NOT NULL,
    temp_path TEXT NOT NULL,
    final_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL DEFAULT 0,
    updated_utc TEXT NOT NULL
);

CREATE TABLE presets (
    id TEXT PRIMARY KEY,
    revision INTEGER NOT NULL,
    schema_version INTEGER NOT NULL DEFAULT 1,
    payload_json TEXT NOT NULL,
    built_in INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE capability_cache (
    cache_key TEXT PRIMARY KEY,
    payload_json TEXT NOT NULL,
    tested_utc TEXT NOT NULL
);

CREATE TABLE settings (
    key TEXT PRIMARY KEY,
    schema_version INTEGER NOT NULL DEFAULT 1,
    value_json TEXT NOT NULL
);
"),
        new(2, @"
CREATE INDEX ix_jobs_state_enqueue ON jobs(state, enqueue_order);
CREATE INDEX ix_jobs_source ON jobs(source_id);
CREATE INDEX ix_attempts_job ON attempts(job_id);
CREATE INDEX ix_outputs_job ON outputs(job_id);
CREATE INDEX ix_journal_phase ON commit_journal(phase);
"),
    };
}
