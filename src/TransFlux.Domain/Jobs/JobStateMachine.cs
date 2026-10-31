namespace TransFlux.Domain.Jobs;

/// <summary>Pure transition rules for the job state machine (see docs/07).</summary>
public static class JobStateMachine
{
    private static readonly Dictionary<JobState, JobState[]> Allowed = new()
    {
        [JobState.Queued] = new[] { JobState.Preflighting, JobState.Canceled, JobState.NeedsAttention },
        [JobState.Preflighting] = new[] { JobState.Running, JobState.NeedsAttention, JobState.Failed, JobState.CancelRequested },
        [JobState.Running] = new[] { JobState.Verifying, JobState.CancelRequested, JobState.Recovering, JobState.Failed, JobState.Interrupted },
        [JobState.Recovering] = new[] { JobState.Preflighting, JobState.Failed, JobState.CancelRequested },
        [JobState.Verifying] = new[] { JobState.Committing, JobState.Failed, JobState.CancelRequested, JobState.Interrupted },
        [JobState.Committing] = new[] { JobState.Succeeded, JobState.NeedsAttention, JobState.Interrupted },
        [JobState.CancelRequested] = new[] { JobState.Canceled },
        [JobState.NeedsAttention] = new[] { JobState.Queued, JobState.Canceled },
        [JobState.Succeeded] = Array.Empty<JobState>(),
        [JobState.Failed] = Array.Empty<JobState>(),
        [JobState.Canceled] = Array.Empty<JobState>(),
        [JobState.Interrupted] = Array.Empty<JobState>(),
    };

    public static bool CanTransition(JobState from, JobState to)
        => Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static JobState Transition(JobState from, JobState to)
    {
        if (!CanTransition(from, to))
            throw new Domain.Common.DomainException($"Illegal job transition {from} -> {to}.");
        return to;
    }

    public static bool IsTerminal(JobState s)
        => s is JobState.Succeeded or JobState.Failed or JobState.Canceled or JobState.Interrupted;

    /// <summary>Cancellation is refused once the job enters the commit boundary.</summary>
    public static bool AllowsCancel(JobState s)
        => s is JobState.Queued or JobState.Preflighting or JobState.Running or JobState.Verifying;

    public static bool IsActive(JobState s)
        => s is JobState.Preflighting or JobState.Running or JobState.Verifying or JobState.Committing or JobState.Recovering or JobState.CancelRequested;
}
