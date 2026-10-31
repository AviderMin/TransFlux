using TransFlux.Application.Contracts;
using TransFlux.Domain.Presets;
using TransFlux.Domain.Rules;

namespace TransFlux.Infrastructure;

/// <summary>Store-backed preset catalog. Seeds the frozen built-in presets when absent.</summary>
public sealed class PresetService : IPresetService
{
    private readonly IJobStore _store;

    public PresetService(IJobStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<IReadOnlyList<Preset>> GetAllAsync(CancellationToken ct)
    {
        var existing = await _store.GetPresetsAsync(ct);
        var byId = existing.ToDictionary(p => p.PresetId);

        var missingBuiltIns = BuiltInPresets.All.Where(p => !byId.ContainsKey(p.PresetId)).ToList();
        foreach (var preset in missingBuiltIns)
        {
            await _store.UpsertPresetAsync(preset, ct);
            byId[preset.PresetId] = preset;
        }

        return byId.Values
            .OrderByDescending(p => p.BuiltIn)
            .ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public Task SaveAsync(Preset preset, CancellationToken ct)
    {
        if (preset is null) throw new ArgumentNullException(nameof(preset));
        if (string.IsNullOrWhiteSpace(preset.Name)) throw new ArgumentException("Preset name is required.", nameof(preset));
        var stored = preset.Revision <= 0 ? preset with { Revision = 1 } : preset;
        return _store.UpsertPresetAsync(stored, ct);
    }

    public async Task DeleteAsync(Guid presetId, CancellationToken ct)
    {
        if (BuiltInPresets.All.Any(p => p.PresetId == presetId))
            throw new InvalidOperationException("Built-in presets cannot be deleted.");
        await _store.DeletePresetAsync(presetId, ct);
    }
}
