using System.Globalization;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Common;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;
using TransFlux.Engine.FFmpeg.Core;

namespace TransFlux.Engine.FFmpeg.Planner;

/// <summary>
/// Compiles a validated plan into typed ffmpeg argument lists. Never concatenates user text into
/// filters; the runner substitutes <see cref="PlanPlaceholders.Input"/>/<see cref="PlanPlaceholders.Output"/>.
/// Emits the selected software codec and, for H.264, verified hardware candidates from the capability snapshot.
/// </summary>
public sealed class FfmpegPlanner : IExecutionPlanner
{
    private static readonly string[] H264HardwareEncoders = { "h264_nvenc", "h264_qsv", "h264_amf" };

    private readonly FfmpegCorePaths _core;

    public FfmpegPlanner(FfmpegCorePaths core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
    }

    public Task<Result<ValidatedPlan>> BuildAsync(
        ConversionRequest request,
        SourceSnapshot source,
        CapabilitySnapshot capabilities,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(capabilities);
        ct.ThrowIfCancellationRequested();

        var issues = MediaCompatibility.Validate(request, source);
        var blocking = issues.Where(i => i.Blocking).ToList();
        if (blocking.Count > 0)
        {
            var detail = string.Join("; ", blocking.Select(i => $"{i.Code}: {i.Message}"));
            return Task.FromResult(Result<ValidatedPlan>.Validation(
                FailureInfo.Of(FailureCode.UnsupportedMedia, "plan.incompatible", detail)));
        }

        try
        {
            var plan = request.Mode switch
            {
                ConversionMode.VideoTranscode => BuildVideoTranscode(request, source, capabilities),
                ConversionMode.AudioExtract => BuildAudioExtract(request, source, capabilities),
                ConversionMode.Remux => BuildRemux(request, source, capabilities),
                _ => throw new DomainException($"Unknown conversion mode {request.Mode}."),
            };
            return Task.FromResult(Result<ValidatedPlan>.Ok(plan));
        }
        catch (PlanValidationException ex)
        {
            return Task.FromResult(Result<ValidatedPlan>.Validation(ex.Failure));
        }
        catch (DomainException ex)
        {
            return Task.FromResult(Result<ValidatedPlan>.Validation(
                FailureInfo.Of(FailureCode.PlanInvalid, "plan.invalid", ex.Message)));
        }
    }

    // ----------------------------------------------------------------- Video transcode

