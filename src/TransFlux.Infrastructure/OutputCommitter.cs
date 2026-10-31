using TransFlux.Application.Contracts;
using TransFlux.Application.Services;
using TransFlux.Domain.Jobs;

namespace TransFlux.Infrastructure;

/// <summary>Atomic, journal-backed output publication. Writes a Prepared intent, performs a
/// no-replace move, records Published, persists the OutputRecord, then closes the journal.
/// Implements <see cref="ICommitReconciler"/> so RecoveryService can reconcile open journals.</summary>
public sealed class OutputCommitter : IOutputCommitter, ICommitReconciler
{
    private readonly IJobStore _store;
    private readonly IFileSystemService _fs;
    private readonly IClock _clock;

    public OutputCommitter(IJobStore store, IFileSystemService fs, IClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        long actualSize;
        try
        {
            if (!_fs.FileExists(request.TempPath))
                return Fail(FailureCode.CommitFailed, "COMMIT_TEMP_MISSING",
                    $"Temporary output '{request.TempPath}' does not exist.", token);
            actualSize = _fs.GetFileLength(request.TempPath);
        }
        catch (Exception ex)
        {
            return Fail(FailureCode.CommitFailed, "COMMIT_TEMP_UNREADABLE", ex.Message, token);
        }

        if (actualSize <= 0)
            return Fail(FailureCode.CommitFailed, "COMMIT_TEMP_EMPTY", "Temporary output is empty.", token);
        if (request.ExpectedSize > 0 && actualSize != request.ExpectedSize)
            return Fail(FailureCode.CommitFailed, "COMMIT_SIZE_MISMATCH",
                $"Expected {request.ExpectedSize} bytes, found {actualSize}.", token);

        var prepared = new CommitJournalEntry
        {
            CommitToken = token,
            AttemptId = request.AttemptId,
            JobId = request.JobId,
            Phase = CommitPhase.Prepared,
            TempPath = request.TempPath,
            FinalPath = request.FinalPath,
            SizeBytes = actualSize,
            UpdatedUtc = _clock.UtcNow,
        };
        await _store.UpsertJournalAsync(prepared, ct);

        try
        {
            _fs.MoveNoReplace(request.TempPath, request.FinalPath);
        }
        catch (IOException ex)
        {
            await SafeUpsertAsync(prepared with { Phase = CommitPhase.Failed, UpdatedUtc = _clock.UtcNow }, ct);
            return Fail(FailureCode.OutputConflict, "OUTPUT_CONFLICT", ex.Message, token);
        }

        var published = prepared with { Phase = CommitPhase.Published, UpdatedUtc = _clock.UtcNow };
        await SafeUpsertAsync(published, ct);

        long finalSize;
        try
        {
            finalSize = _fs.GetFileLength(request.FinalPath);
        }
        catch (Exception ex)
        {
            return Fail(FailureCode.VerifyFailed, "COMMIT_FINAL_UNREADABLE", ex.Message, token);
        }

        if (finalSize != actualSize)
            return Fail(FailureCode.VerifyFailed, "COMMIT_FINAL_SIZE_MISMATCH",
                $"Published file size {finalSize} does not match committed {actualSize}.", token);

        await SaveOutputAsync(published, ct);

        try
        {
            await _store.DeleteJournalAsync(token, ct);
        }
        catch
        {
            // The journal remains Published; startup reconciliation is idempotent.
        }

        TryDeleteFile(OutputReservation.LockPath(request.FinalPath));
        return new CommitResult { Success = true, CommitToken = token, FinalPath = request.FinalPath };
    }

