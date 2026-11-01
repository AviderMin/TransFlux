using System.Diagnostics;
using System.Globalization;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;
using TransFlux.Engine.FFmpeg.Core;
using TransFlux.Engine.FFmpeg.Processes;

namespace TransFlux.Engine.FFmpeg.Capabilities;

/// <summary>
/// Engine-owned core capabilities: identity (version/buildconf/paths/hash) and compiled
/// encoders/decoders/hwaccels/filters/pixel formats, plus a bounded trial encode used by the
/// hardware capability service. Results are cached for the process lifetime of this instance.
/// </summary>
public sealed class FfmpegEngineCapabilities : IEngineCapabilities
{
    private readonly FfmpegCorePaths _core;
    private readonly SemaphoreSlim _queryLock = new(1, 1);
    private FfmpegCapabilityInfo? _cached;
    private CoreIdentity? _identity;

    public FfmpegEngineCapabilities(FfmpegCorePaths core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
    }

    public CoreIdentity GetIdentity()
    {
        if (_identity is not null) return _identity;

        var version = _core.Version;
        var buildConfiguration = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _core.FfmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-version");
            using var process = Process.Start(psi);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(version) && trimmed.StartsWith("ffmpeg version ", StringComparison.Ordinal))
                    {
                        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3) version = parts[2];
                    }
                    else if (trimmed.StartsWith("configuration:", StringComparison.Ordinal))
                    {
                        buildConfiguration = trimmed["configuration:".Length..].Trim();
                    }
                }
            }
        }
        catch { /* identity still usable from core paths/hash */ }

        _identity = new CoreIdentity
        {
            CoreHash = _core.CoreHash,
            FfmpegVersion = version,
            FfprobePath = _core.FfprobePath,
            FfmpegPath = _core.FfmpegPath,
            BuildConfiguration = buildConfiguration,
        };
        return _identity;
    }

    public async Task<FfmpegCapabilityInfo> QueryAsync(CancellationToken ct)
    {
        if (_cached is not null) return _cached;

        await _queryLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;
            var timeout = TimeSpan.FromSeconds(20);

            var encoders = await RunListAsync(new[] { "-encoders" }, timeout, ct).ConfigureAwait(false);
            var decoders = await RunListAsync(new[] { "-decoders" }, timeout, ct).ConfigureAwait(false);
            var codecs = await RunCodecsAsync(encoders, timeout, ct).ConfigureAwait(false);
            var hwaccels = await RunHwaccelsAsync(timeout, ct).ConfigureAwait(false);
            var filters = await RunListAsync(new[] { "-filters" }, timeout, ct).ConfigureAwait(false);
            var pixelFormats = await RunListAsync(new[] { "-pix_fmts" }, timeout, ct).ConfigureAwait(false);

            _cached = new FfmpegCapabilityInfo
            {
                Encoders = encoders,
                Decoders = decoders,
                Codecs = codecs,
                Hwaccels = hwaccels,
                Filters = filters,
                PixelFormats = pixelFormats,
            };
            return _cached;
        }
        finally
        {
            _queryLock.Release();
        }
    }

    public async Task<HardwareValidationResult> ValidateEncoderAsync(HardwareValidationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-n", "-loglevel", "error" };
        if (request.Direct3DAdapterIndex is < 0)
            return new HardwareValidationResult { FailureReason = "显卡索引无效。" };
        try
        {
            HardwareEncoding.AddDevice(arguments, request.EncoderName, request.Direct3DAdapterIndex);
        }
        catch (Exception ex)
        {
            return new HardwareValidationResult { FailureReason = ex.Message };
        }
        arguments.Add("-f"); arguments.Add("lavfi");
        arguments.Add("-i"); arguments.Add($"testsrc2=duration=1:size={Evenize(request.Width)}x{Evenize(request.Height)}:rate=30");
        if (request.Direct3DAdapterIndex is not null)
        {
            arguments.Add("-vf"); arguments.Add(HardwareEncoding.UploadFilter(request.EncoderName));
        }
        else if (!string.IsNullOrWhiteSpace(request.PixelFormat))
        {
            arguments.Add("-pix_fmt"); arguments.Add(request.PixelFormat!);
        }
        arguments.Add("-c:v"); arguments.Add(request.EncoderName);
        if (request.EncoderName == "h264_nvenc") arguments.AddRange(["-preset", "p4", "-rc", "constqp", "-qp", "23"]);
        if (request.EncoderName == "h264_qsv") arguments.AddRange(["-preset", "medium", "-global_quality", "23"]);
        if (request.EncoderName == "h264_amf") arguments.AddRange(["-quality", "balanced", "-rc", "cqp", "-qp_i", "23", "-qp_p", "23", "-qp_b", "23"]);
        arguments.Add("-t"); arguments.Add("1");
        arguments.Add("-f"); arguments.Add("null"); arguments.Add("-");

        var timeout = request.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : request.Timeout;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await ProcessCapture.RunAsync(_core.FfmpegPath, arguments, timeout, 256 * 1024, 64 * 1024, ct).ConfigureAwait(false);
            stopwatch.Stop();
            if (result.TimedOut)
                return new HardwareValidationResult { Success = false, FailureReason = $"Trial encode timed out after {timeout.TotalSeconds:0}s.", Elapsed = stopwatch.Elapsed };
            if (result.ExitCode != 0)
                return new HardwareValidationResult { Success = false, FailureReason = HardwareEncoding.FailureSummary(result.Stderr), DiagnosticDetail = Trim(result.Stderr), Elapsed = stopwatch.Elapsed };
            return new HardwareValidationResult { Success = true, Elapsed = stopwatch.Elapsed };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new HardwareValidationResult { Success = false, FailureReason = ex.Message, Elapsed = stopwatch.Elapsed };
        }
    }

    private async Task<IReadOnlyList<string>> RunListAsync(string[] commandArgs, TimeSpan timeout, CancellationToken ct)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin" };
        arguments.AddRange(commandArgs);
        var result = await ProcessCapture.RunAsync(_core.FfmpegPath, arguments, timeout, 4 * 1024 * 1024, 64 * 1024, ct).ConfigureAwait(false);
        return ParseNames(result.Stdout, skipFirstToken: true);
    }

    private async Task<IReadOnlyList<FfmpegCodecInfo>> RunCodecsAsync(IReadOnlyList<string> encoders, TimeSpan timeout, CancellationToken ct)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-codecs" };
        var result = await ProcessCapture.RunAsync(_core.FfmpegPath, arguments, timeout, 8 * 1024 * 1024, 64 * 1024, ct).ConfigureAwait(false);
        return ParseCodecs(result.Stdout, encoders);
    }

    internal static IReadOnlyList<FfmpegCodecInfo> ParseCodecs(string output, IReadOnlyList<string> encoders)
    {
        var codecs = new List<FfmpegCodecInfo>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('-')) continue;
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            var flags = parts[0];
            if (flags.Length < 3 || !IsFlagToken(flags)) continue;
            if (flags[0] != 'D' && flags[1] != 'E') continue;
            var kind = flags[2] switch
            {
                'V' => MediaKind.Video,
                'A' => MediaKind.Audio,
                'S' => MediaKind.Subtitle,
                'D' => MediaKind.Data,
                'T' => MediaKind.Attachment,
                _ => MediaKind.Unknown,
            };
            if (kind == MediaKind.Unknown) continue;
            var name = parts[1];
            var description = parts[2];
            var marker = description.IndexOf(" (encoders:", StringComparison.Ordinal);
            if (marker >= 0) description = description[..marker];
            var hardware = encoders
                .Where(encoder => encoder.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase))
                .Where(encoder => HardwareEncoderSuffixes.Contains(encoder[(name.Length + 1)..]))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            codecs.Add(new FfmpegCodecInfo
            {
                Name = name,
                LongName = description,
                Kind = kind,
                CanDecode = flags[0] == 'D',
                CanEncode = flags[1] == 'E',
                HardwareEncoders = hardware,
            });
        }
        return codecs;
    }

    private static readonly HashSet<string> HardwareEncoderSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvenc", "qsv", "amf", "vaapi", "videotoolbox", "v4l2m2m", "d3d12va", "mf", "vulkan",
    };

    private async Task<IReadOnlyList<string>> RunHwaccelsAsync(TimeSpan timeout, CancellationToken ct)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-hwaccels" };
        var result = await ProcessCapture.RunAsync(_core.FfmpegPath, arguments, timeout, 256 * 1024, 64 * 1024, ct).ConfigureAwait(false);
        var names = new List<string>();
        foreach (var raw in result.Stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Hardware acceleration", StringComparison.OrdinalIgnoreCase)) continue;
            var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!names.Contains(token)) names.Add(token);
        }
        return names;
    }

    private static IReadOnlyList<string> ParseNames(string output, bool skipFirstToken)
    {
        var names = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(':')) continue; // section header such as "Encoders:"
            if (line.StartsWith('-')) continue; // separator legend
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            if (skipFirstToken)
            {
                // Flag legend lines like "V..... = Video" put "=" in the name slot.
                if (tokens[1] == "=") continue;
                var name = tokens[1];
                if (IsFlagToken(tokens[0]) && !names.Contains(name)) names.Add(name);
            }
        }
        return names;
    }

    private static bool IsFlagToken(string token)
    {
        if (token.Length is < 2 or > 12) return false;
        foreach (var c in token)
        {
            if (!(char.IsLetter(c) || c == '.' || c == '_' || c == '-')) return false;
        }
        return true;
    }

    /// <summary>Clamps to at least 2 and rounds down to even for yuv420 test patterns.</summary>
    private static int Evenize(int value)
    {
        value = Math.Max(2, value);
        return value - (value & 1);
    }

    private static string Trim(string value) => value.Length <= 1000 ? value : value[^1000..];
}