    private ValidatedPlan BuildVideoTranscode(ConversionRequest request, SourceSnapshot source, CapabilitySnapshot capabilities)
    {
        var video = MediaCompatibility.SelectPrimaryVideo(request, source)
            ?? throw new PlanValidationException(FailureInfo.Of(FailureCode.UnsupportedMedia, "plan.no_video", "No video stream selected."));
        if (video.Width is not int w || video.Height is not int h || w <= 0 || h <= 0)
            throw new PlanValidationException(FailureInfo.Of(FailureCode.PlanInvalid, "plan.unknown_dimensions", "Video dimensions are unknown."));

        var primaryAudio = MediaCompatibility.SelectPrimaryAudio(request, source);
        var audioIndices = SelectAudioIndices(request, source, primaryAudio);
        var geometry = ComputeGeometry(video, request.Resize);
        var expected = BuildVideoExpectedOutput(request, source, geometry, audioIndices.Count);

        var warnings = BuildWarnings(request, source);
        var candidates = new List<ExecutionCandidate>();

        var cpuEncoder = request.VideoCodec switch
        {
            VideoCodec.H264 => "libx264", VideoCodec.Hevc => "libx265", VideoCodec.Av1 => "libaom-av1", VideoCodec.Vp9 => "libvpx-vp9",
            _ => throw new DomainException("Unsupported video codec."),
        };
        var cpu = BuildVideoCandidate(request, video, audioIndices, geometry, HardwareBackend.Cpu, cpuEncoder, "cpu", usesGpu: false, uncalibrated: false);
        candidates.Add(cpu);

        var requested = request.Hardware.Backend;
        var hw = capabilities.Candidates
            .Where(c => request.VideoCodec == VideoCodec.H264 && c.Available && H264HardwareEncoders.Contains(c.EncoderName))
            .Where(c => request.Hardware.EncoderName is null || string.Equals(c.EncoderName, request.Hardware.EncoderName, StringComparison.OrdinalIgnoreCase))
            .Where(c => requested == HardwareBackend.Auto || MapBackend(c.Backend) == requested)
            .ToList();

        foreach (var cap in hw)
        {
            var backend = MapBackend(cap.Backend);
            candidates.Add(BuildVideoCandidate(request, video, audioIndices, geometry, backend, cap.EncoderName, cap.Backend, usesGpu: false, uncalibrated: true));
        }

        if (requested is HardwareBackend.NvidiaNvenc or HardwareBackend.IntelQsv or HardwareBackend.AmdAmf)
        {
            if (!candidates.Any(c => c.Backend == requested))
                throw new PlanValidationException(FailureInfo.Of(
                    FailureCode.DeviceUnavailable, "plan.device_unavailable",
                    $"Requested hardware backend {requested} has no verified encoder in the capability snapshot."));
            // Prefer the explicitly requested backend, keeping CPU as a fallback candidate.
            candidates = candidates.OrderByDescending(c => c.Backend == requested).ToList();
        }
        else if (requested == HardwareBackend.Cpu && candidates.Count > 1)
        {
            candidates = candidates.Where(c => c.Backend == HardwareBackend.Cpu).ToList();
        }

        var parameterPlan = candidates[0].Arguments.ToList();
        return Assemble(request, source, capabilities, candidates, expected, warnings, parameterPlan);
    }

