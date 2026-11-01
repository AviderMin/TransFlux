using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;
using TransFlux.Engine.FFmpeg.Planner;
using TransFlux.Engine.FFmpeg.Probe;
using TransFlux.Engine.FFmpeg.Processes;
using TransFlux.Engine.FFmpeg.Runner;
using TransFlux.Engine.FFmpeg.Verifier;

namespace TransFlux.Engine.IntegrationTests;

public sealed class OutputFormatTests
{
    public static IEnumerable<object[]> VideoPairs()
        => from container in OutputFormatCatalog.VideoContainers
           from video in OutputFormatCatalog.VideoCodecs(container)
           from audio in OutputFormatCatalog.AudioCodecs(container)
           select new object[] { container, video, audio };
    public static IEnumerable<object[]> AudioPairs()
        => from container in OutputFormatCatalog.AudioContainers
           from audio in OutputFormatCatalog.AudioCodecs(container)
           select new object[] { container, audio };

    private static async Task<SourceSnapshot> SourceAsync()
    {
        var path = TestSupport.PathFor("format-matrix-source.mp4");
        if (!File.Exists(path))
        {
            var generated = await ProcessCapture.RunAsync(TestSupport.Core.FfmpegPath,
                ["-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=10:duration=0.6",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=0.6",
                    "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-colorspace", "bt709",
                    "-color_primaries", "bt709", "-color_trc", "bt709", "-c:a", "aac", path],
                TimeSpan.FromSeconds(20), 65536, 65536, default);
            Assert.Equal(0, generated.ExitCode);
        }
        var probe = await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = path }, default);
        Assert.True(probe.IsSuccess, probe.Failure?.TechnicalDetail);
        return probe.Value!;
    }

    [Theory]
    [MemberData(nameof(VideoPairs))]
    public async Task EveryAdvertisedVideoCombinationEncodesAndVerifies(ContainerFormat container, VideoCodec video, AudioCodec audio)
    {
        var source = await SourceAsync();
        await ConvertAndVerify(source, new ConversionRequest { SourceSnapshotId = source.MediaId,
            Container = container, VideoCodec = video, Speed = SpeedTier.Faster, Hardware = HardwarePreference.Cpu,
            Audio = new AudioSpec { Codec = audio } });
    }

    [Theory]
    [MemberData(nameof(AudioPairs))]
    public async Task EveryAdvertisedAudioCombinationEncodesAndVerifies(ContainerFormat container, AudioCodec audio)
    {
        var source = await SourceAsync();
        await ConvertAndVerify(source, new ConversionRequest { SourceSnapshotId = source.MediaId,
            Mode = ConversionMode.AudioExtract, Container = container, Audio = new AudioSpec { Codec = audio } });
    }

    private static async Task ConvertAndVerify(SourceSnapshot source, ConversionRequest request)
    {
        var hash = TestSupport.Sha256(source.AbsolutePath);
        var planned = await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, TestSupport.EmptyCapabilities(), default);
        Assert.True(planned.IsSuccess, planned.Failure?.TechnicalDetail);
        var plan = planned.Value!;
        var attempt = TestSupport.BuildAttempt(plan, source, request);
        var run = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, new CollectingProgress(), default);
        Assert.True(run.Succeeded, run.StderrTail);
        var verified = await new FfmpegVerifier(TestSupport.Core).VerifyAsync(new VerificationRequest
            { Plan = plan, Source = source, OutputPath = attempt.TempPath, FullDecode = true }, default);
        Assert.True(verified.Passed, verified.Summary);
        Assert.Equal(hash, TestSupport.Sha256(source.AbsolutePath));
        // Verification must detect a wrong selected codec, not just accept a decodable file.
        var wrong = plan with { ExpectedOutput = plan.ExpectedOutput with
            { AudioCodec = request.Audio.Codec == AudioCodec.Aac ? AudioCodec.Flac : AudioCodec.Aac } };
        var rejected = await new FfmpegVerifier(TestSupport.Core).VerifyAsync(new VerificationRequest
            { Plan = wrong, Source = source, OutputPath = attempt.TempPath }, default);
        Assert.False(rejected.Passed);
    }
}
