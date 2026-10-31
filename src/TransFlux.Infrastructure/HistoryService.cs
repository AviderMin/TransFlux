using TransFlux.Application.Contracts;

namespace TransFlux.Infrastructure;

/// <summary>SQLite-backed conversion history. Reads terminal jobs (Succeeded/Failed/Canceled/Interrupted)
/// newest-first; clearing removes history records only (outputs, attempts, terminal jobs) in a short
/// transaction and never deletes output files, active jobs, presets, settings or the capability cache.</summary>
public sealed class HistoryService : IHistoryService
{
    private readonly SqliteJobStore _store;

    public HistoryService(SqliteJobStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int limit, CancellationToken ct)
        => _store.GetHistoryAsync(limit, ct);

    public async Task ClearAsync(CancellationToken ct)
    {
        await _store.ClearHistoryAsync(ct).ConfigureAwait(false);
    }
}