    private static ExecutionCandidate BuildVideoCandidate(
        ConversionRequest request,
        StreamDescriptor video,
        IReadOnlyList<int> audioIndices,
        Geometry geometry,
        HardwareBackend backend,
        string encoder,
        string backendLabel,
        bool usesGpu,
        bool uncalibrated)
    {
        var args = GlobalPrefix(request.Trim?.Start);
        AddInput(args);
        AddTrimDuration(args, request.Trim);

        args.Add("-map"); args.Add($"0:{video.SourceIndex}");
        foreach (var audioIndex in audioIndices)
        {
            args.Add("-map"); args.Add($"0:{audioIndex}");
        }

        args.Add("-c:v"); args.Add(encoder);
        AddVideoQuality(args, encoder, request);
        if (request.VideoCodec == VideoCodec.Hevc && request.Container is ContainerFormat.Mp4 or ContainerFormat.Mov)
        { args.Add("-tag:v"); args.Add("hvc1"); }
        args.Add("-pix_fmt"); args.Add("yuv420p");
        if (request.FrameRateOverride is double frameRate && frameRate > 0)
        {
            args.Add("-r"); args.Add(frameRate.ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (geometry.FilterChain.Count > 0)
        {
            args.Add("-vf"); args.Add(string.Join(",", geometry.FilterChain));
        }

        if (audioIndices.Count > 0)
        {
            AddAudioEncoding(args, request);
        }
        else
        {
            args.Add("-an");
        }

        AddMetadataPolicy(args, request.Metadata);
        AddContainer(args, request.Container, faststart: request.Container is ContainerFormat.Mp4 or ContainerFormat.Mov);
        args.Add(PlanPlaceholders.Output);

        var effectiveCrf = request.CrfOverride ?? CrfFor(request.Quality, request.VideoCodec);
        var rationale = uncalibrated
            ? $"{backendLabel} {encoder}: hardware candidate; quality mapping unverified on this build."
            : (request.VideoBitrateKbps is int vbr && vbr > 0
                ? $"cpu {encoder} {vbr}kbps; {request.Audio.Codec.Name()} audio."
                : $"cpu {encoder} crf {effectiveCrf}; {request.Audio.Codec.Name()} audio.");

        return new ExecutionCandidate
        {
            CandidateId = $"{backendLabel}-{encoder}",
            Backend = backend,
            EncoderName = encoder,
            UsesGpuDecode = usesGpu,
            UsesGpuFilter = false,
            DeviceId = request.Hardware.AdapterId,
            Arguments = args,
            Rationale = rationale,
        };
    }

    // ----------------------------------------------------------------- Audio extract

    private ValidatedPlan BuildAudioExtract(ConversionRequest request, SourceSnapshot source, CapabilitySnapshot capabilities)
    {
        var audio = MediaCompatibility.SelectPrimaryAudio(request, source)
            ?? throw new PlanValidationException(FailureInfo.Of(FailureCode.UnsupportedMedia, "plan.no_audio", "No audio stream selected."));

        var codec = request.Audio.Codec;

        var args = GlobalPrefix(request.Trim?.Start);
        AddInput(args);
        AddTrimDuration(args, request.Trim);
        args.Add("-map"); args.Add($"0:{audio.SourceIndex}");
        args.Add("-vn");
        AddAudioEncoding(args, request);
        AddMetadataPolicy(args, request.Metadata);
        AddContainer(args, request.Container, faststart: request.Container == ContainerFormat.M4a);
        args.Add(PlanPlaceholders.Output);

        var expected = new ExpectedOutput
        {
            Container = request.Container,
            VideoCodec = null,
            AudioCodec = codec,
            Width = null,
            Height = null,
            PixelFormat = null,
            DurationUs = request.Trim is { } audioTrim ? (long)audioTrim.Duration.TotalMicroseconds : source.DurationUs,
            StreamCount = 1,
            VideoStreamCount = 0,
            AudioStreamCount = 1,
            FastStart = request.Container == ContainerFormat.M4a,
        };

        var candidate = new ExecutionCandidate
        {
            CandidateId = $"cpu-{codec.ToString().ToLowerInvariant()}",
            Backend = HardwareBackend.Cpu,
            EncoderName = codec.Encoder(),
            UsesGpuDecode = false,
            UsesGpuFilter = false,
            Arguments = args,
            Rationale = $"audio extract to {request.Container.DisplayName()} ({codec}).",
        };

        var warnings = BuildWarnings(request, source);
        return Assemble(request, source, capabilities, new[] { candidate }, expected, warnings, args.ToList());
    }

    // ----------------------------------------------------------------- Remux

    private ValidatedPlan BuildRemux(ConversionRequest request, SourceSnapshot source, CapabilitySnapshot capabilities)
    {
        var args = GlobalPrefix(null);
        AddInput(args);

        int mapped = 0, videoMapped = 0, audioMapped = 0;
        foreach (var v in source.VideoStreams)
        {
            args.Add("-map"); args.Add($"0:{v.SourceIndex}");
            mapped++; videoMapped++;
        }
        foreach (var a in source.AudioStreams)
        {
            args.Add("-map"); args.Add($"0:{a.SourceIndex}");
            mapped++; audioMapped++;
        }
        foreach (var sub in source.SubtitleStreams)
        {
            if (!sub.IsTextSubtitle) continue;
            if (request.Container is ContainerFormat.Mp4 or ContainerFormat.Mov && sub.Codec != "mov_text") continue;
            args.Add("-map"); args.Add($"0:{sub.SourceIndex}");
            mapped++;
        }

        args.Add("-c"); args.Add("copy");
        AddMetadataPolicy(args, request.Metadata);
        AddContainer(args, request.Container, faststart: request.Container is ContainerFormat.Mp4 or ContainerFormat.Mov);
        args.Add(PlanPlaceholders.Output);

        var expected = new ExpectedOutput
        {
            Container = request.Container,
            VideoCodec = VideoCodec.Copy,
            AudioCodec = audioMapped > 0 ? AudioCodec.Copy : (AudioCodec?)null,
            Width = null,
            Height = null,
            PixelFormat = null,
            DurationUs = source.DurationUs,
            StreamCount = mapped,
            VideoStreamCount = videoMapped,
            AudioStreamCount = audioMapped,
            FastStart = request.Container is ContainerFormat.Mp4 or ContainerFormat.Mov,
        };

        var candidate = new ExecutionCandidate
        {
            CandidateId = "cpu-copy",
            Backend = HardwareBackend.Cpu,
            EncoderName = "copy",
            UsesGpuDecode = false,
            UsesGpuFilter = false,
            Arguments = args,
            Rationale = $"remux to {request.Container.DisplayName()} with stream copy (whitelist enforced by compatibility matrix).",
        };

        var warnings = BuildWarnings(request, source);
        return Assemble(request, source, capabilities, new[] { candidate }, expected, warnings, args.ToList());
    }

    // ----------------------------------------------------------------- Shared helpers

    private static List<string> GlobalPrefix(TimeSpan? seekTo)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-n", "-loglevel", "warning",
            "-nostats", "-stats_period", "0.5", "-progress", "pipe:1",
        };
        if (seekTo is { } start && start > TimeSpan.Zero)
        {
            args.Add("-ss");
            args.Add(FmtSeconds(start));
        }
        return args;
    }

