using TransFlux.Application.Contracts;

namespace TransFlux.Infrastructure;

/// <summary>Persists <see cref="AppSettings"/> as JSON in the settings table under a stable key.</summary>
public sealed class SettingsService : ISettingsService
{
    private const string SettingsKey = "app.settings";

    private readonly IJobStore _store;

    public SettingsService(IJobStore store, IClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(clock);
    }

    public async Task<AppSettings> GetAsync(CancellationToken ct)
    {
        var all = await _store.GetSettingsAsync(ct);
        if (all.TryGetValue(SettingsKey, out var json) && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, CanonicalJson.Options);
                if (settings is not null) return settings;
            }
            catch (System.Text.Json.JsonException)
            {
                // Corrupt/unknown schema: fall back to defaults rather than failing the UI.
            }
        }
        return new AppSettings();
    }

    public Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        var json = CanonicalJson.Serialize(settings);
        return _store.SaveSettingAsync(SettingsKey, json, ct);
    }
}
