using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;
using TransFlux.Hardware.Windows;

namespace TransFlux.Infrastructure.Tests;

public sealed class CapabilityServiceTests
{
    private sealed class FakeEngine : IEngineCapabilities
    {
        public List<string> ValidateCalls { get; } = new();
        public List<HardwareValidationRequest> Requests { get; } = new();
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
            Requests.Add(request);
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
        return (store, new CapabilityService(engine, store, new SystemClock(), () => new[]
        {
            new AdapterInfo { AdapterId = "LUID-one", Name = "NVIDIA one", VendorId = 0x10DE, DxgiIndex = 0 },
            new AdapterInfo { AdapterId = "LUID-two", Name = "NVIDIA two", VendorId = 0x10DE, DxgiIndex = 2 },
            new AdapterInfo { AdapterId = "software", Name = "Software", VendorId = 0x1002, DxgiIndex = 3, IsSoftware = true },
        }), engine);
    }

    [Fact]
    public async Task CheapQuery_DoesNotPreventLaterTrial_AndBindsEveryPhysicalAdapter()
    {
        using var dir = new TempDir();
        var (store, svc, engine) = await CreateAsync(dir);
        await using var guard = store;
        var cheap = await svc.GetAsync(new CapabilityQuery { IncludeTrialRun = false }, default);
        Assert.Empty(engine.Requests);
        Assert.False(cheap.HasAvailableGpuEncoder);
        var tested = await svc.GetAsync(new CapabilityQuery { IncludeTrialRun = true }, default);
        Assert.True(tested.HasAvailableGpuEncoder);
        Assert.Equal(new int?[] { 0, 2 }, engine.Requests.Where(r => r.EncoderName == "h264_nvenc").Select(r => r.Direct3DAdapterIndex));
        Assert.DoesNotContain(engine.Requests, r => r.EncoderName == "h264_amf");
        Assert.Equal(2, tested.Candidates.Count(c => c.Backend == "nvidia" && c.TrialCompleted && c.Available));
    }

    [Fact]
    public async Task TargetedQuery_DoesNotPoisonFullSnapshot()
    {
        using var dir = new TempDir();
        var (store, svc, engine) = await CreateAsync(dir);
        await using var guard = store;
        var targeted = await svc.GetAsync(new CapabilityQuery { IncludeTrialRun = true, TargetEncoders = new[] { "h264_qsv" } }, default);
        Assert.DoesNotContain(targeted.Candidates, c => c.Backend == "nvidia");
        var full = await svc.GetAsync(new CapabilityQuery { IncludeTrialRun = true }, default);
        Assert.NotEqual(targeted.CapabilityHash, full.CapabilityHash);
        Assert.Contains(full.Candidates, c => c.Backend == "nvidia" && c.Available);
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