    private static void AddInput(List<string> args)
    {
        args.Add("-protocol_whitelist"); args.Add("file");
        args.Add("-i"); args.Add(PlanPlaceholders.Input);
    }

    private static void AddTrimDuration(List<string> args, TrimRange? trim)
    {
        if (trim is { } t)
        {
            args.Add("-t");
            args.Add(FmtSeconds(t.Duration));
        }
    }

    private static void AddMetadataPolicy(List<string> args, MetadataPolicy policy)
    {
        if (policy == MetadataPolicy.StripPersonalTags)
        {
            args.Add("-map_metadata"); args.Add("-1");
            args.Add("-map_metadata:s"); args.Add("-1");
            args.Add("-map_chapters"); args.Add("-1");
        }
        else
        {
            args.Add("-map_metadata"); args.Add("0");
            args.Add("-map_chapters"); args.Add("0");
        }
    }

    private static void AddContainer(List<string> args, ContainerFormat container, bool faststart)
    {
        switch (container)
        {
            case ContainerFormat.Mp4:
                if (faststart) { args.Add("-movflags"); args.Add("+faststart"); }
                args.Add("-f"); args.Add("mp4");
                break;
            case ContainerFormat.Mkv:
                args.Add("-f"); args.Add("matroska");
                break;
            case ContainerFormat.M4a:
                if (faststart) { args.Add("-movflags"); args.Add("+faststart"); }
                args.Add("-f"); args.Add("ipod");
                break;
            case ContainerFormat.Flac:
                args.Add("-f"); args.Add("flac");
                break;
            case ContainerFormat.Wav:
                args.Add("-f"); args.Add("wav");
                break;
            case ContainerFormat.Mov:
                if (faststart) { args.Add("-movflags"); args.Add("+faststart"); }
                args.Add("-f"); args.Add("mov");
                break;
            case ContainerFormat.Webm:
                args.Add("-f"); args.Add("webm"); break;
            case ContainerFormat.Mp3:
                args.Add("-f"); args.Add("mp3"); break;
            case ContainerFormat.Ogg:
                args.Add("-f"); args.Add("ogg"); break;
            case ContainerFormat.Opus:
                args.Add("-f"); args.Add("opus"); break;
            case ContainerFormat.Aac:
                args.Add("-f"); args.Add("adts"); break;
            default:
                throw new DomainException($"Unknown container {container}.");
        }
    }