    /// <summary>Reconciles a single open journal entry after a crash. Idempotent.
    /// Prepared+final -> publish; Prepared+temp -> move then publish; Published+final -> record;
    /// unresolved -> Failure and the journal is kept for NeedsAttention.</summary>
    public async Task<CommitResult> ReconcileAsync(CommitJournalEntry entry, CancellationToken ct)
    {
        try
        {
            switch (entry.Phase)
            {
                case CommitPhase.Prepared:
                {
                    if (_fs.FileExists(entry.FinalPath))
                        return await PublishExistingAsync(entry, ct).ConfigureAwait(false);

                    if (_fs.FileExists(entry.TempPath))
                    {
                        try
                        {
                            _fs.MoveNoReplace(entry.TempPath, entry.FinalPath);
                        }
                        catch (IOException ex)
                        {
                            return Fail(FailureCode.CommitFailed, "COMMIT_RECONCILE_MOVE", ex.Message, entry.CommitToken);
                        }
                        if (!_fs.FileExists(entry.FinalPath))
                            return Fail(FailureCode.CommitFailed, "COMMIT_RECONCILE_MOVE",
                                "Move reported success but final file is missing.", entry.CommitToken);
                        return await PublishExistingAsync(entry, ct).ConfigureAwait(false);
                    }

                    // Neither temp nor final exists: unresolved, keep the journal for NeedsAttention.
                    return Fail(FailureCode.CommitFailed, "COMMIT_UNRESOLVED",
                        "Prepared journal has neither a temporary nor a final file.", entry.CommitToken);
                }
                case CommitPhase.Published:
                {
                    if (!_fs.FileExists(entry.FinalPath))
                        return Fail(FailureCode.VerifyFailed, "COMMIT_PUBLISHED_MISSING",
                            "Journal marked Published but the final file is missing.", entry.CommitToken);
                    return await PublishExistingAsync(entry, ct).ConfigureAwait(false);
                }
                case CommitPhase.Failed:
                default:
                    await _store.DeleteJournalAsync(entry.CommitToken, ct).ConfigureAwait(false);
                    return Fail(FailureCode.CommitFailed, "COMMIT_PREVIOUSLY_FAILED",
                        "Journal recorded a failed commit; no output was published.", entry.CommitToken);
            }
        }
        catch (Exception ex)
        {
            return Fail(FailureCode.CommitFailed, "COMMIT_RECONCILE_FAILED", ex.Message, entry.CommitToken);
        }
    }

    private async Task<CommitResult> PublishExistingAsync(CommitJournalEntry entry, CancellationToken ct)
    {
        long finalSize;
        try
        {
            finalSize = _fs.GetFileLength(entry.FinalPath);
        }
        catch (Exception ex)
        {
            return Fail(FailureCode.VerifyFailed, "COMMIT_FINAL_UNREADABLE", ex.Message, entry.CommitToken);
        }

        if (entry.SizeBytes > 0 && finalSize != entry.SizeBytes)
            return Fail(FailureCode.VerifyFailed, "COMMIT_RECONCILE_SIZE",
                $"Final file size {finalSize} does not match journal {entry.SizeBytes}.", entry.CommitToken);

        var published = entry with { Phase = CommitPhase.Published, SizeBytes = finalSize, UpdatedUtc = _clock.UtcNow };
        await _store.UpsertJournalAsync(published, ct).ConfigureAwait(false);
        await SaveOutputAsync(published, ct).ConfigureAwait(false);
        await _store.DeleteJournalAsync(entry.CommitToken, ct).ConfigureAwait(false);
        TryDeleteFile(OutputReservation.LockPath(entry.FinalPath));
        return new CommitResult { Success = true, CommitToken = entry.CommitToken, FinalPath = entry.FinalPath };
    }

    private Task SaveOutputAsync(CommitJournalEntry entry, CancellationToken ct)
        => _store.SaveOutputAsync(new OutputRecord
        {
            JobId = entry.JobId,
            AttemptId = entry.AttemptId,
            FinalPath = entry.FinalPath,
            Size = entry.SizeBytes,
            VerificationSummary = string.Empty,
            CommittedUtc = _clock.UtcNow,
            CommitToken = entry.CommitToken,
        }, ct);

    private async Task SafeUpsertAsync(CommitJournalEntry entry, CancellationToken ct)
    {
        try
        {
            await _store.UpsertJournalAsync(entry, ct).ConfigureAwait(false);
        }
        catch
        {
            // Recovery will inspect the disk state (Prepared + final present => published).
        }
    }

    private static CommitResult Fail(FailureCode code, string key, string detail, string token)
        => new()
        {
            Success = false,
            CommitToken = token,
            Failure = FailureInfo.Of(code, key, detail),
        };

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
