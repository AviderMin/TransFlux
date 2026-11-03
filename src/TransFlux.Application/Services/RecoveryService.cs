using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;

namespace TransFlux.Application.Services;

/// <summary>
/// Crash-recovery pass run once at startup before the dispatch loop starts.
/// 1) Reconciles every open commit journal (Prepared or Published) through the committer's
///    identity-preserving reconciler. A successful reconciliation records the output and closes
///    the journal; an unresolved or failed one leaves the journal open and moves the owning job
///    to NeedsAttention. A database record is never accepted as proof that a file exists.
/// 2) For jobs left in Committing with a recorded output but no open journal, verifies the final
///    file (existence, size, and journal hash when available) before marking the job Succeeded.
/// 3) Marks every remaining non-terminal active job Interrupted (docs/07: no automatic resume).
/// </summary>
public static class RecoveryService
{
    public static async Task RunAsync(IJobStore store, IOutputCommitter committer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(committer);

        var journals = await store.GetOpenJournalsAsync(ct).ConfigureAwait(false);
        foreach (var journal in journals)
        {
            ct.ThrowIfCancellationRequested();

            // Published and Prepared journals both go through the reconciler. The previous
            // short-circuit on an existing OutputRecord let a missing or replaced final file be
            // reported as Succeeded; a journal is only closed after the committer proves identity.
            var result = await ReconcileAsync(committer, journal, ct).ConfigureAwait(false);
            if (result.Success)
            {
                await EnsureOutputAsync(store, journal, ct).ConfigureAwait(false);
                await store.DeleteJournalAsync(journal.CommitToken, ct).ConfigureAwait(false);
            }
            else
            {
                var failure = result.Failure
                    ?? FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryFailed");
                await MoveToNeedsAttentionAsync(store, journal.JobId, failure, ct).ConfigureAwait(false);
                // Keep the journal open so the unresolved commit and its evidence can be inspected.
            }
        }

        // A crash can occur after durable publication but before the final job transition,
        // including after the committer has already removed its journal. Verify the file itself;
        // the OutputRecord only proves what the previous process intended.
        foreach (var job in await store.GetJobsAsync(new[] { JobState.Committing }, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var outputs = await store.GetOutputsForJobAsync(job.JobId, ct).ConfigureAwait(false);
            var output = outputs
                .Where(o => job.ActiveAttemptId is null || o.AttemptId == job.ActiveAttemptId)
                .OrderByDescending(o => o.CommittedUtc)
                .FirstOrDefault();
            if (output is null)
            {
                continue;
            }

            var verification = await VerifyOutputAsync(committer, output, ct).ConfigureAwait(false);
            if (!verification.Success)
            {
                var failure = verification.Failure
                    ?? FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryFailed");
                await MoveToNeedsAttentionAsync(store, job.JobId, failure, ct).ConfigureAwait(false);
                continue;
            }

            var attempt = await store.GetAttemptAsync(output.AttemptId, ct).ConfigureAwait(false);
            if (attempt is not null)
            {
                await store.UpdateAttemptAsync(attempt with
                {
                    State = AttemptState.Succeeded,
                    EndedUtc = output.CommittedUtc,
                }, ct).ConfigureAwait(false);
            }
            await store.UpdateJobAsync(job with
            {
                State = JobState.Succeeded,
                ActiveAttemptId = null,
                Revision = job.Revision + 1,
                UpdatedUtc = output.CommittedUtc,
            }, job.Revision, ct).ConfigureAwait(false);
        }

        // Any job still active was interrupted by the previous process exit.
        await store.MarkActiveJobsInterruptedAsync(ct).ConfigureAwait(false);
    }

    private static async Task<CommitResult> ReconcileAsync(
        IOutputCommitter committer, CommitJournalEntry journal, CancellationToken ct)
    {
        if (committer is ICommitReconciler reconciler)
        {
            try
            {
                return await reconciler.ReconcileAsync(journal, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CommitResult
                {
                    Success = false,
                    CommitToken = journal.CommitToken,
                    Failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryFailed", ex.Message),
                };
            }
        }

        // Fallback: a Prepared commit can still be completed through the standard committer.
        // A Published journal cannot be verified without the reconciler and must fail closed.
        if (journal.Phase != CommitPhase.Prepared)
        {
            return new CommitResult
            {
                Success = false,
                CommitToken = journal.CommitToken,
                Failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryUnverifiable",
                    "The output committer cannot verify a published commit journal."),
            };
        }

        try
        {
            return await committer.CommitAsync(new CommitRequest
            {
                JobId = journal.JobId,
                AttemptId = journal.AttemptId,
                TempPath = journal.TempPath,
                FinalPath = journal.FinalPath,
                ExpectedSize = journal.SizeBytes,
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CommitResult
            {
                Success = false,
                CommitToken = journal.CommitToken,
                Failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryFailed", ex.Message),
            };
        }
    }

    private static async Task<CommitResult> VerifyOutputAsync(
        IOutputCommitter committer, OutputRecord output, CancellationToken ct)
    {
        if (committer is IOutputRecordVerifier verifier)
        {
            try
            {
                return await verifier.VerifyOutputAsync(output, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CommitResult
                {
                    Success = false,
                    CommitToken = output.CommitToken,
                    Failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryFailed", ex.Message),
                };
            }
        }

        return new CommitResult
        {
            Success = false,
            CommitToken = output.CommitToken,
            Failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryUnverifiable",
                "The output committer cannot verify a recorded output."),
        };
    }

    private static async Task EnsureOutputAsync(IJobStore store, CommitJournalEntry journal, CancellationToken ct)
    {
        var outputs = await store.GetOutputsForJobAsync(journal.JobId, ct).ConfigureAwait(false);
        if (outputs.Any(o => o.AttemptId == journal.AttemptId))
        {
            return;
        }

        await store.SaveOutputAsync(new OutputRecord
        {
            JobId = journal.JobId,
            AttemptId = journal.AttemptId,
            FinalPath = journal.FinalPath,
            Size = journal.SizeBytes,
            VerificationSummary = "Recovered from commit journal.",
            CommittedUtc = DateTimeOffset.UtcNow,
            CommitToken = journal.CommitToken,
        }, ct).ConfigureAwait(false);
    }

    private static async Task MoveToNeedsAttentionAsync(
        IJobStore store, Guid jobId, FailureInfo failure, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var job = await store.GetJobAsync(jobId, ct).ConfigureAwait(false);
            if (job is null)
            {
                return;
            }
            if (job.State == JobState.NeedsAttention)
            {
                return;
            }
            if (JobStateMachine.IsTerminal(job.State))
            {
                return;
            }

            var target = JobStateMachine.CanTransition(job.State, JobState.NeedsAttention)
                ? JobState.NeedsAttention
                : JobState.Failed;
            if (!JobStateMachine.CanTransition(job.State, target))
            {
                return;
            }

            var updated = job with
            {
                State = target,
                Revision = job.Revision + 1,
                UpdatedUtc = DateTimeOffset.UtcNow,
                ActiveAttemptId = null,
            };

            try
            {
                await store.UpdateJobAsync(updated, job.Revision, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception)
            {
                var fresh = await store.GetJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                if (fresh is null || fresh.Revision != job.Revision || fresh.State != job.State)
                {
                    continue;
                }
                throw;
            }
        }
    }
}