    private static void AddVideoQuality(List<string> args, string encoder, ConversionRequest request)
    {
        var crf = request.CrfOverride ?? CrfFor(request.Quality, request.VideoCodec);
        var bitrate = request.VideoBitrateKbps;
        switch (encoder)
        {
            case "libx265":
                args.Add("-preset"); args.Add(PresetFor(request.Speed));
                args.Add("-x265-params"); args.Add("pools=4:frame-threads=2:log-level=error");
                if (bitrate is > 0) { args.Add("-b:v"); args.Add($"{bitrate}k"); }
                else { args.Add("-crf"); args.Add(crf.ToString(CultureInfo.InvariantCulture)); }
                break;
            case "libaom-av1":
            case "libvpx-vp9":
                args.Add("-threads"); args.Add("4");
                args.Add("-row-mt"); args.Add("1");
                args.Add("-cpu-used"); args.Add(request.Speed switch { SpeedTier.Faster => "8", SpeedTier.Slower => "4", _ => "6" });
                args.Add("-b:v"); args.Add(bitrate is > 0 ? $"{bitrate}k" : "0");
                if (bitrate is not > 0) { args.Add("-crf"); args.Add(crf.ToString(CultureInfo.InvariantCulture)); }
                break;
            case "libx264":
                args.Add("-preset"); args.Add(PresetFor(request.Speed));
                if (bitrate is int b1 && b1 > 0) { args.Add("-b:v"); args.Add($"{b1}k"); }
                else { args.Add("-crf"); args.Add(crf.ToString(CultureInfo.InvariantCulture)); }
                break;
            case "h264_nvenc":
                args.Add("-preset"); args.Add(request.Speed switch { SpeedTier.Faster => "p2", SpeedTier.Slower => "p6", _ => "p4" });
                args.Add("-rc"); args.Add("vbr");
                if (bitrate is int b2 && b2 > 0) { args.Add("-b:v"); args.Add($"{b2}k"); }
                else { args.Add("-cq"); args.Add(crf.ToString(CultureInfo.InvariantCulture)); }
                break;
            case "h264_qsv":
                args.Add("-preset"); args.Add(request.Speed switch { SpeedTier.Faster => "fast", SpeedTier.Slower => "slow", _ => "medium" });
                if (bitrate is int b3 && b3 > 0) { args.Add("-b:v"); args.Add($"{b3}k"); }
                else { args.Add("-global_quality"); args.Add(crf.ToString(CultureInfo.InvariantCulture)); }
                break;
            case "h264_amf":
                args.Add("-quality"); args.Add(request.Speed switch { SpeedTier.Faster => "speed", SpeedTier.Slower => "quality", _ => "balanced" });
                if (bitrate is int b4 && b4 > 0) { args.Add("-rc"); args.Add("cbr"); args.Add("-b:v"); args.Add($"{b4}k"); }
                else { args.Add("-rc"); args.Add("cqp"); args.Add("-qp_i"); args.Add(crf.ToString(CultureInfo.InvariantCulture)); args.Add("-qp_p"); args.Add(crf.ToString(CultureInfo.InvariantCulture)); }
                break;
            default:
                throw new DomainException($"Unsupported video encoder '{encoder}'.");
        }
    }

    private static int CrfFor(QualityTier quality, VideoCodec codec) => (codec, quality) switch
    {
        (VideoCodec.Hevc, QualityTier.SmallerSize) => 30,
        (VideoCodec.Hevc, QualityTier.HigherQuality) => 22,
        (VideoCodec.Hevc, _) => 26,
        (VideoCodec.Av1 or VideoCodec.Vp9, QualityTier.SmallerSize) => 40,
        (VideoCodec.Av1 or VideoCodec.Vp9, QualityTier.HigherQuality) => 28,
        (VideoCodec.Av1 or VideoCodec.Vp9, _) => 34,
        (_, QualityTier.SmallerSize) => 26,
        (_, QualityTier.HigherQuality) => 20,
        _ => 23,
    };

