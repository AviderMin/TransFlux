using TransFlux.Domain.Plans;

namespace TransFlux.Application.Contracts;

/// <summary>Visible cards, independent of immutable execution/history records.</summary>
public sealed record WorkspaceEntry(Guid MediaId, string SourcePath, ConversionRequest Request, Guid? JobId);

public interface IWorkspacePersistence
{
    /// <summary>Null means no saved workspace; an empty list means the user cleared it.</summary>
    Task<IReadOnlyList<WorkspaceEntry>?> LoadAsync(CancellationToken ct);
    Task SaveAsync(IReadOnlyList<WorkspaceEntry> entries, CancellationToken ct);
}
