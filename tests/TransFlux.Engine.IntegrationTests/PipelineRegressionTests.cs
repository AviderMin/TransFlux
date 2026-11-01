using TransFlux.Application.Contracts;
using TransFlux.Domain.Common;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Engine.FFmpeg.Planner;
using TransFlux.Engine.FFmpeg.Probe;
using TransFlux.Engine.FFmpeg.Processes;
using TransFlux.Engine.FFmpeg.Runner;
using TransFlux.Engine.FFmpeg.Verifier;

namespace TransFlux.Engine.IntegrationTests;

/// <summary>
/// Task-2 regression tests: multi-audio retention, language/disposition rebuild,
/// subtitle carrying (mov_text / bitmap copy / attachments), colour passthrough,
/// HDR static metadata parsing and Dolby Vision/HDR classification fixes.
/// </summary>
public class PipelineRegressionTests
{
    private static async Task RunFfmpegAsync(params string[] args)
    {
        var result = await ProcessCapture.RunAsync(TestSupport.Core.FfmpegPath, args.ToList(),
            TimeSpan.FromSeconds(120), 1 << 20, 1 << 20, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private static async Task<SourceSnapshot> ProbeAsync(string path)
    {
        var r = await new FfmpegProbe(TestSupport.Core).ProbeAsync(
            new SourceReference { AbsolutePath = path }, CancellationToken.None);
        Assert.True(r.IsSuccess, r.Failure?.TechnicalDetail);
        return r.Value!;
    }

    private static async Task<(SourceSnapshot Source, string Path)> GenerateTwoAudioMkvAsync(string name)
    {
        var path = TestSupport.PathFor(name);
        if (!File.Exists(path))
        {
            await RunFfmpegAsync(
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=10:duration=1",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
                "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=1",
                "-map", "0:v", "-map", "1:a", "-map", "2:a",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac",
                "-metadata:s:a:0", "language=eng", "-disposition:a:0", "default",
                "-metadata:s:a:1", "language=jpn", "-disposition:a:1", "forced",
                path);
        }
        return (await ProbeAsync(path), path);
    }

    private static async Task<(ValidatedPlan Plan, SourceSnapshot Source, ConversionRequest Request)> PlanAsync(
        SourceSnapshot source, ConversionRequest request)
    {
        var planner = new FfmpegPlanner(TestSupport.Core);
        var planned = await planner.BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(planned.IsSuccess, planned.Failure?.TechnicalDetail);
        return (planned.Value!, source, request);
    }

    private static async Task<string> RunAndVerifyAsync(ValidatedPlan plan, SourceSnapshot source, ConversionRequest request)
    {
        var attempt = TestSupport.BuildAttempt(plan, source, request);
        var run = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, new CollectingProgress(), CancellationToken.None);
        Assert.True(run.Succeeded, run.Failure?.TechnicalDetail ?? run.StderrTail);
        var verification = await new FfmpegVerifier(TestSupport.Core).VerifyAsync(new VerificationRequest
        {
            Plan = plan,
            OutputPath = attempt.TempPath,
            Source = source,
        }, CancellationToken.None);
        Assert.True(verification.Passed, verification.Summary);
        return attempt.TempPath;
    }

    // ---------------- Major 1: multi-audio retention ----------------

    [Fact]
    public async Task Transcode_Mp4_MultiAudio_KeepsAllTracks()
    {
        var (source, _) = await GenerateTwoAudioMkvAsync("multi-audio-src.mkv");
        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mp4);
        var (plan, src, req) = await PlanAsync(source, request);
        Assert.Equal(2, plan.ExpectedOutput.AudioStreamCount);
        Assert.Equal(3, plan.ExpectedOutput.StreamCount);

        var outPath = await RunAndVerifyAsync(plan, src, req);
        var output = await ProbeAsync(outPath);
        Assert.Equal(2, output.AudioStreams.Count());
    }

    // ---------------- Major 2: language / disposition rebuild ----------------

    [Fact]
    public async Task Transcode_PreservesLanguageAndDisposition()
    {
        var (source, _) = await GenerateTwoAudioMkvAsync("language-src.mkv");
        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mkv);
        var (plan, src, req) = await PlanAsync(source, request);
        Assert.Equal(new[] { "eng", "jpn" }, plan.ExpectedOutput.AudioLanguages);

