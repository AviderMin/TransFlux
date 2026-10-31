using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Infrastructure.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "transflux-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string NewFile(string name, string content = "hello-transflux")
    {
        var path = System.IO.Path.Combine(Path, name);
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
        return path;
    }

    public string NewEmptyFile(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}

internal static class TestData
{
    public static ConversionRequest NewRequest(Guid? sourceSnapshotId = null) => new()
    {
        SourceSnapshotId = sourceSnapshotId ?? Guid.NewGuid(),
    };

    public static SourceFingerprint NewFingerprint(string normalizedPath) => new()
    {
        NormalizedPath = normalizedPath,
        SizeBytes = 1234,
        ModifiedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
    };

    public static Job NewJob(
        Guid? jobId = null,
        Guid? sourceSnapshotId = null,
        long enqueueOrder = 1,
        JobState state = JobState.Queued,
        long revision = 0,
        string sourcePath = @"C:\media\input.mp4") => new()
    {
        JobId = jobId ?? Guid.NewGuid(),
        RequestSnapshot = NewRequest(sourceSnapshotId),
        SourceFingerprint = NewFingerprint(sourcePath),
        EnqueueOrder = enqueueOrder,
        State = state,
        Revision = revision,
        CreatedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        UpdatedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        RequestHash = "request-hash",
        SourcePath = sourcePath,
    };

    public static ValidatedPlan NewPlan() => new()
    {
        PlanId = Guid.NewGuid(),
        RequestHash = "request-hash",
        CoreHash = "core-hash",
        CapabilityHash = "capability-hash",
        ExpectedOutput = new ExpectedOutput { Container = ContainerFormat.Mp4 },
        PlanHash = "plan-hash",
    };

    public static Attempt NewAttempt(Guid jobId, int ordinal = 1, string? tempPath = null, string? finalPath = null) => new()
    {
        AttemptId = Guid.NewGuid(),
        JobId = jobId,
        Ordinal = ordinal,
        PlanSnapshot = NewPlan(),
        CoreHash = "core-hash",
        DeviceIdentity = null,
        TempPath = tempPath ?? @"C:\work\a.tmp",
        ReservedFinalPath = finalPath ?? @"C:\out\a.mp4",
        State = AttemptState.Created,
        StartedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
    };
}
