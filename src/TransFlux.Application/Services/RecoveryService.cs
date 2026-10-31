using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;

namespace TransFlux.Application.Services;

/// <summary>
/// Crash-recovery pass run once at startup before the dispatch loop starts.
/// 1) Reconciles open commit journals: a journal whose output record already exists is closed;
///    a journal the committer can still complete is finished and its output recorded; anything
///    else is left open and the owning job is moved to NeedsAttention for manual review.
/// 2) Marks every remaining non-terminal active job Interrupted (docs/07: no automatic resume).
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

            var outputs = await store.GetOutputsForJobAsync(journal.JobId, ct).ConfigureAwait(false);
            if (outputs.Any(o => o.AttemptId == journal.AttemptId))
            {
                // The output was already recorded; the journal is stale and can be closed.
                await store.DeleteJournalAsync(journal.CommitToken, ct).ConfigureAwait(false);
                continue;
            }

            if (journal.Phase == CommitPhase.Published)
            {
                await EnsureOutputAsync(store, journal, ct).ConfigureAwait(false);
                await store.DeleteJournalAsync(journal.CommitToken, ct).ConfigureAwait(false);
                continue;
            }

            // Prepared: ask the committer to reconcile (identity check + complete or reject).
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
                // Keep the journal open so the unresolved commit can be inspected/reconciled later.
            }
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
            catch (Exception ex)
            {
                return new CommitResult
                {
                    Success = false,
                    Failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryFailed", ex.Message),
                };
            }
        }

        // Fallback: attempt to complete the commit through the standard committer.
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
        catch (Exception ex)
        {
            return new CommitResult
            {
                Success = false,
                Failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryFailed", ex.Message),
            };
        }
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
