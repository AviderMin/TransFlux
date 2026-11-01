using System.Text.Json;
using TransFlux.Application.Contracts;

namespace TransFlux.Infrastructure;

public sealed class WorkspacePersistence(IJobStore store) : IWorkspacePersistence
{
    private const string Key = "workspace.v1";
    public async Task<IReadOnlyList<WorkspaceEntry>?> LoadAsync(CancellationToken ct)
    {
        var settings = await store.GetSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.TryGetValue(Key, out var json)) return null;
        return JsonSerializer.Deserialize<WorkspaceEntry[]>(json, CanonicalJson.Options) ?? Array.Empty<WorkspaceEntry>();
    }
    public Task SaveAsync(IReadOnlyList<WorkspaceEntry> entries, CancellationToken ct)
        => store.SaveSettingAsync(Key, CanonicalJson.Serialize(entries), ct);
}
