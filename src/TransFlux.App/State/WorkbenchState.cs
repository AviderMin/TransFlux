using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.App.State;

public enum DraftStatus { Probing, Ready, Invalid }

/// <summary>A single imported source plus its per-item settings and probe result.</summary>
public sealed record MediaDraft
{
    public required Guid MediaId { get; init; }
    public required string Path { get; init; }
    public DraftStatus Status { get; init; } = DraftStatus.Probing;
    public SourceSnapshot? Snapshot { get; init; }
    public FailureInfo? Failure { get; init; }
    public string? ErrorText { get; init; }
    public ConversionOptions Options { get; init; } = ConversionOptions.Default;

    public string FileName => System.IO.Path.GetFileName(Path);
    public bool IsReady => Status == DraftStatus.Ready && Snapshot is not null;
    public bool IsProbing => Status == DraftStatus.Probing;
}

/// <summary>Session workbench state. Lives above the page navigator so switching
/// navigation never drops the draft or the selection (docs/03).</summary>
public sealed record WorkbenchState
{
    public IReadOnlyList<MediaDraft> Items { get; init; } = Array.Empty<MediaDraft>();
    public IReadOnlyList<Guid> Selected { get; init; } = Array.Empty<Guid>();
    public Guid? FocusedId { get; init; }

    public static readonly WorkbenchState Empty = new();

    public MediaDraft? Focused => Items.FirstOrDefault(i => i.MediaId == FocusedId) ?? Items.FirstOrDefault();

    public bool IsSelected(Guid id) => Selected.Contains(id);

    public IReadOnlyList<MediaDraft> ReadyItems => Items.Where(i => i.IsReady).ToList();

    public long? TotalReadyBytes
    {
        get
        {
            var ready = Items.Where(i => i.Snapshot is not null).ToList();
            if (ready.Count == 0) return null;
            return ready.Sum(i => i.Snapshot!.SizeBytes);
        }
    }
}

public abstract record WorkbenchAction;

public sealed record AddDrafts(IReadOnlyList<MediaDraft> Drafts) : WorkbenchAction;
public sealed record ProbeCompleted(Guid MediaId, SourceSnapshot? Snapshot, FailureInfo? Failure, string? ErrorText) : WorkbenchAction;
public sealed record RemoveDrafts(IReadOnlyList<Guid> Ids) : WorkbenchAction;
public sealed record FocusDraft(Guid? MediaId) : WorkbenchAction;
public sealed record ToggleSelect(Guid MediaId) : WorkbenchAction;
public sealed record SelectAllDrafts : WorkbenchAction;
public sealed record ClearDraftSelection : WorkbenchAction;
public sealed record UpdateOptions(IReadOnlyList<Guid> TargetIds, Func<ConversionOptions, ConversionOptions> Mutate) : WorkbenchAction;

public static class WorkbenchReducer
{
    public static WorkbenchState Reduce(WorkbenchState state, WorkbenchAction action) => action switch
    {
        AddDrafts a => state with { Items = state.Items.Concat(a.Drafts).ToList() },

        ProbeCompleted a => state with
        {
            Items = state.Items.Select(i => i.MediaId != a.MediaId
                ? i
                : i with
                {
                    Status = a.Failure is null && a.Snapshot is not null ? DraftStatus.Ready : DraftStatus.Invalid,
                    Snapshot = a.Snapshot,
                    Failure = a.Failure,
                    ErrorText = a.ErrorText,
                }).ToList(),
        },

        RemoveDrafts a => state with
        {
            Items = state.Items.Where(i => !a.Ids.Contains(i.MediaId)).ToList(),
            Selected = state.Selected.Where(id => !a.Ids.Contains(id)).ToList(),
            FocusedId = state.FocusedId is Guid f && a.Ids.Contains(f) ? null : state.FocusedId,
        },

        FocusDraft a => state with { FocusedId = a.MediaId },

        ToggleSelect a => state with
        {
            Selected = state.Selected.Contains(a.MediaId)
                ? state.Selected.Where(id => id != a.MediaId).ToList()
                : state.Selected.Append(a.MediaId).ToList(),
        },

        SelectAllDrafts => state with { Selected = state.Items.Select(i => i.MediaId).ToList() },

        ClearDraftSelection => state with { Selected = Array.Empty<Guid>() },

        UpdateOptions a => state with
        {
            Items = state.Items.Select(i => a.TargetIds.Contains(i.MediaId)
                ? i with { Options = a.Mutate(i.Options) }
                : i).ToList(),
        },

        _ => state,
    };
}
