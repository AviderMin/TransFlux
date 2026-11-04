using TransFlux.Application.Contracts;
using TransFlux.Domain.Plans;

namespace TransFlux.Infrastructure.Tests;

public sealed class WorkspacePersistenceTests
{
    [Fact]
    public async Task WorkspaceSurvivesReopen_AndRemovalDoesNotDeleteHistoryOrFiles()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "state.db");
        var source = Path.Combine(dir.Path, "source.mp4"); File.WriteAllText(source, "source unchanged");
        var id = Guid.NewGuid(); var job = Guid.NewGuid();
        await using (var db = new SqliteJobStore(path))
        {
            await db.InitializeAsync(default);
            Assert.Null(await new WorkspacePersistence(db).LoadAsync(default));
            await new WorkspacePersistence(db).SaveAsync([new(id, source, new ConversionRequest
                { SourceSnapshotId = id, CrfOverride = 18, Output = new() { OutputDirectory = dir.Path } }, job)], default);
        }
        await using var reopened = new SqliteJobStore(path); await reopened.InitializeAsync(default);
        var service = new WorkspacePersistence(reopened);
        var entries = await service.LoadAsync(default); Assert.NotNull(entries);
        var restored = Assert.Single(entries);
        Assert.Equal(job, restored.JobId); Assert.Equal(18, restored.Request.CrfOverride);
        await service.SaveAsync([], default);
        var cleared = await service.LoadAsync(default); Assert.NotNull(cleared);
        Assert.Empty(cleared); Assert.Equal("source unchanged", File.ReadAllText(source));
    }

    [Fact]
    public async Task CorruptWorkspace_IsBackedUpAndLaterSavesStillPersist()
    {
        using var dir = new TempDir();
        await using var db = new SqliteJobStore(Path.Combine(dir.Path, "state.db"));
        await db.InitializeAsync(default);

        const string corrupt = "{\"schema\":99,";
        await db.SaveSettingAsync("workspace.v1", corrupt, default);
        var service = new WorkspacePersistence(db);

        // A corrupt value must degrade to "no workspace" instead of throwing and disabling saves.
        Assert.Null(await service.LoadAsync(default));

        var settings = await db.GetSettingsAsync(default);
        var backup = Assert.Single(settings, kv => kv.Key.StartsWith("workspace.v1.corrupt-", StringComparison.Ordinal));
        Assert.Equal(corrupt, backup.Value);

        // User changes produced after the corrupt restore must persist to the primary key.
        var id = Guid.NewGuid();
        await service.SaveAsync([new WorkspaceEntry(id, Path.Combine(dir.Path, "source.mp4"),
            new ConversionRequest { SourceSnapshotId = id, Output = new() }, null)], default);
        var entries = await service.LoadAsync(default);
        Assert.NotNull(entries);
        Assert.Equal(id, Assert.Single(entries).MediaId);
    }
}
