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
}
