using TransFlux.Domain.Jobs;

namespace TransFlux.App.State;

/// <summary>Execution projected onto the same card throughout its lifecycle.</summary>
public sealed record QueueRow(Job Job, ProgressSnapshot? Progress, OutputRecord? Output, FailureInfo? Failure, Attempt? Attempt)
{
    public JobState State => Job.State;
    public bool IsTerminal => JobStateMachine.IsTerminal(State);
    public bool IsActive => JobStateMachine.IsActive(State);
    public bool CanCancel => JobStateMachine.AllowsCancel(State);
    public bool CanRetry => State is JobState.Failed or JobState.Canceled or JobState.Interrupted or JobState.NeedsAttention;
}
