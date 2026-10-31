using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;
using TransFlux.Hardware.Windows;

namespace TransFlux.Infrastructure.Tests;

public sealed class CapabilityServiceTests
{
    private sealed class FakeEngine : IEngineCapabilities
    {
        public List<string> ValidateCalls { get; } = new();
        public bool FailNvenc { get; set; }

        public CoreIdentity GetIdentity() => new() { CoreHash = "core-test", FfmpegVersion = "test" };

        public Task<FfmpegCapabilityInfo> QueryAsync(CancellationToken ct)
            => Task.FromResult(new FfmpegCapabilityInfo
            {
                Encoders = new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_amf", "aac" },
            });

        public Task<HardwareValidationResult> ValidateEncoderAsync(HardwareValidationRequest request, CancellationToken ct)
        {
            ValidateCalls.Add(request.EncoderName);
            if (request.EncoderName == "h264_nvenc" && FailNvenc)
                return Task.FromResult(new HardwareValidationResult
                {
                    Success = false,
                    FailureReason = "simulated nvenc init failure",
                    Elapsed = TimeSpan.FromMilliseconds(5),
                });
            return Task.FromResult(new HardwareValidationResult { Success = true, Elapsed = TimeSpan.FromMilliseconds(5) });
        }
    }

    private static async Task<(SqliteJobStore Store, CapabilityService Service, FakeEngine Engine)> CreateAsync(TempDir dir)
    {
        var store = new SqliteJobStore(Path.Combine(dir.Path, "state.db"));
        await store.InitializeAsync(default);
        var engine = new FakeEngine();
        return (store, new CapabilityService(engine, store, new SystemClock()), engine);
    }

    [Fact]
    public async Task Get_ProducesCandidates_And_CachesSecondCall()
    {
        using var dir = new TempDir();
        var (store, svc, engine) = await CreateAsync(dir);
        await using var storeGuard = store;

        var snap1 = await svc.GetAsync(new CapabilityQuery { IncludeTrialRun = true }, default);
        Assert.False(string.IsNullOrWhiteSpace(snap1.CapabilityHash));
        Assert.Contains(snap1.Candidates, c => c.Backend == "cpu" && c.EncoderName == "libx264" && c.Available);
        var firstCalls = engine.ValidateCalls.Count;
        Assert.True(firstCalls >= 1);

        var snap2 = await svc.GetAsync(new CapabilityQuery { IncludeTrialRun = true }, default);
        Assert.Equal(snap1.CapabilityHash, snap2.CapabilityHash);
        Assert.Equal(firstCalls, engine.ValidateCalls.Count); // served from cache, no re-trial
    }

    [Fact]
    public async Task Refresh_ForcesRetrial()
    {
        using var dir = new TempDir();
        var (store, svc, engine) = await CreateAsync(dir);
        await using var storeGuard = store;

        var first = await svc.GetAsync(new CapabilityQuery { IncludeTrialRun = true }, default);
        var calls = engine.ValidateCalls.Count;
        var refreshed = await svc.RefreshAsync(default);
        Assert.True(engine.ValidateCalls.Count > calls);
        Assert.Equal(first.CapabilityHash, refreshed.CapabilityHash);
        Assert.All(refreshed.Candidates, c => Assert.False(string.IsNullOrWhiteSpace(c.CandidateId)));
    }

    [Fact]
    public async Task FailedTrial_MarksCandidateUnavailable_WithReason()
    {
        using var dir = new TempDir();
        var (store, svc, engine) = await CreateAsync(dir);
        await using var storeGuard = store;
        engine.FailNvenc = true;

        var snap = await svc.GetAsync(new CapabilityQuery { IncludeTrialRun = true }, default);
        Assert.Contains(snap.Candidates, c => c.Backend == "cpu" && c.Available);

        var nvenc = snap.Candidates.FirstOrDefault(c => c.Backend == "nvidia" && c.EncoderName == "h264_nvenc");
        if (nvenc is not null)
        {
            Assert.False(nvenc.Available);
            Assert.False(string.IsNullOrWhiteSpace(nvenc.UnavailableReason));
        }
    }
}
