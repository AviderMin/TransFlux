using System.Globalization;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Common;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;
using TransFlux.Engine.FFmpeg.Core;
using TransFlux.Engine.FFmpeg.Capabilities;

namespace TransFlux.Engine.FFmpeg.Planner;

/// <summary>
/// Compiles a validated plan into typed ffmpeg argument lists. Never concatenates user text into
/// filters; the runner substitutes <see cref="PlanPlaceholders.Input"/>/<see cref="PlanPlaceholders.Output"/>.
/// Emits the selected software codec and, for H.264, verified hardware candidates from the capability snapshot.
/// </summary>
public sealed class FfmpegPlanner : IExecutionPlanner
{
    private static readonly string[] H264HardwareEncoders = { "h264_nvenc", "h264_qsv", "h264_amf" };
    private const int CurrentPlanSchemaVersion = 2;

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
        var selection = StreamSelectionResolver.Resolve(request, source);
        var video = selection.Video
            ?? throw new PlanValidationException(FailureInfo.Of(FailureCode.UnsupportedMedia, "plan.no_video", "No video stream selected."));
        if (video.Width is not int w || video.Height is not int h || w <= 0 || h <= 0)
            throw new PlanValidationException(FailureInfo.Of(FailureCode.PlanInvalid, "plan.unknown_dimensions", "Video dimensions are unknown."));

        var audioStreams = selection.Audio.ToList();
        var subtitles = SubtitlePolicy.SelectForContainer(request.Container, selection.Subtitles);
        // Matroska is the only target that can carry font/attachment streams; keeping them is
        // required for ASS/SSA styling to render correctly after a video transcode.
        var attachments = request.Container == ContainerFormat.Mkv
            ? source.Streams.Where(st => st.Kind == MediaKind.Attachment).ToList()
            : new List<StreamDescriptor>();
        var geometry = ComputeGeometry(video, request.Resize);
        var expected = BuildVideoExpectedOutput(request, source, geometry, video, audioStreams, subtitles.Included, attachments);

        var warnings = BuildWarnings(request, source, selection, subtitles);
        var candidates = new List<ExecutionCandidate>();

        var cpuEncoder = request.VideoCodec switch
        {
            VideoCodec.H264 => "libx264", VideoCodec.Hevc => "libx265", VideoCodec.Av1 => "libaom-av1", VideoCodec.Vp9 => "libvpx-vp9",
            _ => throw new DomainException("Unsupported video codec."),
        };
        var cpu = BuildVideoCandidate(request, video, audioStreams, subtitles.Included, attachments, geometry, HardwareBackend.Cpu, cpuEncoder, "cpu", uncalibrated: false);
        candidates.Add(cpu);

        var requested = request.Hardware.Backend;
        var hw = capabilities.Candidates
            .Where(c => request.VideoCodec == VideoCodec.H264 && c.Available && c.TrialCompleted && c.Direct3DAdapterIndex is >= 0 && H264HardwareEncoders.Contains(c.EncoderName))
            .Where(c => request.Hardware.EncoderName is null || string.Equals(c.EncoderName, request.Hardware.EncoderName, StringComparison.OrdinalIgnoreCase))
            .Where(c => requested == HardwareBackend.Auto || MapBackend(c.Backend) == requested)
            .Where(c => request.Hardware.AdapterId is null || c.DeviceId == request.Hardware.AdapterId)
            .ToList();

        foreach (var cap in hw)
        {
            var backend = MapBackend(cap.Backend);
            candidates.Add(BuildVideoCandidate(request, video, audioStreams, subtitles.Included, attachments, geometry, backend, cap.EncoderName, cap.Backend,
                uncalibrated: true, cap.Direct3DAdapterIndex, cap.DeviceId));
        }

