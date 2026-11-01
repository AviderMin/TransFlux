using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;

namespace TransFlux.App.State;

public enum DraftStatus { Probing, Ready, Invalid }

/// <summary>One durable visible card. JobId freezes parameters after enqueue.</summary>
public sealed record MediaDraft
{
    public required Guid MediaId { get; init; }
    public required string Path { get; init; }
    public DraftStatus Status { get; init; } = DraftStatus.Probing;
    public SourceSnapshot? Snapshot { get; init; }
    public FailureInfo? Failure { get; init; }
    public string? ErrorText { get; init; }
    public ConversionOptions Options { get; init; } = ConversionOptions.Default;
    public Guid? JobId { get; init; }
    public bool Submitting { get; init; }
    public bool CanEdit => JobId is null && !Submitting;
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool IsReady => Status == DraftStatus.Ready && Snapshot is not null;
    public bool IsProbing => Status == DraftStatus.Probing;
}
