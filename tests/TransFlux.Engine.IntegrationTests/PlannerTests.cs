using TransFlux.Application.Contracts;
using TransFlux.Domain.Common;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Engine.FFmpeg.Planner;
using TransFlux.Engine.FFmpeg.Probe;

namespace TransFlux.Engine.IntegrationTests;

public class PlannerTests
{
    [Theory]
    [InlineData("nvidia", "h264_nvenc", HardwareBackend.NvidiaNvenc)]
    [InlineData("intel", "h264_qsv", HardwareBackend.IntelQsv)]
    [InlineData("amd", "h264_amf", HardwareBackend.AmdAmf)]
    public async Task Auto_BindsVerifiedSelectedAdapter_ThenFallsBackToCpu(string backend, string encoder, HardwareBackend expected)
    {
        var source = SyntheticVideo(640, 360);
        var request = TestSupport.TranscodeRequest(source) with { Hardware = HardwarePreference.ForAdapter("LUID-selected") };
        var cap = new CandidateCapability { CandidateId = "gpu", Backend = backend, EncoderName = encoder,
            DeviceId = "LUID-selected", Direct3DAdapterIndex = 2, TrialCompleted = true, Available = true };
        var caps = TestSupport.EmptyCapabilities() with { Candidates = new[] { cap with { DeviceId = "other", Direct3DAdapterIndex = 0 }, cap } };
        var result = await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, caps, default);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        var paths = result.Value!.CandidatePaths;
        Assert.Equal(2, paths.Count);
        Assert.Equal(expected, paths[0].Backend);
        Assert.Equal("LUID-selected", paths[0].DeviceId);
        Assert.Contains("d3d11va=tfbase:2", paths[0].Arguments);
        Assert.DoesNotContain("LUID-selected", paths[0].Arguments);
        Assert.Contains(paths[0].Arguments, a => a.Contains("hwupload"));
        if (backend == "intel") Assert.Contains("qsv=tfencode@tfbase", paths[0].Arguments);
        Assert.Equal(HardwareBackend.Cpu, paths[1].Backend);
        Assert.DoesNotContain("-init_hw_device", paths[1].Arguments);
        var untested = caps with { Candidates = new[] { cap with { TrialCompleted = false } } };
        var safe = await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, untested, default);
        Assert.Equal(HardwareBackend.Cpu, Assert.Single(safe.Value!.CandidatePaths).Backend);
    }

    private static async Task<SourceSnapshot> ProbeSampleAsync(string name = "planner-sample.mp4")
    {
        var path = await TestSupport.GenerateVideoAsync(name);
        var probe = new FfmpegProbe(TestSupport.Core);
        var r = await probe.ProbeAsync(new SourceReference { AbsolutePath = path }, CancellationToken.None);
        Assert.True(r.IsSuccess, r.Failure?.TechnicalDetail);
        return r.Value!;
    }

    private static SourceSnapshot SyntheticVideo(int width, int height, int bitDepth = 8, string pixFmt = "yuv420p",
        ColorClassification color = ColorClassification.SdrBt709, string? side = null, int rotation = 0)
    {
        var stream = new StreamDescriptor
        {
            SourceIndex = 0,
            Kind = MediaKind.Video,
            Codec = "h264",
            Width = width,
            Height = height,
            PixelFormat = pixFmt,
            BitDepth = bitDepth,
            SampleAspectRatio = new Rational(1, 1),
            FrameRate = new Rational(30, 1),
            AverageFrameRate = new Rational(30, 1),
            HasHdrSideData = side is not null,
            SideDataSummary = side,
            RotationDegrees = rotation,
            DurationUs = 5_000_000,
        };
        return new SourceSnapshot
        {
            MediaId = Guid.NewGuid(),
            AbsolutePath = "C:\\fake\\input.mp4",
            SizeBytes = 1000,
            DurationUs = 5_000_000,
            Streams = new[] { stream },
            Format = new MediaFormat { FormatName = "mp4", DurationUs = 5_000_000 },
            ColorClassification = color,
        };
    }

    private static StreamDescriptor AudioTrace(int index, string? language = null, string codec = "aac")
        => new()
        {
            SourceIndex = index,
            Kind = MediaKind.Audio,
            Codec = codec,
            Channels = 2,
            ChannelLayout = "stereo",
            SampleRate = 48000,
            IsDefault = index == 1,
            Language = language,
        };

    private static StreamDescriptor SubtitleTrace(int index, string codec = "subrip", string? language = null)
        => new() { SourceIndex = index, Kind = MediaKind.Subtitle, Codec = codec, Language = language };

    private static SourceSnapshot SyntheticWithStreams(params StreamDescriptor[] extra)
    {
        var source = SyntheticVideo(640, 360);
        return source with { Streams = new[] { source.Streams[0] }.Concat(extra).ToArray() };
    }

    private static string[] Arguments(ValidatedPlan plan) => plan.CandidatePaths[0].Arguments.ToArray();

    private static bool HasMap(string[] args, int sourceIndex)
        => args.Select((arg, i) => (arg, next: i + 1 < args.Length ? args[i + 1] : null))
            .Any(p => p.arg == "-map" && p.next == $"0:{sourceIndex}");

    [Fact]
    public async Task BuildTranscodePlan_ProducesTypedArgsAndExpectedOutput()
    {
        var source = await ProbeSampleAsync();
        var request = TestSupport.TranscodeRequest(source);
        var planner = new FfmpegPlanner(TestSupport.Core);

        var result = await planner.BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        var plan = result.Value!;

        var cpu = plan.CandidatePaths[0];
        Assert.Equal(HardwareBackend.Cpu, cpu.Backend);
        Assert.Equal("libx264", cpu.EncoderName);
        Assert.Contains(PlanPlaceholders.Input, cpu.Arguments);
        Assert.Contains(PlanPlaceholders.Output, cpu.Arguments);
        Assert.Contains("-progress", cpu.Arguments);
        Assert.Contains("pipe:1", cpu.Arguments);
        Assert.Contains("-crf", cpu.Arguments);
        Assert.Contains("23", cpu.Arguments);
        Assert.Contains("+faststart", cpu.Arguments);

        Assert.Equal(VideoCodec.H264, plan.ExpectedOutput.VideoCodec);
        Assert.Equal(AudioCodec.Aac, plan.ExpectedOutput.AudioCodec);
        Assert.Equal("yuv420p", plan.ExpectedOutput.PixelFormat);
        Assert.Equal(320, plan.ExpectedOutput.Width);
        Assert.Equal(240, plan.ExpectedOutput.Height);
        Assert.True(plan.ExpectedOutput.FastStart);
        Assert.Equal(1, plan.ExpectedOutput.VideoStreamCount);
        Assert.Equal(1, plan.ExpectedOutput.AudioStreamCount);
        Assert.Equal(64, plan.PlanHash.Length);
        Assert.Equal(64, plan.RequestHash.Length);
        Assert.Equal(TestSupport.Core.CoreHash, plan.CoreHash);
    }

    [Fact]
    public async Task BuildTranscodePlan_IsDeterministicHash()
    {
        var source = await ProbeSampleAsync("planner-deterministic.mp4");
        var request = TestSupport.TranscodeRequest(source);
        var planner = new FfmpegPlanner(TestSupport.Core);
        var a = await planner.BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        var b = await planner.BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.Equal(a.Value!.PlanHash, b.Value!.PlanHash);
        Assert.NotEqual(a.Value!.PlanId, b.Value!.PlanId);
    }

    [Fact]
    public async Task BuildPlan_BlocksHdrSource()
    {
        var source = SyntheticVideo(1920, 1080, bitDepth: 10, pixFmt: "yuv420p10le",
            color: ColorClassification.Hdr10, side: "Mastering display metadata");
        var request = TestSupport.TranscodeRequest(source);
        var planner = new FfmpegPlanner(TestSupport.Core);
        var result = await planner.BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ResultKind.ValidationFailure, result.Kind);
    }

    [Fact]
    public async Task BuildPlan_Max1080p_DownscalesEven()
    {
        var source = SyntheticVideo(3840, 2160);
        var request = TestSupport.TranscodeRequest(source) with { Resize = ResizeSpec.Max1080p() };
        var planner = new FfmpegPlanner(TestSupport.Core);
        var result = await planner.BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        Assert.Equal(1920, result.Value!.ExpectedOutput.Width);
        Assert.Equal(1080, result.Value.ExpectedOutput.Height);
    }

    [Fact]
    public async Task BuildPlan_OddDimensions_RoundedDownToEven()
    {
        var source = SyntheticVideo(1919, 1079);
        var request = TestSupport.TranscodeRequest(source);
        var planner = new FfmpegPlanner(TestSupport.Core);
        var result = await planner.BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        Assert.Equal(1918, result.Value!.ExpectedOutput.Width);
        Assert.Equal(1078, result.Value.ExpectedOutput.Height);
    }

    [Fact]
    public async Task BuildRemuxPlan_CopiesStreams()
    {
        var source = await ProbeSampleAsync("planner-remux.mp4");
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            Mode = ConversionMode.Remux,
            Container = ContainerFormat.Mkv,
        };
        var planner = new FfmpegPlanner(TestSupport.Core);
        var result = await planner.BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        var plan = result.Value!;
        Assert.Equal("copy", plan.CandidatePaths[0].EncoderName);
        Assert.Contains("-c", plan.CandidatePaths[0].Arguments);
        Assert.Contains("copy", plan.CandidatePaths[0].Arguments);
        Assert.Equal(VideoCodec.Copy, plan.ExpectedOutput.VideoCodec);
        Assert.False(plan.ExpectedOutput.FastStart);
    }

    [Fact]
    public async Task BuildAudioExtractPlan_M4a_ProducesAacOnly()
    {
        var source = await ProbeSampleAsync("planner-audio.mp4");
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            Mode = ConversionMode.AudioExtract,
            Container = ContainerFormat.M4a,
            Audio = new AudioSpec { Codec = AudioCodec.Aac, BitRateKbps = 192 },
        };
        var planner = new FfmpegPlanner(TestSupport.Core);
        var result = await planner.BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        var plan = result.Value!;
        Assert.Equal(AudioCodec.Aac, plan.ExpectedOutput.AudioCodec);
        Assert.Equal(0, plan.ExpectedOutput.VideoStreamCount);
        Assert.Equal(1, plan.ExpectedOutput.AudioStreamCount);
        Assert.Contains("-vn", plan.CandidatePaths[0].Arguments);
    }
    [Fact]
    public async Task V2_AudioSelection_DoesNotDropSubtitles_AndMapsOnlySelectedAudio()
    {
        var source = SyntheticWithStreams(AudioTrace(1), SubtitleTrace(2), AudioTrace(3));
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            RequestVersion = 2,
            Mode = ConversionMode.VideoTranscode,
            Container = ContainerFormat.Mp4,
            AudioTracks = AudioSelection.Tracks(1),
            Subtitles = SubtitleSelection.AutoPreserve,
        };
        var result = await new FfmpegPlanner(TestSupport.Core)
            .BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        var plan = result.Value!;
        var args = Arguments(plan);
        Assert.True(HasMap(args, 0));
        Assert.True(HasMap(args, 1));
        Assert.False(HasMap(args, 3));
        Assert.True(HasMap(args, 2));
        Assert.Equal(1, plan.ExpectedOutput.AudioStreamCount);
        Assert.Single(plan.ExpectedOutput.SubtitleLanguages);
        Assert.Contains(plan.Warnings, w => w.Contains("字幕随输出保留"));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("未写入输出"));
    }

    [Fact]
    public async Task V2_SubtitleNone_EmitsNoSubtitleMap_AndWarningSaysExcluded()
    {
        var source = SyntheticWithStreams(AudioTrace(1), SubtitleTrace(2));
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            RequestVersion = 2,
            Mode = ConversionMode.VideoTranscode,
            Container = ContainerFormat.Mp4,
            AudioTracks = AudioSelection.Auto,
            Subtitles = SubtitleSelection.None,
        };
        var result = await new FfmpegPlanner(TestSupport.Core)
            .BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        var plan = result.Value!;
        Assert.False(HasMap(Arguments(plan), 2));
        Assert.Equal(0, plan.ExpectedOutput.StreamCount - 2); // one video + one audio only
        Assert.Contains(plan.Warnings, w => w.Contains("字幕未写入输出") && w.Contains("已选择不输出字幕"));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("字幕随输出保留"));
    }

    [Fact]
    public async Task V2_SubtitleExplicit_Mkv_CopiesOnlyListedSubtitle()
    {
        var source = SyntheticWithStreams(AudioTrace(1), SubtitleTrace(2, "ass", "jpn"), SubtitleTrace(3, "subrip", "eng"));
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            RequestVersion = 2,
            Mode = ConversionMode.VideoTranscode,
            Container = ContainerFormat.Mkv,
            AudioTracks = AudioSelection.Auto,
            Subtitles = SubtitleSelection.Tracks(3),
        };
        var result = await new FfmpegPlanner(TestSupport.Core)
            .BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        var plan = result.Value!;
        var args = Arguments(plan);
        Assert.False(HasMap(args, 2));
        Assert.True(HasMap(args, 3));
        Assert.Equal(new[] { "eng" }, plan.ExpectedOutput.SubtitleLanguages);
        Assert.Contains(plan.Warnings, w => w.Contains("字幕随输出保留") && w.Contains("1 条"));
    }

    [Fact]
    public async Task V2_AudioExtract_MultipleExplicitTracks_IsBlockedWithSingleTrackMessage()
    {
        var source = SyntheticWithStreams(AudioTrace(1), AudioTrace(3));
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            RequestVersion = 2,
            Mode = ConversionMode.AudioExtract,
            Container = ContainerFormat.M4a,
            AudioTracks = AudioSelection.Tracks(1, 3),
        };
        var result = await new FfmpegPlanner(TestSupport.Core)
            .BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Contains("一次只输出一条音轨", result.Failure!.TechnicalDetail);
    }

    [Fact]
    public async Task V2_AudioExtract_SingleExplicitTrack_MapsExactlyThatTrack()
    {
        var source = SyntheticWithStreams(AudioTrace(1), AudioTrace(3));
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            RequestVersion = 2,
            Mode = ConversionMode.AudioExtract,
            Container = ContainerFormat.M4a,
            AudioTracks = AudioSelection.Tracks(3),
            Audio = new AudioSpec { Codec = AudioCodec.Aac },
        };
        var result = await new FfmpegPlanner(TestSupport.Core)
            .BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        var plan = result.Value!;
        Assert.True(HasMap(Arguments(plan), 3));
        Assert.False(HasMap(Arguments(plan), 1));
        Assert.Equal(1, plan.ExpectedOutput.AudioStreamCount);
    }

    [Fact]
    public async Task V2_AudioNone_TranscodeEmitsAnAndNoAudioStreams()
    {
        var source = SyntheticWithStreams(AudioTrace(1), SubtitleTrace(2));
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            RequestVersion = 2,
            Mode = ConversionMode.VideoTranscode,
            Container = ContainerFormat.Mp4,
            AudioTracks = AudioSelection.None,
            Subtitles = SubtitleSelection.AutoPreserve,
        };
        var result = await new FfmpegPlanner(TestSupport.Core)
            .BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        var plan = result.Value!;
        Assert.Contains("-an", Arguments(plan));
        Assert.Equal(0, plan.ExpectedOutput.AudioStreamCount);
        Assert.Equal(1, plan.ExpectedOutput.StreamCount - 1); // video plus one subtitle
    }

    [Fact]
    public async Task V2_ZeroSubtitles_DoesNotClaimAllSubtitlesArePreserved()
    {
        var source = SyntheticWithStreams(AudioTrace(1));
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            RequestVersion = 2,
            Mode = ConversionMode.VideoTranscode,
            Container = ContainerFormat.Mp4,
            AudioTracks = AudioSelection.Auto,
        };
        var result = await new FfmpegPlanner(TestSupport.Core)
            .BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        Assert.DoesNotContain(result.Value!.Warnings, w => w.Contains("全部保留") || w.Contains("字幕随输出保留"));
    }
    [Fact]
    public async Task V1_AudioOnlySelection_KeepsLegacyCombinedFilterSemantics()
    {
        var source = SyntheticWithStreams(AudioTrace(1), SubtitleTrace(2));
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            RequestVersion = 1,
            Mode = ConversionMode.VideoTranscode,
            Container = ContainerFormat.Mp4,
            SelectedStreamIndices = new[] { 1 }, // legacy audio selection also filtered subtitles
        };
        var result = await new FfmpegPlanner(TestSupport.Core)
            .BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        var plan = result.Value!;
        Assert.False(HasMap(Arguments(plan), 2));
        Assert.Empty(plan.ExpectedOutput.SubtitleLanguages);
        Assert.Contains(plan.Warnings, w => w.Contains("旧版选流未包含字幕轨"));
    }
}
