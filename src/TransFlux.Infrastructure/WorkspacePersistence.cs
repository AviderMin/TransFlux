using System.Globalization;
using System.Text.Json;
using TransFlux.Application.Contracts;

namespace TransFlux.Infrastructure;

public sealed class WorkspacePersistence(IJobStore store) : IWorkspacePersistence
{
    private const string Key = "workspace.v1";
    private const string CorruptKeyPrefix = Key + ".corrupt-";

    public async Task<IReadOnlyList<WorkspaceEntry>?> LoadAsync(CancellationToken ct)
    {
        var settings = await store.GetSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.TryGetValue(Key, out var json)) return null;

        try
        {
            return JsonSerializer.Deserialize<WorkspaceEntry[]>(json, CanonicalJson.Options)
                ?? Array.Empty<WorkspaceEntry>();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Never let one corrupt value disable persistence forever: keep the original bytes
            // under a timestamped backup key, report "no saved workspace", and let the next save
            // overwrite the primary key with fresh state.
            var backupKey = CorruptKeyPrefix
                + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            await store.SaveSettingAsync(backupKey, json, ct).ConfigureAwait(false);
            return null;
        }
    }

    public Task SaveAsync(IReadOnlyList<WorkspaceEntry> entries, CancellationToken ct)
        => store.SaveSettingAsync(Key, CanonicalJson.Serialize(entries), ct);
}
