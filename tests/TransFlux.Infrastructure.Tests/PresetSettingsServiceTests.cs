using TransFlux.Application.Contracts;
using TransFlux.Domain.Presets;
using TransFlux.Domain.Rules;

namespace TransFlux.Infrastructure.Tests;

public sealed class PresetSettingsServiceTests
{
    [Fact]
    public async Task PresetService_SeedsBuiltIns_And_ManagesUserPresets()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(Path.Combine(dir.Path, "state.db"));
        await store.InitializeAsync(default);
        var svc = new PresetService(store);

        var seeded = await svc.GetAllAsync(default);
        Assert.Equal(BuiltInPresets.All.Count, seeded.Count(p => p.BuiltIn));

        // Idempotent re-read does not duplicate built-ins.
        var again = await svc.GetAllAsync(default);
        Assert.Equal(BuiltInPresets.All.Count, again.Count(p => p.BuiltIn));

        var user = new Preset
        {
            PresetId = Guid.NewGuid(),
            Name = "用户预设",
            Template = new PresetRequestTemplate(),
        };
        await svc.SaveAsync(user, default);
        Assert.Contains(await svc.GetAllAsync(default), p => p.PresetId == user.PresetId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.DeleteAsync(BuiltInPresets.GeneralVideoBalancedId, default));

        await svc.DeleteAsync(user.PresetId, default);
        Assert.DoesNotContain(await svc.GetAllAsync(default), p => p.PresetId == user.PresetId);
    }

    [Fact]
    public async Task SettingsService_RoundTrips_AppSettings()
    {
        using var dir = new TempDir();
        await using var store = new SqliteJobStore(Path.Combine(dir.Path, "state.db"));
        await store.InitializeAsync(default);
        var svc = new SettingsService(store, new SystemClock());

        var defaults = await svc.GetAsync(default);
        Assert.Equal("System", defaults.Theme);
        Assert.Equal(1, defaults.MaxConcurrentTranscodes);

        await svc.SaveAsync(defaults with { Theme = "Dark", Language = "zh-Hans", MaxConcurrentTranscodes = 3 }, default);

        var reloaded = await svc.GetAsync(default);
        Assert.Equal("Dark", reloaded.Theme);
        Assert.Equal("zh-Hans", reloaded.Language);
        Assert.Equal(3, reloaded.MaxConcurrentTranscodes);
    }
}