        var outPath = await RunAndVerifyAsync(plan, src, req);
        var output = await ProbeAsync(outPath);
        var audio = output.AudioStreams.ToList();
        Assert.Equal(2, audio.Count);
        Assert.Equal("eng", audio[0].Language);
        Assert.True(audio[0].IsDefault, "default marker must survive the transcode");
        Assert.Equal("jpn", audio[1].Language);
        Assert.True(audio[1].IsForced, "forced marker must survive the transcode");
    }

    // ---------------- Major 3: subtitle chain ----------------

    [Fact]
    public async Task Transcode_Mp4_TextSubtitle_BecomesMovText()
    {
        var srt = TestSupport.PathFor("pipeline-sub.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:00,800\nhello transflux\n");
        var path = TestSupport.PathFor("subtitle-src.mkv");
        if (!File.Exists(path))
        {
            await RunFfmpegAsync(
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=10:duration=1",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
                "-i", srt,
                "-map", "0:v", "-map", "1:a", "-map", "2:s",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-c:s", "srt",
                "-metadata:s:s:0", "language=jpn",
                path);
        }
        var source = await ProbeAsync(path);
        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mp4);
        var (plan, src, req) = await PlanAsync(source, request);
        Assert.Equal(new[] { "jpn" }, plan.ExpectedOutput.SubtitleLanguages);

        var outPath = await RunAndVerifyAsync(plan, src, req);
        var output = await ProbeAsync(outPath);
        var sub = Assert.Single(output.SubtitleStreams);
        Assert.Equal("mov_text", sub.Codec);
        Assert.Equal("jpn", sub.Language);
    }

    [Fact]
    public async Task Remux_Mkv_KeepsFontAttachments()
    {
        var baseVideo = await TestSupport.GenerateVideoAsync("attach-base.mp4");
        var font = TestSupport.PathFor("dummy-font.ttf");
        if (!File.Exists(font)) await File.WriteAllBytesAsync(font, new byte[] { 0x00, 0x01, 0x00, 0x00 });
        var path = TestSupport.PathFor("attach-src.mkv");
        if (!File.Exists(path))
        {
            await RunFfmpegAsync(
                "-hide_banner", "-y", "-i", baseVideo,
                "-attach", font, "-metadata:s:t:0", "mimetype=application/x-truetype-font",
                "-c", "copy", path);
        }
        var source = await ProbeAsync(path);
        Assert.Contains(source.Streams, s => s.Kind == MediaKind.Attachment);

        var request = new ConversionRequest { SourceSnapshotId = source.MediaId, Mode = ConversionMode.Remux, Container = ContainerFormat.Mkv };
        var (plan, src, req) = await PlanAsync(source, request);
        Assert.Equal(1, plan.ExpectedOutput.AttachmentStreamCount);
        Assert.Equal(3, plan.ExpectedOutput.StreamCount);

        var outPath = await RunAndVerifyAsync(plan, src, req);
        var output = await ProbeAsync(outPath);
        Assert.Contains(output.Streams, s => s.Kind == MediaKind.Attachment);
    }

    // ---------------- Major 5: colour passthrough ----------------

    [Fact]
    public async Task Transcode_PreservesColourMetadata()
    {
        var path = TestSupport.PathFor("colour-src.mp4");
        if (!File.Exists(path))
        {
            await RunFfmpegAsync(
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=10:duration=1",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
                "-map", "0:v", "-map", "1:a",
                "-vf", "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac",
                path);
        }
        var source = await ProbeAsync(path);
        Assert.Equal("bt709", source.PrimaryVideo!.ColorPrimaries);

        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mp4);
        var (plan, src, req) = await PlanAsync(source, request);
        Assert.Equal("bt709", plan.ExpectedOutput.ColorPrimaries);
        Assert.Equal("bt709", plan.ExpectedOutput.ColorTransfer);
        Assert.Equal("bt709", plan.ExpectedOutput.ColorSpace);
        var args = plan.CandidatePaths[0].Arguments;
        Assert.Contains(args, a => a.StartsWith("setparams="));

        var outPath = await RunAndVerifyAsync(plan, src, req);
        var output = await ProbeAsync(outPath);
        Assert.Equal("bt709", output.PrimaryVideo!.ColorPrimaries);
        Assert.Equal("bt709", output.PrimaryVideo.ColorTransfer);
        Assert.Equal("bt709", output.PrimaryVideo.ColorSpace);
        Assert.Equal("tv", output.PrimaryVideo.ColorRange);
    }

    // ---------------- HDR static metadata parsing ----------------

    [Fact]
    public async Task Probe_ParsesHdrStaticMetadata()
    {
        var path = TestSupport.PathFor("hdr-static.mkv");
        if (!File.Exists(path))
        {
            await RunFfmpegAsync(
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=128x72:rate=10:duration=0.5",
                "-c:v", "libx265", "-pix_fmt", "yuv420p10le",
                // setparams is the verified carrier for colour properties on the pinned core.
                "-vf", "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc",
                "-x265-params", "master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):max-cll=1000,400",
                path);
        }
        var snapshot = await ProbeAsync(path);
        var video = snapshot.PrimaryVideo!;
        Assert.Equal("G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50)", video.MasterDisplay);
        Assert.Equal(1000, video.MaxCLL);
        Assert.Equal(400, video.MaxFALL);
        Assert.Equal(ColorClassification.Hdr10, snapshot.ColorClassification);
    }

    // ---------------- Planner argument regressions ----------------

    private static SourceSnapshot SyntheticWithSub(string subCodec, string? subLanguage = null, string? audioLanguage = null)
    {
        var streams = new StreamDescriptor[]
        {
            new()
            {
                SourceIndex = 0, Kind = MediaKind.Video, Codec = "h264", Width = 320, Height = 240,
                PixelFormat = "yuv420p", BitDepth = 8, SampleAspectRatio = new Rational(1, 1),
                FrameRate = new Rational(30, 1), AverageFrameRate = new Rational(30, 1),
            },
            new()
            {
                SourceIndex = 1, Kind = MediaKind.Audio, Codec = "aac", Channels = 2,
                SampleRate = 48000, IsDefault = true, Language = audioLanguage,
            },
            new()
            {
                SourceIndex = 2, Kind = MediaKind.Subtitle, Codec = subCodec,
                Language = subLanguage, IsForced = true,
            },
        };
        return new SourceSnapshot
        {
            MediaId = Guid.NewGuid(),
            AbsolutePath = "C:\\fake\\input.mkv",
            SizeBytes = 1000,
            DurationUs = 5_000_000,
            Streams = streams,
            Format = new MediaFormat { FormatName = "matroska", DurationUs = 5_000_000 },
            ColorClassification = ColorClassification.SdrBt709,
        };
    }

    [Fact]
    public async Task Planner_MkvBitmapSubtitle_StreamCopies()
    {
        var source = SyntheticWithSub("hdmv_pgs_subtitle");
        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mkv);
        var (plan, _, _) = await PlanAsync(source, request);
        var args = plan.CandidatePaths[0].Arguments.ToList();
        var at = args.IndexOf("-c:s:0");
        Assert.True(at >= 0, "bitmap subtitle must be mapped for MKV");
        Assert.Equal("copy", args[at + 1]);
    }

    [Fact]
    public async Task Planner_Mp4Subrip_UsesMovText()
    {
        var source = SyntheticWithSub("subrip");
        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mp4);
        var (plan, _, _) = await PlanAsync(source, request);
        var args = plan.CandidatePaths[0].Arguments.ToList();
        var at = args.IndexOf("-c:s:0");
        Assert.True(at >= 0);
        Assert.Equal("mov_text", args[at + 1]);
    }

    [Fact]
    public async Task Planner_RebuildsLanguageAndDisposition()
    {
        var source = SyntheticWithSub("subrip", subLanguage: "jpn", audioLanguage: "chi");
        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mkv);
        var (plan, _, _) = await PlanAsync(source, request);
        var args = plan.CandidatePaths[0].Arguments.ToList();

        Assert.Contains("-metadata:s:a:0", args);
        Assert.Equal("language=chi", args[args.IndexOf("-metadata:s:a:0") + 1]);
        Assert.Contains("-disposition:a:0", args);
        Assert.Equal("default", args[args.IndexOf("-disposition:a:0") + 1]);
        Assert.Contains("-metadata:s:s:0", args);
        Assert.Equal("language=jpn", args[args.IndexOf("-metadata:s:s:0") + 1]);
        Assert.Contains("-disposition:s:0", args);
        Assert.Equal("forced", args[args.IndexOf("-disposition:s:0") + 1]);
    }

    [Fact]
    public async Task Planner_Mp4KeepsMultipleAudioMaps()
    {
        var source = SyntheticWithSub("subrip");
        source = source with
        {
            Streams = source.Streams.Concat(new[]
            {
                new StreamDescriptor { SourceIndex = 3, Kind = MediaKind.Audio, Codec = "aac", Channels = 2, SampleRate = 48000 },
            }).ToList(),
        };
        var request = TestSupport.TranscodeRequest(source, ContainerFormat.Mp4);
        var (plan, _, _) = await PlanAsync(source, request);
        Assert.Equal(2, plan.ExpectedOutput.AudioStreamCount);
        var args = plan.CandidatePaths[0].Arguments;
        Assert.Contains("0:1", args);
        Assert.Contains("0:3", args);
    }

    // ---------------- Classification / parser regressions ----------------

    [Fact]
    public void ColorClassifier_Smpte432_IsWideGamut_NotDolbyVision()
    {
        var video = new StreamDescriptor
        {
            SourceIndex = 0, Kind = MediaKind.Video, Codec = "h264", PixelFormat = "yuv420p", BitDepth = 8,
            ColorPrimaries = "smpte432", ColorSpace = "smpte432",
        };
        Assert.Equal(ColorClassification.WideGamut, ColorClassifier.Classify(video));
    }

    [Fact]
    public void ColorClassifier_Hlg_IsDistinctClass()
    {
        var video = new StreamDescriptor
        {
            SourceIndex = 0, Kind = MediaKind.Video, Codec = "hevc", PixelFormat = "yuv420p10le", BitDepth = 10,
            ColorTransfer = "arib-std-b67", ColorPrimaries = "bt2020", ColorSpace = "bt2020nc",
        };
        Assert.Equal(ColorClassification.Hlg, ColorClassifier.Classify(video));
    }

    [Fact]
    public void InferBitDepth_Handles14BitAndPacked48()
    {
        Assert.Equal(14, ColorClassifier.InferBitDepth("yuv444p14le"));
        Assert.Equal(16, ColorClassifier.InferBitDepth("rgb48le"));
        Assert.Equal(16, ColorClassifier.InferBitDepth("rgba64le"));
        Assert.Equal(8, ColorClassifier.InferBitDepth("yuv420p"));
    }

    [Fact]
    public void Parser_DoviConfigurationRecord_ExtractsProfile()
    {
        const string json = """
        {
          "streams": [
            { "index": 0, "codec_name": "hevc", "codec_type": "video", "width": 64, "height": 64,
              "pix_fmt": "yuv420p10le", "bits_per_raw_sample": "10",
              "side_data_list": [
                { "side_data_type": "DOVI configuration record", "dv_profile": 8, "dv_level": 6,
                  "rpu_present_flag": 1, "el_present_flag": 0, "bl_present_flag": 1,
                  "dv_bl_signal_compatibility_id": 1 }
              ] }
          ],
          "format": { "format_name": "mov,mp4", "duration": "1.000000" }
        }
        """;
        var snapshot = FfprobeJsonParser.Parse(json, "C:\\x\\dv.mp4", 46930, DateTimeOffset.UtcNow, Guid.NewGuid());
        var video = snapshot.PrimaryVideo!;
        Assert.Equal(8, video.DvProfile);
        Assert.Equal(1, video.DvBlSignalCompatibilityId);
        Assert.Equal(ColorClassification.DolbyVision, snapshot.ColorClassification);
    }

    [Fact]
    public void Parser_FrameLevelHdrSideData_MergesIntoStream()
    {
        const string json = """
        {
          "streams": [
            { "index": 0, "codec_name": "hevc", "codec_type": "video", "width": 64, "height": 64,
              "pix_fmt": "yuv420p10le", "bits_per_raw_sample": "10", "time_base": "1/10240", "start_time": "0.047000" }
          ],
          "frames": [
            { "media_type": "video", "stream_index": 0, "side_data_list": [
                { "side_data_type": "Mastering display metadata",
                  "red_x": "34000/50000", "red_y": "16000/50000",
                  "green_x": "13250/50000", "green_y": "34500/50000",
                  "blue_x": "7500/50000", "blue_y": "3000/50000",
                  "white_point_x": "15635/50000", "white_point_y": "16450/50000",
                  "min_luminance": "50/10000", "max_luminance": "10000000/10000" },
                { "side_data_type": "Content light level metadata", "max_content": 1000, "max_average": 400 }
              ] }
          ],
          "format": { "format_name": "matroska", "duration": "1.000000" }
        }
        """;
        var snapshot = FfprobeJsonParser.Parse(json, "C:\\x\\hdr.mkv", 46930, DateTimeOffset.UtcNow, Guid.NewGuid());
        var video = snapshot.PrimaryVideo!;
        Assert.Equal("G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50)", video.MasterDisplay);
        Assert.Equal(1000, video.MaxCLL);
        Assert.Equal(400, video.MaxFALL);
        Assert.Equal(47_000, video.StartUs);
        Assert.Equal(1, video.TimeBase!.Value.Numerator);
        Assert.Equal(10240, video.TimeBase!.Value.Denominator);
    }
}