        if (requested is HardwareBackend.NvidiaNvenc or HardwareBackend.IntelQsv or HardwareBackend.AmdAmf)
        {
            if (!candidates.Any(c => c.Backend == requested))
                throw new PlanValidationException(FailureInfo.Of(
                    FailureCode.DeviceUnavailable, "plan.device_unavailable",
                    $"Requested hardware backend {requested} has no verified encoder in the capability snapshot."));
            // A manually locked backend must never silently fall back to another device/CPU.
            candidates = [candidates.First(c => c.Backend == requested)];
        }
        else if (requested == HardwareBackend.Auto && candidates.Any(c => c.Backend != HardwareBackend.Cpu))
        {
            candidates = [candidates.First(c => c.Backend != HardwareBackend.Cpu), cpu];
        }
        else
        {
            candidates = [cpu];
        }

        var parameterPlan = candidates[0].Arguments.ToList();
        return Assemble(request, source, capabilities, candidates, expected, warnings, parameterPlan);
    }

    private static ExecutionCandidate BuildVideoCandidate(
        ConversionRequest request,
        StreamDescriptor video,
        IReadOnlyList<StreamDescriptor> audioStreams,
        IReadOnlyList<SubtitlePolicy.SubtitleTarget> subtitles,
        IReadOnlyList<StreamDescriptor> attachments,
        Geometry geometry,
        HardwareBackend backend,
        string encoder,
        string backendLabel,
        bool uncalibrated,
        int? adapterIndex = null,
        string? deviceId = null)
    {
        var args = GlobalPrefix(request.Trim?.Start);
        HardwareEncoding.AddDevice(args, encoder, adapterIndex);
        AddInput(args);
        AddTrimDuration(args, request.Trim);

        args.Add("-map"); args.Add($"0:{video.SourceIndex}");
        foreach (var audio in audioStreams)
        {
            args.Add("-map"); args.Add($"0:{audio.SourceIndex}");
        }
        foreach (var sub in subtitles)
        {
            args.Add("-map"); args.Add($"0:{sub.Stream.SourceIndex}");
        }
        foreach (var attachment in attachments)
        {
            args.Add("-map"); args.Add($"0:{attachment.SourceIndex}");
        }

        args.Add("-c:v"); args.Add(encoder);
        AddVideoQuality(args, encoder, request);
        if (request.VideoCodec == VideoCodec.Hevc && request.Container is ContainerFormat.Mp4 or ContainerFormat.Mov)
        { args.Add("-tag:v"); args.Add("hvc1"); }
        if (adapterIndex is null) { args.Add("-pix_fmt"); args.Add("yuv420p"); }
        AddColorOutputArgs(args, video);
        if (request.FrameRateOverride is double frameRate && frameRate > 0)
        {
            args.Add("-r"); args.Add(frameRate.ToString("0.###", CultureInfo.InvariantCulture));
        }

        var filters = geometry.FilterChain.ToList();
        var setparams = SetparamsFilter(video);
        if (setparams is not null) filters.Add(setparams);
        if (adapterIndex is not null) filters.Add(HardwareEncoding.UploadFilter(encoder));
        if (filters.Count > 0)
        {
            args.Add("-vf"); args.Add(string.Join(",", filters));
        }

        if (audioStreams.Count > 0)
        {
            AddAudioEncoding(args, request);
        }
        else
        {
            args.Add("-an");
        }

        for (var i = 0; i < subtitles.Count; i++)
        {
            args.Add($"-c:s:{i}"); args.Add(subtitles[i].Encoder);
        }
        if (attachments.Count > 0)
        {
            args.Add("-c:t"); args.Add("copy");
            AddAttachmentIdentity(args, attachments);
        }

        AddStreamIdentity(args, audioStreams, subtitles.Select(s => s.Stream).ToList());
        AddMetadataPolicy(args, request.Metadata, dropChapters: request.Trim is not null);
        AddContainer(args, request.Container, faststart: request.Container is ContainerFormat.Mp4 or ContainerFormat.Mov);
        args.Add(PlanPlaceholders.Output);

        var effectiveCrf = request.CrfOverride ?? CrfFor(request.Quality, request.VideoCodec);
        // Audio Copy is a stream copy, not an encode: never claim an audio encoder in the
        // candidate rationale.
        var audioSummary = request.Audio.Codec == AudioCodec.Copy
            ? "audio copy"
            : $"{request.Audio.Codec.Name()} audio";
        var rationale = uncalibrated
            ? $"{backendLabel} {encoder}: hardware candidate; quality mapping unverified on this build."
            : (request.VideoBitrateKbps is int vbr && vbr > 0
                ? $"cpu {encoder} {vbr}kbps; {audioSummary}."
                : $"cpu {encoder} crf {effectiveCrf}; {audioSummary}.");

        return new ExecutionCandidate
        {
            CandidateId = $"{backendLabel}-{encoder}" + (deviceId is null ? "" : ":" + deviceId),
            Backend = backend,
            EncoderName = encoder,
            // Decode is always software in the current pipeline; only hardware candidates
            // run GPU filters (format=nv12,hwupload).
            UsesGpuDecode = false,
            UsesGpuFilter = adapterIndex is not null,
            DeviceId = deviceId,
            Arguments = args,
            Rationale = rationale,
        };
    }

    // ----------------------------------------------------------------- Audio extract

    private ValidatedPlan BuildAudioExtract(ConversionRequest request, SourceSnapshot source, CapabilitySnapshot capabilities)
    {
        var selection = StreamSelectionResolver.Resolve(request, source);
        if (request.RequestVersion >= 2 && request.AudioTracks.Mode == AudioSelectionMode.Explicit
            && selection.Audio.Count > 1)
        {
            // Defensive second guard: compatibility validation must already block this, but a
            // plan must never silently drop extra explicitly selected tracks.
            throw new PlanValidationException(FailureInfo.Of(FailureCode.UnsupportedMedia, "plan.audio_extract_single",
                "音频提取一次只输出一条音轨，请只选择一条源音轨。"));
        }
        var audio = request.RequestVersion >= 2
            ? (request.AudioTracks.Mode == AudioSelectionMode.Auto ? source.PrimaryAudio : selection.Audio.FirstOrDefault())
            : MediaCompatibility.SelectPrimaryAudio(request, source);
        if (audio is null)
            throw new PlanValidationException(FailureInfo.Of(FailureCode.UnsupportedMedia, "plan.no_audio", "No audio stream selected."));

        var codec = request.Audio.Codec;

        var args = GlobalPrefix(request.Trim?.Start);
        AddInput(args);
        AddTrimDuration(args, request.Trim);
        args.Add("-map"); args.Add($"0:{audio.SourceIndex}");
        args.Add("-vn");
        AddAudioEncoding(args, request);
        AddStreamIdentity(args, new[] { audio }, Array.Empty<StreamDescriptor>());
        AddMetadataPolicy(args, request.Metadata, dropChapters: request.Trim is not null);
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
            AttachmentStreamCount = 0,
            FastStart = request.Container == ContainerFormat.M4a,
            AudioLanguages = new[] { audio.Language ?? string.Empty },
            TrackSignatures = new[] { AudioSignature(audio, codec.ProbeName(), request.Audio.Channels, assertDispositions: false) },
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

        var warnings = BuildWarnings(request, source, selection,
            new SubtitlePolicy.SubtitleSelection(Array.Empty<SubtitlePolicy.SubtitleTarget>(), Array.Empty<string>()));
        return Assemble(request, source, capabilities, new[] { candidate }, expected, warnings, args.ToList());
    }

    // ----------------------------------------------------------------- Remux

    private ValidatedPlan BuildRemux(ConversionRequest request, SourceSnapshot source, CapabilitySnapshot capabilities)
    {
        var args = GlobalPrefix(null);
        AddInput(args);

        int mapped = 0, videoMapped = 0, audioMapped = 0, subtitleMapped = 0, attachmentMapped = 0;
        var selection = StreamSelectionResolver.Resolve(request, source);
        var selectedStreams = MediaCompatibility.SelectRemuxStreams(request, source);
        var mappedVideos = new List<StreamDescriptor>();
        var mappedAudio = new List<StreamDescriptor>();
        var mappedSubtitles = new List<StreamDescriptor>();
        var mappedAttachments = new List<StreamDescriptor>();
        foreach (var stream in selectedStreams)
        {
            args.Add("-map"); args.Add($"0:{stream.SourceIndex}");
            mapped++;
            if (stream.Kind == MediaKind.Video) { videoMapped++; mappedVideos.Add(stream); }
            if (stream.Kind == MediaKind.Audio) { audioMapped++; mappedAudio.Add(stream); }
            if (stream.Kind == MediaKind.Subtitle) { subtitleMapped++; mappedSubtitles.Add(stream); }
            if (stream.Kind == MediaKind.Attachment) { attachmentMapped++; mappedAttachments.Add(stream); }
        }

        args.Add("-c"); args.Add("copy");
        AddStreamIdentity(args, mappedAudio, mappedSubtitles);
        AddMetadataPolicy(args, request.Metadata, dropChapters: request.Trim is not null);
        AddAttachmentIdentity(args, mappedAttachments);
        AddContainer(args, request.Container, faststart: request.Container is ContainerFormat.Mp4 or ContainerFormat.Mov);
        args.Add(PlanPlaceholders.Output);

        // Dolby Vision remux must prove the DOVI configuration record survives the copy;
        // the expectation is taken from the selected source video (never assumed).
        var dolbyVisionVideo = mappedVideos.FirstOrDefault(v => v.DvProfile is not null);
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
            AttachmentStreamCount = attachmentMapped,
            FastStart = request.Container is ContainerFormat.Mp4 or ContainerFormat.Mov,
            AudioLanguages = mappedAudio.Select(a => a.Language ?? string.Empty).ToList(),
            SubtitleLanguages = mappedSubtitles.Select(s => s.Language ?? string.Empty).ToList(),
            TrackSignatures = selectedStreams
                .Where(st => st.Kind is MediaKind.Video or MediaKind.Audio && !st.IsAttachedPicture)
                .Select(st => st.Kind == MediaKind.Video
                    ? VideoSignature(st.Codec)
                    : AudioSignature(st, st.Codec, outputChannels: null,
                        assertDispositions: request.Container is ContainerFormat.Mkv or ContainerFormat.Webm))
                .ToList(),
            DolbyVision = dolbyVisionVideo is { DvProfile: int dvProfile }
                ? new DolbyVisionExpectation
                {
                    Profile = dvProfile,
                    BlSignalCompatibilityId = dolbyVisionVideo.DvBlSignalCompatibilityId,
                    RpuRequired = dolbyVisionVideo.DvRpuPresent == true,
                    ElRequired = dolbyVisionVideo.DvElPresent == true,
                    BlRequired = dolbyVisionVideo.DvBlPresent == true,
                }
                : null,
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

        var warnings = BuildWarnings(request, source, selection,
            SubtitlePolicy.SelectForContainer(request.Container, selection.Subtitles));
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

    /// <param name="dropChapters">True when the requested trim makes original chapter timestamps
    /// meaningless; chapters are then removed instead of copied onto a shifted timeline.</param>
    private static void AddMetadataPolicy(List<string> args, MetadataPolicy policy, bool dropChapters)
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
            args.Add("-map_chapters"); args.Add(dropChapters ? "-1" : "0");
        }
    }

    /// <summary>
    /// Rebuilds per-track language and the complete disposition mask that global metadata
    /// stripping would otherwise lose (docs/05: language and disposition survive every
    /// metadata policy). Every flag is emitted, not just default/forced: accessibility and
    /// commentary markers (hearing_impaired, visual_impaired, comment, lyrics...) are user
    /// intent, and emitting a partial mask would silently clear them.
    /// </summary>
    private static void AddStreamIdentity(List<string> args, IReadOnlyList<StreamDescriptor> audio, IReadOnlyList<StreamDescriptor> subtitles)
    {
        for (var i = 0; i < audio.Count; i++) AddOneStreamIdentity(args, "a", i, audio[i]);
        for (var i = 0; i < subtitles.Count; i++) AddOneStreamIdentity(args, "s", i, subtitles[i]);
    }

    /// <summary>Full disposition flag list in FFmpeg's canonical order (attached_pic excluded:
    /// cover art is never mapped as audio/subtitle and is handled as a stream kind).</summary>
    private static IReadOnlyList<string> DispositionFlags(StreamDescriptor stream)
    {
        var flags = new List<string>();
        if (stream.IsDefault) flags.Add("default");
        if (stream.IsDub) flags.Add("dub");
        if (stream.IsOriginal) flags.Add("original");
        if (stream.IsComment) flags.Add("comment");
        if (stream.IsLyrics) flags.Add("lyrics");
        if (stream.IsKaraoke) flags.Add("karaoke");
        if (stream.IsForced) flags.Add("forced");
        if (stream.IsHearingImpaired) flags.Add("hearing_impaired");
        if (stream.IsVisualImpaired) flags.Add("visual_impaired");
        if (stream.IsCleanEffects) flags.Add("clean_effects");
        if (stream.IsTimedThumbnails) flags.Add("timed_thumbnails");
        if (stream.IsNonDiegetic) flags.Add("non_diegetic");
        if (stream.IsCaptions) flags.Add("captions");
        if (stream.IsDescriptions) flags.Add("descriptions");
        if (stream.IsMetadata) flags.Add("metadata");
        if (stream.IsDependent) flags.Add("dependent");
        if (stream.IsStillImage) flags.Add("still_image");
        if (stream.IsMultilayer) flags.Add("multilayer");
        return flags;
    }

    private static void AddOneStreamIdentity(List<string> args, string kind, int index, StreamDescriptor stream)
    {
        if (!string.IsNullOrEmpty(stream.Language) && stream.Language != "und")
        {
            args.Add($"-metadata:s:{kind}:{index}");
            args.Add("language=" + stream.Language);
        }
        var flags = DispositionFlags(stream);
        args.Add($"-disposition:{kind}:{index}");
        args.Add(flags.Count == 0 ? "0" : string.Join("+", flags));
    }

    /// <summary>
    /// Attachment filename/mimetype are structural for Matroska attachments (fonts);
    /// rebuild them because metadata stripping removes the original stream tags.
    /// </summary>
    private static void AddAttachmentIdentity(List<string> args, IReadOnlyList<StreamDescriptor> attachments)
    {
        for (var i = 0; i < attachments.Count; i++)
        {
            var att = attachments[i];
            if (!string.IsNullOrEmpty(att.AttachmentFileName))
            {
                args.Add($"-metadata:s:t:{i}"); args.Add("filename=" + att.AttachmentFileName);
            }
            if (!string.IsNullOrEmpty(att.AttachmentMimeType))
            {
                args.Add($"-metadata:s:t:{i}"); args.Add("mimetype=" + att.AttachmentMimeType);
            }
        }
    }

    private static readonly string[] UnusableColourValues = { "unknown", "unspecified", "reserved", "n/a" };

    private static bool UsableColour(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (UnusableColourValues.Contains(v, StringComparer.OrdinalIgnoreCase)) return false;
        // Colour values are ffprobe enum tokens; keep them inert inside filter strings.
        foreach (var c in v)
        {
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_')) return false;
        }
        return v.Length <= 32;
    }

    /// <summary>
    /// Standard output-side colour options. Kept for portability; on the pinned FFmpeg
    /// 8.0.1 core only -colorspace/-color_range survive this route, so the verified carrier
    /// is the setparams segment (see SetparamsFilter).
    /// </summary>
    private static void AddColorOutputArgs(List<string> args, StreamDescriptor video)
    {
        if (UsableColour(video.ColorPrimaries)) { args.Add("-color_primaries"); args.Add(video.ColorPrimaries!); }
        if (UsableColour(video.ColorTransfer)) { args.Add("-color_trc"); args.Add(video.ColorTransfer!); }
        if (UsableColour(video.ColorSpace)) { args.Add("-colorspace"); args.Add(video.ColorSpace!); }
        if (UsableColour(video.ColorRange)) { args.Add("-color_range"); args.Add(video.ColorRange!); }
    }

    /// <summary>
    /// Colour passthrough verified end to end on the pinned core: frame colour properties
    /// set by setparams reach the encoder VUI/colour metadata for libx264, libx265,
    /// libvpx-vp9, libaom-av1 and h264_nvenc alike.
    /// </summary>
    private static string? SetparamsFilter(StreamDescriptor video)
    {
        var parts = new List<string>();
        if (UsableColour(video.ColorPrimaries)) parts.Add("color_primaries=" + video.ColorPrimaries);
        if (UsableColour(video.ColorTransfer)) parts.Add("color_trc=" + video.ColorTransfer);
        if (UsableColour(video.ColorSpace)) parts.Add("colorspace=" + video.ColorSpace);
        if (UsableColour(video.ColorRange)) parts.Add("range=" + video.ColorRange);
        return parts.Count == 0 ? null : "setparams=" + string.Join(":", parts);
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
                // Thread pools stay with x265 defaults so small machines are not oversubscribed.
                args.Add("-x265-params"); args.Add("log-level=error");
                if (bitrate is > 0) { args.Add("-b:v"); args.Add($"{bitrate}k"); }
                else { args.Add("-crf"); args.Add(crf.ToString(CultureInfo.InvariantCulture)); }
                break;
            case "libaom-av1":
                args.Add("-threads"); args.Add("4");
                args.Add("-row-mt"); args.Add("1");
                args.Add("-cpu-used"); args.Add(request.Speed switch { SpeedTier.Faster => "8", SpeedTier.Slower => "4", _ => "6" });
                args.Add("-b:v"); args.Add(bitrate is > 0 ? $"{bitrate}k" : "0");
                if (bitrate is not > 0) { args.Add("-crf"); args.Add(crf.ToString(CultureInfo.InvariantCulture)); }
                break;
            case "libvpx-vp9":
                args.Add("-threads"); args.Add("4");
                args.Add("-row-mt"); args.Add("1");
                // libvpx good-deadline cpu-used tops out at 5; never share the aom mapping.
                args.Add("-cpu-used"); args.Add(request.Speed switch { SpeedTier.Faster => "5", SpeedTier.Slower => "2", _ => "4" });
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
                if (bitrate is int b2 && b2 > 0)
                {
                    args.Add("-rc"); args.Add("vbr");
                    args.Add("-b:v"); args.Add($"{b2}k");
                }
                else
                {
                    // constqp maps quality tiers to a fixed QP; "vbr -cq" without a target
                    // bitrate leaves the rate target undefined on the pinned core.
                    args.Add("-rc"); args.Add("constqp");
                    args.Add("-qp"); args.Add(crf.ToString(CultureInfo.InvariantCulture));
                }
                break;
            case "h264_qsv":
                args.Add("-preset"); args.Add(request.Speed switch { SpeedTier.Faster => "fast", SpeedTier.Slower => "slow", _ => "medium" });
                if (bitrate is int b3 && b3 > 0) { args.Add("-b:v"); args.Add($"{b3}k"); }
                else { args.Add("-global_quality"); args.Add(crf.ToString(CultureInfo.InvariantCulture)); }
                break;
            case "h264_amf":
                args.Add("-quality"); args.Add(request.Speed switch { SpeedTier.Faster => "speed", SpeedTier.Slower => "quality", _ => "balanced" });
                if (bitrate is int b4 && b4 > 0) { args.Add("-rc"); args.Add("cbr"); args.Add("-b:v"); args.Add($"{b4}k"); }
                else
                {
                    args.Add("-rc"); args.Add("cqp");
                    args.Add("-qp_i"); args.Add(crf.ToString(CultureInfo.InvariantCulture));
                    args.Add("-qp_p"); args.Add(crf.ToString(CultureInfo.InvariantCulture));
                    args.Add("-qp_b"); args.Add(crf.ToString(CultureInfo.InvariantCulture));
                }
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
        if (audio.Codec == AudioCodec.Copy)
        {
            // G2 audio-copy slice: stream-copy the mapped source tracks untouched. Bitrate,
            // sample-rate, channel-count and codec-specific options must not be emitted.
            args.Add("-c:a"); args.Add("copy");
            return;
        }
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

    private static ExpectedOutput BuildVideoExpectedOutput(ConversionRequest request, SourceSnapshot source, Geometry geometry,
        StreamDescriptor video, IReadOnlyList<StreamDescriptor> audioStreams, IReadOnlyList<SubtitlePolicy.SubtitleTarget> subtitles,
        IReadOnlyList<StreamDescriptor> attachments)
    {
        long? durationUs = request.Trim is { } trim
            ? (long)trim.Duration.TotalMicroseconds
            : source.DurationUs;
        // Copy preserves each source track's codec and channel layout; never expect the
        // request-level encode codec or requested channel count in that case.
        var audioCopy = request.Audio.Codec == AudioCodec.Copy;
        var audioCodecName = audioCopy ? null : request.Audio.Codec.ProbeName();
        return new ExpectedOutput
        {
            Container = request.Container,
            VideoCodec = request.VideoCodec,
            AudioCodec = audioStreams.Count > 0 ? request.Audio.Codec : (AudioCodec?)null,
            Width = geometry.TargetWidth,
            Height = geometry.TargetHeight,
            PixelFormat = "yuv420p",
            DurationUs = durationUs,
            StreamCount = 1 + audioStreams.Count + subtitles.Count + attachments.Count,
            VideoStreamCount = 1,
            AudioStreamCount = audioStreams.Count,
            AttachmentStreamCount = attachments.Count,
            FastStart = request.Container is ContainerFormat.Mp4 or ContainerFormat.Mov,
            ColorPrimaries = UsableColour(video.ColorPrimaries) ? video.ColorPrimaries : null,
            ColorTransfer = UsableColour(video.ColorTransfer) ? video.ColorTransfer : null,
            ColorSpace = UsableColour(video.ColorSpace) ? video.ColorSpace : null,
            ColorRange = UsableColour(video.ColorRange) ? video.ColorRange : null,
            AudioLanguages = audioStreams.Select(a => a.Language ?? string.Empty).ToList(),
            SubtitleLanguages = subtitles.Select(s => s.Stream.Language ?? string.Empty).ToList(),
            TrackSignatures = new[] { VideoSignature(request.VideoCodec.ProbeName()) }
                .Concat(audioStreams.Select(a => AudioSignature(a,
                    audioCopy ? a.Codec : audioCodecName,
                    audioCopy ? null : request.Audio.Channels,
                    assertDispositions: request.Container is ContainerFormat.Mkv or ContainerFormat.Webm)))
                .ToList(),
        };
    }

    private static ExpectedStreamSignature VideoSignature(string? codec) => new()
    {
        Kind = MediaKind.Video,
        Codec = string.IsNullOrEmpty(codec) ? null : codec,
    };

    /// <param name="assertDispositions">True only for Matroska outputs, the only muxer in the
    /// supported set that reliably preserves the full disposition mask. MP4 always promotes the
    /// first track of a kind to default, and the audio-only muxers carry no dispositions.</param>
    private static ExpectedStreamSignature AudioSignature(StreamDescriptor source, string? codec, int? outputChannels,
        bool assertDispositions) => new()
    {
        Kind = MediaKind.Audio,
        Codec = string.IsNullOrEmpty(codec) ? null : codec,
        Channels = outputChannels ?? source.Channels,
        IsDefault = assertDispositions ? source.IsDefault : null,
        IsForced = assertDispositions ? source.IsForced : null,
    };

    private static List<string> BuildWarnings(ConversionRequest request, SourceSnapshot source,
        StreamSelectionResolver.ResolvedStreamSelection selection, SubtitlePolicy.SubtitleSelection subtitles)
    {
        var warnings = new List<string>();
        warnings.AddRange(selection.Warnings);
        warnings.AddRange(subtitles.Warnings);
        if (request.Mode is ConversionMode.VideoTranscode or ConversionMode.Remux)
            AddSubtitleWarnings(request, source, selection, subtitles, warnings);
        if (request.Metadata == MetadataPolicy.StripPersonalTags)
            warnings.Add("自由标签与章节已清除；音轨/字幕的语言与默认/强制标记保留；其它 disposition 标记是否保留取决于目标容器。");
        else
            warnings.Add("标题/章节与流标签已保留；元数据可能包含隐私信息。");
        if (request.Mode == ConversionMode.Remux && source.Format.ChapterCount is > 0
            && request.Metadata == MetadataPolicy.StripPersonalTags)
            warnings.Add($"章节（{source.Format.ChapterCount} 个）已按元数据策略清除。");
        if (request.Mode == ConversionMode.VideoTranscode && request.Trim is not null
            && source.Format.ChapterCount is > 0)
            warnings.Add($"裁剪后的时间轴无法保留原章节，已移除 {source.Format.ChapterCount} 个章节标记。");
        if ((request.Mode is ConversionMode.VideoTranscode or ConversionMode.Remux) && request.Container != ContainerFormat.Mkv)
        {
            var attachments = source.Streams.Count(s => s.Kind == MediaKind.Attachment);
            if (attachments > 0)
                warnings.Add($"字体/附件流（{attachments} 条）无法写入 {request.Container.DisplayName()}，未随输出保留。");
        }
        var effectiveColour = selection.Video?.ColorClassification ?? source.ColorClassification;
        if (request.Mode == ConversionMode.VideoTranscode && effectiveColour != ColorClassification.SdrBt709)
            warnings.Add($"Source colour classified as {effectiveColour}.");
        return warnings;
    }

    /// <summary>
    /// Subtitle messaging must match the resolved selection: retained tracks are named as
    /// retained/copied/converted, excluded tracks are named as excluded with the reason, and
    /// a source with zero subtitle tracks never claims that "all subtitles are preserved".
    /// </summary>
    private static void AddSubtitleWarnings(ConversionRequest request, SourceSnapshot source,
        StreamSelectionResolver.ResolvedStreamSelection selection, SubtitlePolicy.SubtitleSelection subtitles,
        List<string> warnings)
    {
        if (!source.SubtitleStreams.Any()) return;

        if (subtitles.Included.Count > 0)
        {
            var copied = subtitles.Included.Count(t => t.IsCopy);
            var converted = subtitles.Included.Count - copied;
            var detail = converted == 0
                ? $"共 {subtitles.Included.Count} 条，均直接复制"
                : $"共 {subtitles.Included.Count} 条（{copied} 条复制、{converted} 条转换）";
            warnings.Add($"字幕随输出保留：{detail}，不做烧录。");
            return;
        }

        if (selection.Subtitles.Count == 0)
        {
            var reason = request.RequestVersion >= 2
                ? request.Subtitles.Mode == SubtitleSelectionMode.None ? "已选择不输出字幕" : "未选择任何字幕轨"
                : "旧版选流未包含字幕轨";
            warnings.Add($"字幕未写入输出：{reason}；源文件 {source.SubtitleStreams.Count()} 条字幕均被排除。");
            return;
        }

        warnings.Add($"所选字幕轨（{selection.Subtitles.Count} 条）均无法写入 {request.Container.DisplayName()}，已从输出排除。");
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
            schema = CurrentPlanSchemaVersion,
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
            PlanSchemaVersion = CurrentPlanSchemaVersion,
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
                AdapterId = candidates.FirstOrDefault(c => c.Backend != HardwareBackend.Cpu)?.DeviceId,
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
