using System.Text.Json;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;

namespace TransFlux.Hardware.Windows;

/// <summary>Real capability probing: DXGI adapters + compiled encoders from the pinned core +
/// bounded trial encodes, cached by core/os/adapter/driver/probe-schema identity. Exposes the CPU
/// libx264 baseline plus NVENC/QSV/AMF only when a device exists and a trial encode succeeds.</summary>
public sealed class CapabilityService : ICapabilityService
{
    private const int ProbeSchemaVersion = 1;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    private readonly IEngineCapabilities _engine;
    private readonly IJobStore _store;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    public CapabilityService(IEngineCapabilities engine, IJobStore store, IClock clock)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<CapabilitySnapshot> GetAsync(CapabilityQuery query, CancellationToken ct)
        => GetOrProbeAsync(query, false, ct);

    public Task<CapabilitySnapshot> RefreshAsync(CancellationToken ct)
        => GetOrProbeAsync(new CapabilityQuery { ForceRefresh = true, IncludeTrialRun = true }, true, ct);

    private async Task<CapabilitySnapshot> GetOrProbeAsync(CapabilityQuery query, bool force, CancellationToken ct)
    {
        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CoreIdentity core;
            try { core = _engine.GetIdentity(); }
            catch { core = new CoreIdentity { CoreHash = "unknown" }; }

            var adapters = SafeEnumerateAdapters();
            var osBuild = Environment.OSVersion.Version.Build.ToString();
            var cacheKey = BuildCacheKey(core, osBuild, adapters);

            if (!force && !query.ForceRefresh)
            {
                var cached = await _store.GetCapabilityAsync(cacheKey, ct).ConfigureAwait(false);
                if (cached is { } hit && _clock.UtcNow - hit.TestedUtc < CacheTtl)
                {
                    try
                    {
                        var restored = JsonSerializer.Deserialize<CapabilitySnapshot>(hit.PayloadJson, CanonicalJson.Options);
                        if (restored is not null) return restored;
                    }
                    catch (JsonException)
                    {
                        // Corrupt cache entry: fall through and re-probe.
                    }
                }
            }

            var snapshot = await ProbeAsync(core, osBuild, adapters, cacheKey, query, ct).ConfigureAwait(false);
            try
            {
                await _store.SaveCapabilityAsync(cacheKey, CanonicalJson.Serialize(snapshot), snapshot.ProbedUtc, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Persisting the cache is best-effort; the in-memory snapshot is still returned.
            }
            return snapshot;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task<CapabilitySnapshot> ProbeAsync(
        CoreIdentity core,
        string osBuild,
        IReadOnlyList<AdapterInfo> adapters,
        string cacheKey,
        CapabilityQuery query,
        CancellationToken ct)
    {
        var encoders = await SafeQueryEncodersAsync(ct).ConfigureAwait(false);
        var targets = query.TargetEncoders.Count > 0
            ? new HashSet<string>(query.TargetEncoders, StringComparer.OrdinalIgnoreCase)
            : null;

        var candidates = new List<CandidateCapability>
        {
            await ProbeCandidateAsync("cpu", "libx264", "cpu", encoders, query.IncludeTrialRun, ct).ConfigureAwait(false),
        };

        var gpuSpecs = new (string Backend, string Encoder, uint VendorId)[]
        {
            ("nvidia", "h264_nvenc", 0x10DE),
            ("intel", "h264_qsv", 0x8086),
            ("amd", "h264_amf", 0x1002),
        };

        foreach (var spec in gpuSpecs)
        {
            if (targets is not null && !targets.Contains(spec.Encoder)) continue;
            if (!encoders.Contains(spec.Encoder, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(Unavailable(spec.Backend, spec.Encoder, null, "encoder not present in core"));
                continue;
            }

            var adapter = adapters.FirstOrDefault(a => !a.IsSoftware && a.VendorId == spec.VendorId);
            if (adapter is null)
            {
                candidates.Add(Unavailable(spec.Backend, spec.Encoder, null, "no matching DXGI adapter"));
                continue;
            }

            candidates.Add(await ProbeCandidateAsync(spec.Backend, spec.Encoder, adapter.AdapterId, encoders, query.IncludeTrialRun, ct)
                .ConfigureAwait(false));
        }

        return new CapabilitySnapshot
        {
            CapabilityHash = cacheKey,
            CoreHash = core.CoreHash,
            OsBuild = osBuild,
            Adapters = adapters,
            Candidates = candidates,
            ProbedUtc = _clock.UtcNow,
        };
    }

    private async Task<CandidateCapability> ProbeCandidateAsync(
        string backend,
        string encoder,
        string deviceId,
        IReadOnlyList<string> encoders,
        bool includeTrial,
        CancellationToken ct)
    {
        if (!encoders.Contains(encoder, StringComparer.OrdinalIgnoreCase))
            return Unavailable(backend, encoder, deviceId, "encoder not present in core");

        if (backend != "cpu")
        {
            if (string.IsNullOrEmpty(deviceId) || deviceId == "cpu")
                return Unavailable(backend, encoder, null, "no matching DXGI adapter");
            if (!includeTrial)
                return Unavailable(backend, encoder, deviceId, "trial run not requested");
        }
        else
        {
            // Software encoder: presence plus (when requested) a trial encode.
            if (!includeTrial)
                return Available(backend, encoder, null);
        }

        HardwareValidationResult trial;
        try
        {
            trial = await _engine.ValidateEncoderAsync(new HardwareValidationRequest
            {
                EncoderName = encoder,
                DeviceArgument = backend == "cpu" ? null : deviceId,
                Timeout = TimeSpan.FromSeconds(10),
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable(backend, encoder, deviceId, $"trial encode threw: {ex.Message}");
        }

        return trial.Success
            ? Available(backend, encoder, backend == "cpu" ? null : deviceId)
            : Unavailable(backend, encoder, deviceId, trial.FailureReason ?? "trial encode failed");
    }

    private static CandidateCapability Available(string backend, string encoder, string? deviceId)
        => new()
        {
            CandidateId = BuildCandidateId(backend, encoder, deviceId),
            Backend = backend,
            EncoderName = encoder,
            DeviceId = deviceId,
            Available = true,
            UnavailableReason = null,
            SupportsGpuDecode = false,
            SupportsGpuFilter = false,
            Supports10Bit = false,
            SupportedPixelFormats = backend == "cpu" ? new[] { "yuv420p" } : Array.Empty<string>(),
        };

    private static CandidateCapability Unavailable(string backend, string encoder, string? deviceId, string reason)
        => new()
        {
            CandidateId = BuildCandidateId(backend, encoder, deviceId),
            Backend = backend,
            EncoderName = encoder,
            DeviceId = deviceId,
            Available = false,
            UnavailableReason = reason,
            SupportsGpuDecode = false,
            SupportsGpuFilter = false,
            Supports10Bit = false,
            SupportedPixelFormats = Array.Empty<string>(),
        };

    private static string BuildCandidateId(string backend, string encoder, string? deviceId)
        => $"{backend}:{encoder}:{deviceId ?? "default"}";

    private static string BuildCacheKey(CoreIdentity core, string osBuild, IReadOnlyList<AdapterInfo> adapters)
    {
        var payload = new
        {
            coreHash = core.CoreHash,
            osBuild,
            probeSchemaVersion = ProbeSchemaVersion,
            adapters = adapters
                .OrderBy(a => a.AdapterId, StringComparer.Ordinal)
                .Select(a => new { a.AdapterId, a.VendorId, a.DeviceId, a.DriverVersion })
                .ToArray(),
        };
        return CanonicalJson.Hash(payload);
    }

    private async Task<IReadOnlyList<string>> SafeQueryEncodersAsync(CancellationToken ct)
    {
        try
        {
            var info = await _engine.QueryAsync(ct).ConfigureAwait(false);
            return info.Encoders;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<AdapterInfo> SafeEnumerateAdapters()
    {
        try
        {
            return DxgiAdapterEnumerator.Enumerate();
        }
        catch
        {
            return Array.Empty<AdapterInfo>();
        }
    }
}
