using System.Text.Json;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;

namespace TransFlux.Hardware.Windows;

/// <summary>Per-adapter trial encodes. Only completed trials enter the durable capability cache.</summary>
public sealed class CapabilityService : ICapabilityService
{
    private const int ProbeSchemaVersion = 3;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromMinutes(10);
    private readonly IEngineCapabilities _engine;
    private readonly IJobStore _store;
    private readonly IClock _clock;
    private readonly Func<IReadOnlyList<AdapterInfo>> _enumerate;
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    public CapabilityService(IEngineCapabilities engine, IJobStore store, IClock clock,
        Func<IReadOnlyList<AdapterInfo>>? enumerateAdapters = null)
    {
        _engine = engine; _store = store; _clock = clock;
        _enumerate = enumerateAdapters ?? DxgiAdapterEnumerator.Enumerate;
    }
    public Task<CapabilitySnapshot> GetAsync(CapabilityQuery query, CancellationToken ct) => GetOrProbeAsync(query, ct);
    public Task<CapabilitySnapshot> RefreshAsync(CancellationToken ct)
        => GetOrProbeAsync(new CapabilityQuery { ForceRefresh = true, IncludeTrialRun = true }, ct);

    private async Task<CapabilitySnapshot> GetOrProbeAsync(CapabilityQuery query, CancellationToken ct)
    {
        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var core = _engine.GetIdentity();
            IReadOnlyList<AdapterInfo> adapters;
            try { adapters = _enumerate().DistinctBy(a => a.AdapterId).ToArray(); }
            catch { adapters = []; }
            var osBuild = Environment.OSVersion.Version.Build.ToString();
            var key = CanonicalJson.Hash(new
            {
                coreHash = core.CoreHash, osBuild, probeSchemaVersion = ProbeSchemaVersion,
                adapters = adapters.OrderBy(a => a.AdapterId, StringComparer.Ordinal)
                    .Select(a => new { a.AdapterId, a.VendorId, a.DeviceId, a.DriverVersion, a.DxgiIndex, a.IsSoftware }).ToArray(),
                targets = query.TargetEncoders.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            });
            if (!query.ForceRefresh)
            {
                var cached = await _store.GetCapabilityAsync(key, ct).ConfigureAwait(false);
                if (cached is { } hit)
                {
                    try
                    {
                        var snapshot = JsonSerializer.Deserialize<CapabilitySnapshot>(hit.PayloadJson, CanonicalJson.Options);
                        var ttl = snapshot?.Candidates.Any(c => c.DeviceId is not null && !c.Available) == true ? FailureCacheTtl : CacheTtl;
                        var age = _clock.UtcNow - hit.TestedUtc;
                        if (snapshot is not null && snapshot.CoreHash == core.CoreHash && snapshot.CapabilityHash == key
                            && age >= TimeSpan.Zero && age < ttl) return snapshot;
                    }
                    catch (JsonException) { }
                }
            }
            var info = await _engine.QueryAsync(ct).ConfigureAwait(false);
            var candidates = new List<CandidateCapability>
            {
                await ProbeCandidateAsync("cpu", "libx264", null, info.Encoders, query.IncludeTrialRun, ct).ConfigureAwait(false),
            };
            foreach (var (backend, encoder, vendor) in new[]
                { ("nvidia", "h264_nvenc", 0x10DEu), ("intel", "h264_qsv", 0x8086u), ("amd", "h264_amf", 0x1002u) })
            {
                if (query.TargetEncoders.Count > 0 && !query.TargetEncoders.Contains(encoder, StringComparer.OrdinalIgnoreCase)) continue;
                if (!info.Encoders.Contains(encoder, StringComparer.OrdinalIgnoreCase))
                { candidates.Add(Unavailable(backend, encoder, null, "当前媒体核心未包含该编码器。")); continue; }
                var matches = adapters.Where(a => !a.IsSoftware && a.VendorId == vendor).ToArray();
                if (matches.Length == 0)
                { candidates.Add(Unavailable(backend, encoder, null, "本机没有对应厂商的可用显卡。")); continue; }
                foreach (var adapter in matches)
                    candidates.Add(await ProbeCandidateAsync(backend, encoder, adapter, info.Encoders, query.IncludeTrialRun, ct).ConfigureAwait(false));
            }
            var result = new CapabilitySnapshot { CapabilityHash = key, CoreHash = core.CoreHash, OsBuild = osBuild,
                Adapters = adapters, Candidates = candidates, ProbedUtc = _clock.UtcNow };
            // A cheap read must never poison the next real trial with "trial not requested".
            if (query.IncludeTrialRun)
            {
                try { await _store.SaveCapabilityAsync(key, CanonicalJson.Serialize(result), result.ProbedUtc, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* Cache persistence is optional. */ }
            }
            return result;
        }
        finally { _probeGate.Release(); }
    }

    private async Task<CandidateCapability> ProbeCandidateAsync(string backend, string encoder, AdapterInfo? adapter,
        IReadOnlyList<string> encoders, bool trial, CancellationToken ct)
    {
        if (!encoders.Contains(encoder, StringComparer.OrdinalIgnoreCase))
            return Unavailable(backend, encoder, adapter, "当前媒体核心未包含该编码器。");
        if (!trial)
            return backend == "cpu" ? Available(backend, encoder, null, false)
                : Unavailable(backend, encoder, adapter, "尚未完成试编码，请重新检测硬件。");
        HardwareValidationResult result;
        try
        {
            result = await _engine.ValidateEncoderAsync(new HardwareValidationRequest
            { EncoderName = encoder, Direct3DAdapterIndex = adapter?.DxgiIndex, PixelFormat = "yuv420p", Timeout = TimeSpan.FromSeconds(10) }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Unavailable(backend, encoder, adapter, "硬件试编码发生异常，可查看技术详情。") with { DiagnosticDetail = ex.Message }; }
        return result.Success ? Available(backend, encoder, adapter, true)
            : Unavailable(backend, encoder, adapter, result.FailureReason ?? "试编码未通过。") with
                { TrialCompleted = true, DiagnosticDetail = result.DiagnosticDetail };
    }
    private static CandidateCapability Available(string backend, string encoder, AdapterInfo? adapter, bool trial)
        => new() { CandidateId = $"{backend}:{encoder}:{adapter?.AdapterId ?? "default"}", Backend = backend,
            EncoderName = encoder, DeviceId = adapter?.AdapterId, Direct3DAdapterIndex = adapter?.DxgiIndex,
            Available = true, TrialCompleted = trial, SupportedPixelFormats = ["yuv420p"] };
    private static CandidateCapability Unavailable(string backend, string encoder, AdapterInfo? adapter, string reason)
        => new() { CandidateId = $"{backend}:{encoder}:{adapter?.AdapterId ?? "default"}", Backend = backend,
            EncoderName = encoder, DeviceId = adapter?.AdapterId, Direct3DAdapterIndex = adapter?.DxgiIndex,
            Available = false, UnavailableReason = reason };
}
