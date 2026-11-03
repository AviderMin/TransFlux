using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;

namespace TransFlux.Application.Services;

/// <summary>
/// Optional, additive capability letting <see cref="RecoveryService"/> use a committer's
/// crash-recovery reconciliation path. Implementations may verify the on-disk file identity and
/// complete or reject an open commit journal. RecoveryService falls back to
/// <see cref="IOutputCommitter.CommitAsync"/> when this interface is not implemented.
/// </summary>
public interface ICommitReconciler
{
    Task<CommitResult> ReconcileAsync(CommitJournalEntry entry, CancellationToken ct);
}

/// <summary>
/// Optional capability for verifying that a previously recorded output is still the committed
/// file (existence, size, and journal content hash when one is available). Used for jobs left in
/// Committing whose journal was already closed; a database record alone is never proof.
/// </summary>
public interface IOutputRecordVerifier
{
    Task<CommitResult> VerifyOutputAsync(OutputRecord output, CancellationToken ct);
}