    private static void AddAudioEncoding(List<string> args, ConversionRequest request)
    {
        var audio = request.Audio;
        args.Add("-c:a"); args.Add(audio.Codec.Encoder());
        if (audio.Codec.HasBitrate())
        { args.Add("-b:a"); args.Add($"{audio.BitRateKbps ?? 192}k"); }
        var sampleRate = audio.SampleRate ?? (audio.Codec == AudioCodec.Opus ? 48000 : (int?)null);
        if (sampleRate is > 0) { args.Add("-ar"); args.Add(sampleRate.Value.ToString(CultureInfo.InvariantCulture)); }
        if (audio.Channels is > 0) { args.Add("-ac"); args.Add(audio.Channels.Value.ToString(CultureInfo.InvariantCulture)); }
        if (audio.Codec == AudioCodec.Opus) { args.Add("-mapping_family"); args.Add("1"); }
    }

    private static string PresetFor(SpeedTier speed) => speed switch
    {
        SpeedTier.Faster => "veryfast",
        SpeedTier.Slower => "slow",
        _ => "medium",
    };

    private static HardwareBackend MapBackend(string backend) => backend?.ToLowerInvariant() switch
    {
        "nvidia" => HardwareBackend.NvidiaNvenc,
        "intel" => HardwareBackend.IntelQsv,
        "amd" => HardwareBackend.AmdAmf,
        _ => HardwareBackend.Cpu,
    };

    private static List<int> SelectAudioIndices(ConversionRequest request, SourceSnapshot source, StreamDescriptor? primaryAudio)
    {
        var selected = new List<int>();
        foreach (var index in request.SelectedStreamIndices)
        {
            var stream = source.Streams.FirstOrDefault(s => s.SourceIndex == index);
            if (stream is { Kind: MediaKind.Audio }) selected.Add(index);
        }
        if (selected.Count == 0 && primaryAudio is not null) selected.Add(primaryAudio.SourceIndex);
        if (request.Container == ContainerFormat.Mp4 && selected.Count > 1)
            selected = selected.Take(1).ToList(); // MP4 first-release default: single audio track
        return selected;
    }

    private static ExpectedOutput BuildVideoExpectedOutput(ConversionRequest request, SourceSnapshot source, Geometry geometry, int audioCount)
    {
        long? durationUs = request.Trim is { } trim
            ? (long)trim.Duration.TotalMicroseconds
            : source.DurationUs;
        return new ExpectedOutput
        {
            Container = request.Container,
            VideoCodec = request.VideoCodec,
            AudioCodec = audioCount > 0 ? request.Audio.Codec : (AudioCodec?)null,
            Width = geometry.TargetWidth,
            Height = geometry.TargetHeight,
            PixelFormat = "yuv420p",
            DurationUs = durationUs,
            StreamCount = 1 + audioCount,
            VideoStreamCount = 1,
            AudioStreamCount = audioCount,
            FastStart = request.Container is ContainerFormat.Mp4 or ContainerFormat.Mov,
        };
    }

    private static List<string> BuildWarnings(ConversionRequest request, SourceSnapshot source)
    {
        var warnings = new List<string>();
        if (request.Mode == ConversionMode.VideoTranscode && source.SubtitleStreams.Any())
            warnings.Add("Subtitles are not included in the first-release video transcode output.");
        if (request.Metadata == MetadataPolicy.StripPersonalTags)
            warnings.Add("Free-form metadata and chapters are stripped.");
        else
            warnings.Add("Title/chapters preserved; metadata may contain private tags.");
        if (request.Mode == ConversionMode.VideoTranscode && source.ColorClassification != ColorClassification.SdrBt709)
            warnings.Add($"Source colour classified as {source.ColorClassification}.");
        return warnings;
    }

