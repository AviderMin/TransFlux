using TransFlux.Domain.Jobs;

namespace TransFlux.App.State;

/// <summary>Semantic status tone shared by task cards and history entries (one mapping, no copies).</summary>
public enum StatusTone { Success, Critical, Caution, Neutral, Attention }

/// <summary>
/// Pure classification of a card / history entry into a semantic tone. The UI layer maps
/// tones onto theme brushes; tests lock the semantics here (UI review fix round).
/// </summary>
public static class StatusTones
{
    public static StatusTone For(JobState? state, DraftStatus draftStatus = DraftStatus.Ready,
        bool probing = false, bool blocked = false)
    {
        if (state is { } s)
        {
            return s switch
            {
                JobState.Succeeded => StatusTone.Success,
                JobState.Failed => StatusTone.Critical,
                JobState.Interrupted or JobState.NeedsAttention => StatusTone.Caution,
                JobState.Canceled => StatusTone.Neutral,
                JobState.CancelRequested => StatusTone.Caution,
                _ => StatusTone.Attention,
            };
        }
        if (draftStatus == DraftStatus.Invalid) return StatusTone.Critical;
        if (blocked) return StatusTone.Caution;
        if (probing) return StatusTone.Neutral;
        return StatusTone.Attention;
    }
}
