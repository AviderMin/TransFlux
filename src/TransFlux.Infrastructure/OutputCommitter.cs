using System.Security.Cryptography;
using TransFlux.Application.Contracts;
using TransFlux.Application.Services;
using TransFlux.Domain.Jobs;

namespace TransFlux.Infrastructure;

/// <summary>Infrastructure-private persistence for content identity without expanding the
/// Application commit-journal contract. Null hashes represent legacy journal rows.</summary>
internal interface ICommitJournalContentStore
{
    Task UpsertJournalWithContentHashAsync(CommitJournalEntry entry, string contentSha256, CancellationToken ct);
    Task<string?> GetJournalContentHashAsync(string token, CancellationToken ct);
}

/// <summary>Atomic, journal-backed output publication. Writes a Prepared intent, performs a
/// no-replace move, records Published, persists the OutputRecord, then closes the journal.
/// Implements <see cref="ICommitReconciler"/> so RecoveryService can reconcile open journals.</summary>
public sealed class OutputCommitter : IOutputCommitter, ICommitReconciler, IOutputRecordVerifier
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
        string? contentSha256 = null;
        try
        {
            if (!_fs.FileExists(request.TempPath))
                return Fail(FailureCode.CommitFailed, "COMMIT_TEMP_MISSING",
                    $"Temporary output '{request.TempPath}' does not exist.", token);
            actualSize = _fs.GetFileLength(request.TempPath);
            if (_store is ICommitJournalContentStore)
                contentSha256 = await ComputeContentHashAsync(request.TempPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
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
        // The hash and Prepared intent are persisted together, so a crash cannot leave a new
        // final-only journal with size as its only identity.
        await UpsertJournalWithIdentityAsync(prepared, contentSha256, ct).ConfigureAwait(false);

        try
        {
            _fs.MoveNoReplace(request.TempPath, request.FinalPath);
        }
        catch (IOException ex)
        {
            // Keep the Failed row until startup recovery closes it. Deleting it here would open a
            // crash window where the job is still Committing with no journal: recovery could not
            // tell that the commit failed, and preserved staging would be orphaned without a
            // NeedsAttention transition. GetOpenJournals includes Failed rows for exactly this.
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

        await SaveOutputAsync(published, request.VerificationSummary, ct);

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
                        string recoveredHash;
                        try
                        {
                            // This also gives legacy journals a reliable identity when recovery
                            // itself performs the no-replace move from the original temporary file.
                            recoveredHash = await ComputeContentHashAsync(entry.TempPath, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            return Fail(FailureCode.VerifyFailed, "COMMIT_RECONCILE_HASH", ex.Message, entry.CommitToken);
                        }

                        // Prove the staged content identity before it is exposed under the final
                        // name. A mismatched temp must stay where it is, not be published.
                        var storedHash = _store is ICommitJournalContentStore contentStore
                            ? await contentStore.GetJournalContentHashAsync(entry.CommitToken, ct).ConfigureAwait(false)
                            : null;
                        if (!string.IsNullOrEmpty(storedHash)
                            && !string.Equals(storedHash, recoveredHash, StringComparison.OrdinalIgnoreCase))
                        {
                            return Fail(FailureCode.OutputConflict, "OUTPUT_CONFLICT",
                                "Temporary output content does not match the Prepared journal identity.", entry.CommitToken);
                        }

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
                        return await PublishExistingAsync(entry, ct, recoveredHash).ConfigureAwait(false);
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(FailureCode.CommitFailed, "COMMIT_RECONCILE_FAILED", ex.Message, entry.CommitToken);
        }
    }

    /// <summary>Verifies a recorded output without modifying it. Existence and size are mandatory;
    /// when the journal row still carries a content hash the final content must match it too.</summary>
    public async Task<CommitResult> VerifyOutputAsync(OutputRecord output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            if (!_fs.FileExists(output.FinalPath))
                return Fail(FailureCode.VerifyFailed, "COMMIT_OUTPUT_MISSING",
                    $"Recorded output '{output.FinalPath}' does not exist.", output.CommitToken);

            long finalSize;
            try
            {
                finalSize = _fs.GetFileLength(output.FinalPath);
            }
            catch (Exception ex)
            {
                return Fail(FailureCode.VerifyFailed, "COMMIT_FINAL_UNREADABLE", ex.Message, output.CommitToken);
            }

            if (output.Size > 0 && finalSize != output.Size)
                return Fail(FailureCode.VerifyFailed, "COMMIT_OUTPUT_SIZE_MISMATCH",
                    $"Recorded output size {output.Size} does not match file size {finalSize}.", output.CommitToken);

            var storedHash = _store is ICommitJournalContentStore contentStore
                ? await contentStore.GetJournalContentHashAsync(output.CommitToken, ct).ConfigureAwait(false)
                : null;
            if (!string.IsNullOrEmpty(storedHash))
            {
                var finalHash = await ComputeContentHashAsync(output.FinalPath, ct).ConfigureAwait(false);
                if (!string.Equals(storedHash, finalHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(FailureCode.OutputConflict, "OUTPUT_CONFLICT",
                        "Recorded output content does not match the commit journal identity.", output.CommitToken);
                }
            }

            return new CommitResult { Success = true, CommitToken = output.CommitToken, FinalPath = output.FinalPath };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(FailureCode.CommitFailed, "COMMIT_OUTPUT_VERIFY_FAILED", ex.Message, output.CommitToken);
        }
    }

    private async Task<CommitResult> PublishExistingAsync(
        CommitJournalEntry entry, CancellationToken ct, string? recoveredHash = null)
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

        var storedHash = _store is ICommitJournalContentStore contentStore
            ? await contentStore.GetJournalContentHashAsync(entry.CommitToken, ct).ConfigureAwait(false)
            : null;
        if (!string.IsNullOrEmpty(storedHash) && !string.IsNullOrEmpty(recoveredHash)
            && !string.Equals(storedHash, recoveredHash, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(FailureCode.OutputConflict, "OUTPUT_CONFLICT",
                "Temporary output content does not match the Prepared journal identity.", entry.CommitToken);
        }

        var expectedHash = storedHash ?? recoveredHash;
        if (string.IsNullOrEmpty(expectedHash) && _fs.FileExists(entry.TempPath))
        {
            // A legacy final+temp pair can still be proven by comparing their contents. A legacy
            // final-only row cannot: equal length alone must never claim an unrelated file.
            try
            {
                expectedHash = await ComputeContentHashAsync(entry.TempPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail(FailureCode.VerifyFailed, "COMMIT_RECONCILE_HASH", ex.Message, entry.CommitToken);
            }
        }
        if (string.IsNullOrEmpty(expectedHash))
        {
            return Fail(FailureCode.OutputConflict, "OUTPUT_CONFLICT",
                "The legacy journal has no content identity and no temporary file to verify the final output.",
                entry.CommitToken);
        }

        string finalHash;
        try
        {
            finalHash = await ComputeContentHashAsync(entry.FinalPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(FailureCode.VerifyFailed, "COMMIT_FINAL_UNREADABLE", ex.Message, entry.CommitToken);
        }
        if (!string.Equals(expectedHash, finalHash, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(FailureCode.OutputConflict, "OUTPUT_CONFLICT",
                "Final output content does not match the commit journal identity.", entry.CommitToken);
        }

        var published = entry with { Phase = CommitPhase.Published, SizeBytes = finalSize, UpdatedUtc = _clock.UtcNow };
        await UpsertJournalWithIdentityAsync(published, expectedHash, ct).ConfigureAwait(false);
        await SaveOutputAsync(published, "Recovered from commit journal.", ct).ConfigureAwait(false);
        await _store.DeleteJournalAsync(entry.CommitToken, ct).ConfigureAwait(false);
        TryDeleteFile(OutputReservation.LockPath(entry.FinalPath));
        return new CommitResult { Success = true, CommitToken = entry.CommitToken, FinalPath = entry.FinalPath };
    }

    private Task UpsertJournalWithIdentityAsync(
        CommitJournalEntry entry, string? contentSha256, CancellationToken ct)
        => !string.IsNullOrEmpty(contentSha256) && _store is ICommitJournalContentStore contentStore
            ? contentStore.UpsertJournalWithContentHashAsync(entry, contentSha256, ct)
            : _store.UpsertJournalAsync(entry, ct);

    private static async Task<string> ComputeContentHashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private Task SaveOutputAsync(CommitJournalEntry entry, string verificationSummary, CancellationToken ct)
        => _store.SaveOutputAsync(new OutputRecord
        {
            JobId = entry.JobId,
            AttemptId = entry.AttemptId,
            FinalPath = entry.FinalPath,
            Size = entry.SizeBytes,
            VerificationSummary = verificationSummary,
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