    private ValidatedPlan Assemble(
        ConversionRequest request,
        SourceSnapshot source,
        CapabilitySnapshot capabilities,
        IReadOnlyList<ExecutionCandidate> candidates,
        ExpectedOutput expected,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> parameterPlan)
    {
        var requestHash = CanonicalJson.Hash(request);
        var candidateShape = candidates.Select(c => new
        {
            c.CandidateId,
            c.Backend,
            c.EncoderName,
            c.UsesGpuDecode,
            c.UsesGpuFilter,
            c.DeviceId,
            c.Arguments,
        });
        var planHash = CanonicalJson.Hash(new
        {
            schema = 1,
            requestHash,
            coreHash = _core.CoreHash,
            capabilityHash = capabilities.CapabilityHash,
            candidates = candidateShape,
            expected,
            warnings,
        });

        return new ValidatedPlan
        {
            PlanId = Guid.NewGuid(),
            PlanSchemaVersion = 1,
            RequestHash = requestHash,
            CoreHash = _core.CoreHash,
            CapabilityHash = capabilities.CapabilityHash,
            CandidatePaths = candidates,
            ExpectedOutput = expected,
            Warnings = warnings,
            ParameterPlan = parameterPlan,
            ResourceNeeds = new ResourceNeeds
            {
                NeedsGpuEncoder = candidates.Any(c => c.Backend != HardwareBackend.Cpu),
                AdapterId = request.Hardware.AdapterId,
                NeedsCpuHeavy = candidates.Any(c => c.Backend == HardwareBackend.Cpu),
                NeedsVerification = true,
            },
            PlanHash = planHash,
        };
    }

    private static string FmtSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static int EvenFloor(int value)
    {
        if (value < 2) return 2;
        return value - (value & 1);
    }

    private Geometry ComputeGeometry(StreamDescriptor video, ResizeSpec resize)
    {
        var srcW = video.Width ?? 0;
        var srcH = video.Height ?? 0;
        var sar = video.SampleAspectRatio?.ToDouble() ?? 1.0;
        if (sar <= 0 || double.IsNaN(sar)) sar = 1.0;

        var dispW = (int)Math.Round(srcW * sar);
        var dispH = srcH;

        var rotation = video.RotationDegrees ?? 0;
        var swap = rotation == 90 || rotation == 270;
        var orientW = swap ? dispH : dispW;
        var orientH = swap ? dispW : dispH;

        var targetW = orientW;
        var targetH = orientH;
        if (resize.TargetWidth is int tw && resize.TargetHeight is int th && tw > 0 && th > 0)
        {
            targetW = tw; targetH = th;
        }
        else if (resize.TargetWidth is int tw2 && tw2 > 0)
        {
            targetW = tw2; targetH = (int)Math.Round(orientH * (double)tw2 / Math.Max(1, orientW));
        }
        else if (resize.TargetHeight is int th2 && th2 > 0)
        {
            targetH = th2; targetW = (int)Math.Round(orientW * (double)th2 / Math.Max(1, orientH));
        }
        else if (resize.Mode == ResizeMode.Max1080p)
        {
            var scale = Math.Min(1.0, Math.Min(1920.0 / Math.Max(1, orientW), 1080.0 / Math.Max(1, orientH)));
            targetW = (int)Math.Round(orientW * scale);
            targetH = (int)Math.Round(orientH * scale);
        }
        targetW = EvenFloor(targetW);
        targetH = EvenFloor(targetH);

        // ffmpeg's default autorotate physically rotates decoded frames using the display
        // matrix and drops the rotation metadata, so we only need a scale/setsar here.
        var filters = new List<string>();
        var rotationSwaps = rotation is 90 or 270;
        var needScale = rotationSwaps || targetW != srcW || targetH != srcH;
        if (needScale)
        {
            filters.Add($"scale={targetW}:{targetH}:flags=bicubic");
        }
        if (Math.Abs(sar - 1.0) > 1e-6)
        {
            filters.Add("setsar=1");
        }

        return new Geometry(targetW, targetH, filters);
    }

    internal sealed record Geometry(int TargetWidth, int TargetHeight, List<string> FilterChain);

    private sealed class PlanValidationException : Exception
    {
        public FailureInfo Failure { get; }
        public PlanValidationException(FailureInfo failure) : base(failure.TechnicalDetail)
        {
            Failure = failure;
        }
    }
}
