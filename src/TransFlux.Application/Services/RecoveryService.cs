using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;

namespace TransFlux.Application.Services;

/// <summary>
/// Crash-recovery pass run once at startup before the dispatch loop starts.
/// 1) Reconciles every open commit journal (Prepared or Published) through the committer's
///    identity-preserving reconciler. A successful reconciliation records the output, completes
///    the owning attempt/job, and closes the journal; an unresolved or failed one leaves the
///    journal open and moves the owning job to NeedsAttention. A database record is never
///    accepted as proof that a file exists.
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

        try
        {
            var journals = await store.GetOpenJournalsAsync(ct).ConfigureAwait(false);
            foreach (var journal in journals)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Published and Prepared journals both go through the reconciler. The previous
                    // short-circuit on an existing OutputRecord let a missing or replaced final file be
                    // reported as Succeeded; a journal is only closed after the committer proves identity.
                    var result = await ReconcileAsync(committer, journal, ct).ConfigureAwait(false);
                    if (result.Success)
                    {
                        await EnsureOutputAsync(store, journal, ct).ConfigureAwait(false);
                        await store.DeleteJournalAsync(journal.CommitToken, ct).ConfigureAwait(false);
                        // Reconciliation proved the final file's identity. Complete the owning
                        // attempt/job here: the later Committing sweep depends on an optional
                        // IOutputRecordVerifier and would otherwise degrade a proven output to
                        // NeedsAttention for committers that do not implement that interface.
                        await MarkSucceededAsync(store, journal, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var failure = result.Failure
                            ?? FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryFailed");
                        await MoveToNeedsAttentionAsync(store, journal.JobId, failure, ct).ConfigureAwait(false);
                        // Keep the journal open so the unresolved commit and its evidence can be inspected.
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One broken journal must not abort the whole recovery pass: keep the journal open
                    // for inspection, flag its job for manual review, and continue with the rest.
                    var failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryFailed", ex.Message);
                    try { await MoveToNeedsAttentionAsync(store, journal.JobId, failure, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* best effort; the journal still stays open */ }
                }
            }

            // A crash can occur after durable publication but before the final job transition,
            // including after the committer has already removed its journal. Verify the file itself;
            // the OutputRecord only proves what the previous process intended.
            foreach (var job in await store.GetJobsAsync(new[] { JobState.Committing }, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Isolate a broken Committing row (store or filesystem failure) from every other
                    // job in the sweep: flag it for manual review and continue.
                    var failure = FailureInfo.Of(FailureCode.CommitFailed, "error.commitRecoveryFailed", ex.Message);
                    try { await MoveToNeedsAttentionAsync(store, job.JobId, failure, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* best effort */ }
                }
            }
        }
        finally
        {
            // Always clear remaining active jobs, even when a journal listing or the Committing
            // sweep throws: a crash must never leave jobs stuck as "running" forever. Best effort so
            // this cleanup cannot mask the original recovery failure.
            try { await store.MarkActiveJobsInterruptedAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort */ }
        }
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

    /// <summary>
    /// Completes the attempt and job that own a successfully reconciled journal. Terminal states
    /// are never overridden, and the job transition uses the same revision CAS as every other
    /// writer so a concurrent update cannot be clobbered.
    /// </summary>
    private static async Task MarkSucceededAsync(IJobStore store, CommitJournalEntry journal, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var jobCompleted = false;
        for (var probe = 0; probe < 4; probe++)
        {
            var job = await store.GetJobAsync(journal.JobId, ct).ConfigureAwait(false);
            if (job is null)
            {
                return;
            }
            if (job.State == JobState.Succeeded)
            {
                // Already terminal; never "repair" an arbitrary attempt row from a stale journal.
                return;
            }
            if (JobStateMachine.IsTerminal(job.State))
            {
                return;
            }
            // The journal must belong to the job's current attempt. A journal from an older
            // attempt must not complete a newer in-flight attempt.
            if (job.ActiveAttemptId is Guid activeAttempt && activeAttempt != journal.AttemptId)
            {
                return;
            }

            // A later startup can resolve a journal whose previous recovery attempt left the job
            // in NeedsAttention; a proven identity upgrade is safe there too. Every other state
            // must be able to reach Succeeded through the normal transition table.
            var canComplete = job.State == JobState.NeedsAttention
                || JobStateMachine.CanTransition(job.State, JobState.Succeeded);
            if (!canComplete)
            {
                // Do not touch the attempt: the job may still be interrupted/failed later and an
                // attempt marked Succeeded while the job is not would be an inconsistent pair.
                return;
            }

            var updated = job with
            {
                State = JobState.Succeeded,
                ActiveAttemptId = null,
                Revision = job.Revision + 1,
                UpdatedUtc = now,
            };

            try
            {
                await store.UpdateJobAsync(updated, job.Revision, ct).ConfigureAwait(false);
                jobCompleted = true;
                break;
            }
            catch (Exception)
            {
                var fresh = await store.GetJobAsync(journal.JobId, CancellationToken.None).ConfigureAwait(false);
                if (fresh is null || fresh.Revision != job.Revision || fresh.State != job.State)
                {
                    continue;
                }
                throw;
            }
        }

        if (!jobCompleted)
        {
            return;
        }

        var attempt = await store.GetAttemptAsync(journal.AttemptId, ct).ConfigureAwait(false);
        if (attempt is not null && attempt.State != AttemptState.Succeeded)
        {
            await store.UpdateAttemptAsync(attempt with
            {
                State = AttemptState.Succeeded,
                EndedUtc = now,
            }, ct).ConfigureAwait(false);
        }
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
