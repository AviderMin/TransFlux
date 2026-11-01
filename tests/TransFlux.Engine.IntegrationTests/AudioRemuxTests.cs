using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Engine.FFmpeg.Planner;
using TransFlux.Engine.FFmpeg.Probe;
using TransFlux.Engine.FFmpeg.Runner;
using TransFlux.Engine.FFmpeg.Verifier;
using TransFlux.Engine.FFmpeg.Processes;

namespace TransFlux.Engine.IntegrationTests;

public class AudioRemuxTests
{
    [Theory]
    [InlineData(MetadataPolicy.StripPersonalTags, false)]
    [InlineData(MetadataPolicy.PreserveTitleAndChapters, true)]
    public async Task RemuxHonorsMetadataPolicyInTheActualOutput(MetadataPolicy policy, bool preserve)
    {
        var original = await TestSupport.GenerateVideoAsync("metadata-original.mp4");
        var path = TestSupport.PathFor($"metadata-source-{policy}.mkv");
        var generated = await ProcessCapture.RunAsync(TestSupport.Core.FfmpegPath,
            ["-hide_banner", "-y", "-i", original, "-c", "copy", "-metadata", "comment=private-marker",
                "-metadata:s:v:0", "title=private-marker", path], TimeSpan.FromSeconds(15), 65536, 65536, default);
        Assert.Equal(0, generated.ExitCode);
        var source = (await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = path }, default)).Value!;
        var request = new ConversionRequest { SourceSnapshotId = source.MediaId, Mode = ConversionMode.Remux,
            Container = ContainerFormat.Mkv, Metadata = policy };
        var plan = (await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, TestSupport.EmptyCapabilities(), default)).Value!;
        var attempt = TestSupport.BuildAttempt(plan, source, request);
        var result = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, new CollectingProgress(), default);
        Assert.True(result.Succeeded, result.StderrTail);
        var tags = await ProcessCapture.RunAsync(TestSupport.Core.FfprobePath,
            ["-v", "error", "-show_entries", "format_tags:stream_tags", "-of", "json", attempt.TempPath],
            TimeSpan.FromSeconds(15), 65536, 65536, default);
        Assert.Equal(preserve, tags.Stdout.Contains("private-marker", StringComparison.Ordinal));
    }

    private static async Task<SourceSnapshot> ProbeSampleAsync(string name = "audio-remux-src.mp4")
    {
        var path = await TestSupport.GenerateVideoAsync(name);
        var r = await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = path }, CancellationToken.None);
        Assert.True(r.IsSuccess, r.Failure?.TechnicalDetail);
        return r.Value!;
    }

    [Theory]
    [InlineData(ContainerFormat.M4a, AudioCodec.Aac)]
    [InlineData(ContainerFormat.Flac, AudioCodec.Flac)]
    [InlineData(ContainerFormat.Wav, AudioCodec.PcmS16le)]
    public async Task AudioExtract_ProducesAudioOnlyAndVerifies(ContainerFormat container, AudioCodec codec)
    {
        var source = await ProbeSampleAsync($"audio-src-{container}.mp4");
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            Mode = ConversionMode.AudioExtract,
            Container = container,
            Audio = new AudioSpec { Codec = codec, BitRateKbps = 192 },
        };

        var planResult = await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(planResult.IsSuccess, planResult.Failure?.TechnicalDetail);
        var plan = planResult.Value!;

        var attempt = TestSupport.BuildAttempt(plan, source, request);
        var run = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, new CollectingProgress(), CancellationToken.None);
        Assert.True(run.Succeeded, run.Failure?.TechnicalDetail ?? run.StderrTail);

        var outputProbe = await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = attempt.TempPath }, CancellationToken.None);
        Assert.True(outputProbe.IsSuccess);
        var output = outputProbe.Value!;
        Assert.Empty(output.VideoStreams);
        Assert.Single(output.AudioStreams);

        var verification = await new FfmpegVerifier(TestSupport.Core).VerifyAsync(new VerificationRequest
        {
            Plan = plan,
            OutputPath = attempt.TempPath,
            Source = source,
        }, CancellationToken.None);
        Assert.True(verification.Passed, verification.Summary);
    }

    [Fact]
    public async Task Remux_Mp4_To_Mkv_CopiesStreams()
    {
        var source = await ProbeSampleAsync("remux-src.mp4");
        var sourcePath = source.AbsolutePath;
        var hash = TestSupport.Sha256(sourcePath);
        var request = new ConversionRequest
        {
            SourceSnapshotId = source.MediaId,
            Mode = ConversionMode.Remux,
            Container = ContainerFormat.Mkv,
        };

        var planResult = await new FfmpegPlanner(TestSupport.Core).BuildAsync(request, source, TestSupport.EmptyCapabilities(), CancellationToken.None);
        Assert.True(planResult.IsSuccess, planResult.Failure?.TechnicalDetail);
        var plan = planResult.Value!;

        var attempt = TestSupport.BuildAttempt(plan, source, request);
        var run = await new FfmpegRunner(TestSupport.Core).RunAsync(attempt, new CollectingProgress(), CancellationToken.None);
        Assert.True(run.Succeeded, run.Failure?.TechnicalDetail ?? run.StderrTail);

        var outputProbe = await new FfmpegProbe(TestSupport.Core).ProbeAsync(new SourceReference { AbsolutePath = attempt.TempPath }, CancellationToken.None);
        Assert.True(outputProbe.IsSuccess);
        Assert.Equal("h264", outputProbe.Value!.PrimaryVideo!.Codec);
        Assert.Equal("aac", outputProbe.Value.PrimaryAudio!.Codec);

        var verification = await new FfmpegVerifier(TestSupport.Core).VerifyAsync(new VerificationRequest
        {
            Plan = plan,
            OutputPath = attempt.TempPath,
            Source = source,
        }, CancellationToken.None);
        Assert.True(verification.Passed, verification.Summary);
        Assert.Equal(plan.ExpectedOutput.StreamCount, outputProbe.Value.Streams.Count);
        var wrongStreamCount = plan with
        {
            ExpectedOutput = plan.ExpectedOutput with { StreamCount = plan.ExpectedOutput.StreamCount + 1 },
        };
        var rejected = await new FfmpegVerifier(TestSupport.Core).VerifyAsync(new VerificationRequest
        {
            Plan = wrongStreamCount,
            OutputPath = attempt.TempPath,
            Source = source,
        }, CancellationToken.None);
        Assert.False(rejected.Passed);
        Assert.Contains("subtitle stream count", rejected.Summary);
        Assert.Equal(hash, TestSupport.Sha256(sourcePath));
    }
}
