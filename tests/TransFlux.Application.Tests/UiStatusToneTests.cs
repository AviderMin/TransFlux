using TransFlux.App.State;
using TransFlux.Domain.Jobs;

namespace TransFlux.Application.Tests;

/// <summary>UI review fix round: one status-tone mapping replaces the three copied color switches.</summary>
public sealed class UiStatusToneTests
{
    [Theory]
    [InlineData(JobState.Succeeded, StatusTone.Success)]
    [InlineData(JobState.Failed, StatusTone.Critical)]
    [InlineData(JobState.Interrupted, StatusTone.Caution)]
    [InlineData(JobState.NeedsAttention, StatusTone.Caution)]
    [InlineData(JobState.Canceled, StatusTone.Neutral)]
    [InlineData(JobState.CancelRequested, StatusTone.Caution)]
    [InlineData(JobState.Queued, StatusTone.Attention)]
    [InlineData(JobState.Running, StatusTone.Attention)]
    [InlineData(JobState.Verifying, StatusTone.Attention)]
    [InlineData(JobState.Committing, StatusTone.Attention)]
    [InlineData(JobState.Preflighting, StatusTone.Attention)]
    [InlineData(JobState.Recovering, StatusTone.Attention)]
    public void JobStateMapsToSemanticTone(JobState state, StatusTone expected)
        => Assert.Equal(expected, StatusTones.For(state));

    [Fact]
    public void DraftStatesMapToSemanticTones()
    {
        Assert.Equal(StatusTone.Critical, StatusTones.For(null, DraftStatus.Invalid));
        Assert.Equal(StatusTone.Caution, StatusTones.For(null, DraftStatus.Ready, blocked: true));
        Assert.Equal(StatusTone.Neutral, StatusTones.For(null, DraftStatus.Probing, probing: true));
        Assert.Equal(StatusTone.Attention, StatusTones.For(null, DraftStatus.Ready));
        // A blocking issue colours a card even while probing: users must see the problem.
        Assert.Equal(StatusTone.Caution, StatusTones.For(null, DraftStatus.Probing, probing: true, blocked: true));
    }
}
