using TransFlux.Domain.Common;
using TransFlux.Domain.Jobs;

namespace TransFlux.Domain.Tests;

public class JobStateMachineTests
{
    private static readonly JobState[] AllStates = Enum.GetValues<JobState>();

    // Mirrors docs/07 state diagram exactly; the test fails if the implementation drifts.
    private static readonly Dictionary<JobState, JobState[]> Expected = new()
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

    [Fact]
    public void ExpectedMap_CoversEveryState()
    {
        Assert.Equal(AllStates.Length, Expected.Count);
        foreach (var s in AllStates) Assert.True(Expected.ContainsKey(s), $"Missing expected transitions for {s}");
    }

    [Fact]
    public void TransitionMatrix_MatchesContractForEveryPair()
    {
        foreach (var from in AllStates)
        {
            var allowed = Expected[from];
            foreach (var to in AllStates)
            {
                var expected = Array.IndexOf(allowed, to) >= 0;
                Assert.Equal(expected, JobStateMachine.CanTransition(from, to));
            }
        }
    }

    [Fact]
    public void Transition_ReturnsTarget_WhenAllowed()
    {
        Assert.Equal(JobState.Running, JobStateMachine.Transition(JobState.Preflighting, JobState.Running));
        Assert.Equal(JobState.Canceled, JobStateMachine.Transition(JobState.CancelRequested, JobState.Canceled));
        Assert.Equal(JobState.Succeeded, JobStateMachine.Transition(JobState.Committing, JobState.Succeeded));
    }

    [Theory]
    [InlineData(JobState.Queued, JobState.Running)]
    [InlineData(JobState.Queued, JobState.Succeeded)]
    [InlineData(JobState.Succeeded, JobState.Queued)]
    [InlineData(JobState.Canceled, JobState.Queued)]
    [InlineData(JobState.Committing, JobState.Canceled)]
    [InlineData(JobState.Running, JobState.Queued)]
    [InlineData(JobState.Preflighting, JobState.Verifying)]
    public void Transition_Throws_WhenIllegal(JobState from, JobState to)
    {
        Assert.False(JobStateMachine.CanTransition(from, to));
        Assert.Throws<DomainException>(() => JobStateMachine.Transition(from, to));
    }

    [Fact]
    public void IsTerminal_MatchesTerminalStates()
    {
        foreach (var s in AllStates)
        {
            var expected = s is JobState.Succeeded or JobState.Failed or JobState.Canceled or JobState.Interrupted;
            Assert.Equal(expected, JobStateMachine.IsTerminal(s));
        }
    }

    [Fact]
    public void AllowsCancel_RefusesCommitBoundaryAndLater()
    {
        foreach (var s in AllStates)
        {
            var expected = s is JobState.Queued or JobState.Preflighting or JobState.Running or JobState.Verifying;
            Assert.Equal(expected, JobStateMachine.AllowsCancel(s));
        }
        Assert.False(JobStateMachine.AllowsCancel(JobState.Committing));
        Assert.False(JobStateMachine.AllowsCancel(JobState.CancelRequested));
        Assert.False(JobStateMachine.AllowsCancel(JobState.NeedsAttention));
    }

    [Fact]
    public void IsActive_MatchesInFlightStates()
    {
        foreach (var s in AllStates)
        {
            var expected = s is JobState.Preflighting or JobState.Running or JobState.Verifying
                or JobState.Committing or JobState.Recovering or JobState.CancelRequested;
            Assert.Equal(expected, JobStateMachine.IsActive(s));
        }
        Assert.False(JobStateMachine.IsActive(JobState.Queued));
        Assert.False(JobStateMachine.IsActive(JobState.Succeeded));
    }
}
