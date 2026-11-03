using System.Text.Json;
using TransFlux.App.State;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;
using TransFlux.Domain.Rules;

namespace TransFlux.Application.Tests;

public sealed class OutputSelectionTests
{
    [Fact]
    public void ContainerChangeKeepsCompatibleEncodingAndUnrelatedParameters()
    {
        var before = ConversionOptions.Default with { VideoCodec = VideoCodec.Hevc, AudioCodec = AudioCodec.Alac,
            Quality = QualityTier.HigherQuality, OutputDirectory = "chosen", TrimStart = TimeSpan.FromSeconds(1), TrimEnd = TimeSpan.FromSeconds(3) };
        var after = OutputTargets.WithContainer(before, ContainerFormat.Mov);
        Assert.Equal(VideoCodec.Hevc, after.VideoCodec); Assert.Equal(AudioCodec.Alac, after.AudioCodec);
        Assert.Equal(before.TrimStart, after.TrimStart); Assert.Equal("chosen", after.OutputDirectory);
        Assert.Equal(QualityTier.HigherQuality, after.Quality);
    }
    [Fact]
    public void WebmNormalizesIncompatibleVideoAndAudioCodecs()
    {
        var result = OutputTargets.WithContainer(ConversionOptions.Default with { CrfOverride = 20 }, ContainerFormat.Webm);
        Assert.Equal(VideoCodec.Vp9, result.VideoCodec); Assert.Equal(AudioCodec.Opus, result.AudioCodec); Assert.Null(result.CrfOverride);
    }
    [Fact]
    public void MediaSwitchChoosesAValidContainer_AndPreservesCompatibleSelections()
    {
        var trimStart = TimeSpan.FromSeconds(1); var trimEnd = TimeSpan.FromSeconds(3);
        var audio = OutputTargets.WithMedia(ConversionOptions.Default with { SelectedStreams = [3], TrimStart = trimStart, TrimEnd = trimEnd }, true);
        Assert.Equal(ConversionMode.AudioExtract, audio.Mode); Assert.Equal(ContainerFormat.M4a, audio.Container);
        Assert.Equal(new[] { 3 }, audio.SelectedStreams); Assert.Equal(trimStart, audio.TrimStart); Assert.Equal(trimEnd, audio.TrimEnd);
        var video = OutputTargets.WithMedia(audio, false);
        Assert.Equal(ContainerFormat.Mp4, video.Container); Assert.Equal(ConversionMode.VideoTranscode, video.Mode);
        Assert.Equal(new[] { 3 }, video.SelectedStreams); Assert.Equal(trimStart, video.TrimStart);
    }
    [Fact]
    public void SwitchingToAContainerWithoutVideoCodecsDoesNotThrow()
    {
        var result = OutputTargets.WithContainer(ConversionOptions.Default, ContainerFormat.Mp3);
        Assert.Equal(ContainerFormat.Mp3, result.Container);
        Assert.Equal(VideoCodec.H264, result.VideoCodec);
        Assert.Equal(AudioCodec.Mp3, result.AudioCodec);
    }
    [Fact]
    public void CopyAndTranscodeAreIndependentFromContainerSelection()
    {
        var copy = OutputTargets.WithVideoCodec(ConversionOptions.Default with { TrimStart = TimeSpan.Zero }, VideoCodec.Copy);
        Assert.Equal(ConversionMode.Remux, copy.Mode); Assert.Equal(AudioCodec.Copy, copy.AudioCodec); Assert.Null(copy.TrimStart);
        var transcode = OutputTargets.WithVideoCodec(copy, VideoCodec.Hevc);
        Assert.Equal(ConversionMode.VideoTranscode, transcode.Mode); Assert.Equal(VideoCodec.Hevc, transcode.VideoCodec);
        Assert.Equal(AudioCodec.Aac, transcode.AudioCodec); Assert.Equal(ContainerFormat.Mp4, transcode.Container);
    }
    [Fact]
    public void RequestAndPresetKeepExplicitVideoAndAudioEncodings()
    {
        var options = ConversionOptions.Default with { Container = ContainerFormat.Webm, VideoCodec = VideoCodec.Av1, AudioCodec = AudioCodec.Vorbis };
        var request = options.ToRequest(Guid.NewGuid());
        var roundtrip = JsonSerializer.Deserialize<ConversionRequest>(CanonicalJson.Serialize(request), CanonicalJson.Options)!;
        Assert.Equal(VideoCodec.Av1, roundtrip.EffectiveVideoCodec); Assert.Equal(AudioCodec.Vorbis, roundtrip.Audio.Codec);
        var template = JsonSerializer.Deserialize<PresetRequestTemplate>(CanonicalJson.Serialize(options.ToTemplate()), CanonicalJson.Options)!;
        Assert.Equal(VideoCodec.Av1, ConversionOptions.FromTemplate(template).VideoCodec);
        Assert.Equal(AudioCodec.Vorbis, ConversionOptions.FromRequest(roundtrip).AudioCodec);
    }
    [Fact]
    public void AudioEncodingChangeNormalizesOnlyUnsupportedRateAndBitrate()
    {
        var high = ConversionOptions.Default with { AudioSampleRate = 96000, AudioBitRateKbps = 512, AudioChannels = 2 };
        var mp3 = OutputTargets.WithAudioCodec(high, AudioCodec.Mp3);
        Assert.Null(mp3.AudioSampleRate); Assert.Equal(320, mp3.AudioBitRateKbps); Assert.Equal(2, mp3.AudioChannels);
        var opus = OutputTargets.WithAudioCodec(high with { AudioSampleRate = 44100 }, AudioCodec.Opus);
        Assert.Null(opus.AudioSampleRate); Assert.Equal(510, opus.AudioBitRateKbps);
        var compatible = OutputTargets.WithAudioCodec(high with { AudioSampleRate = 48000, AudioBitRateKbps = 128 }, AudioCodec.Opus);
        Assert.Equal(48000, compatible.AudioSampleRate); Assert.Equal(128, compatible.AudioBitRateKbps);
    }
    [Fact]
    public void LegacyRequestsPresetsAndLastUsedKeysStillResolve()
    {
        var old = JsonSerializer.Deserialize<ConversionRequest>("{\"sourceSnapshotId\":\"11111111-1111-1111-1111-111111111111\",\"container\":\"Mp4\"}", CanonicalJson.Options)!;
        Assert.Equal(VideoCodec.H264, old.VideoCodec);
        Assert.Equal(VideoCodec.H264, JsonSerializer.Deserialize<PresetRequestTemplate>("{}", CanonicalJson.Options)!.VideoCodec);
        foreach (var key in new[] { "mp4-h264", "mkv-h264", "mkv-remux", "m4a-aac", "flac", "wav" }) Assert.NotNull(TargetFormats.Find(key));
        var options = ConversionOptions.Default with { Container = ContainerFormat.Mkv, VideoCodec = VideoCodec.Av1, AudioCodec = AudioCodec.Opus };
        Assert.Equal(options.VideoCodec, TargetFormats.Find(TargetFormats.Find(options)!.Key)!.ApplyTo(ConversionOptions.Default).VideoCodec);
    }
    [Fact]
    public void SelectingAnAudioTrackDoesNotRemoveSubtitles()
    {
        var source = TestData.MakeSource() with
        {
            Streams =
            [
                new StreamDescriptor { SourceIndex = 0, Kind = MediaKind.Video, Codec = "h264", Width = 1920, Height = 1080, PixelFormat = "yuv420p" },
                new StreamDescriptor { SourceIndex = 1, Kind = MediaKind.Audio, Codec = "aac", Channels = 2, SampleRate = 48000 },
                new StreamDescriptor { SourceIndex = 2, Kind = MediaKind.Subtitle, Codec = "subrip", Language = "jpn" },
            ],
        };
        var options = ConversionOptions.Default with { SelectedStreams = [1] };
        var request = options.ToRequest(source.MediaId);
        Assert.Equal(2, request.RequestVersion);
        var subtitles = SubtitlePolicy.SelectForTranscode(request, source);
        var target = Assert.Single(subtitles.Included);
        Assert.Equal(2, target.Stream.SourceIndex);
    }

    [Fact]
    public void AudioExtractMultiTrackSelectionIsBlockedInsteadOfSilentlyTakingFirst()
    {
        var source = TestData.MakeSource() with
        {
            Streams =
            [
                new StreamDescriptor { SourceIndex = 0, Kind = MediaKind.Video, Codec = "h264", Width = 1920, Height = 1080, PixelFormat = "yuv420p" },
                new StreamDescriptor { SourceIndex = 1, Kind = MediaKind.Audio, Codec = "aac", Channels = 2, SampleRate = 48000 },
                new StreamDescriptor { SourceIndex = 3, Kind = MediaKind.Audio, Codec = "aac", Channels = 2, SampleRate = 48000 },
            ],
        };
        var options = ConversionOptions.Default with
        {
            Mode = ConversionMode.AudioExtract, Container = ContainerFormat.M4a,
            AudioCodec = AudioCodec.Aac, SelectedStreams = [1, 3],
        };
        var request = options.ToRequest(source.MediaId);
        Assert.Equal(2, request.RequestVersion);
        var issues = MediaCompatibility.Validate(request, source);
        var blocking = Assert.Single(issues, i => i.Blocking);
        Assert.Contains("一次只输出一条音轨", blocking.Message);
    }

    [Theory]
    [InlineData(ContainerFormat.Webm, VideoCodec.H264, AudioCodec.Opus)]
    [InlineData(ContainerFormat.Webm, VideoCodec.Vp9, AudioCodec.Aac)]
    [InlineData(ContainerFormat.Mp4, VideoCodec.Vp9, AudioCodec.Aac)]
    [InlineData(ContainerFormat.Mov, VideoCodec.Av1, AudioCodec.Aac)]
    public void IncompatibleCombinationsAreBlockedBeforeExecution(ContainerFormat container, VideoCodec video, AudioCodec audio)
    {
        var source = TestData.MakeSource() with { ColorClassification = TransFlux.Domain.Media.ColorClassification.SdrBt709 };
        var request = new ConversionRequest { SourceSnapshotId = source.MediaId, Container = container,
            VideoCodec = video, Audio = new AudioSpec { Codec = audio } };
        Assert.Contains(MediaCompatibility.Validate(request, source), i => i.Code == "CODEC_CONTAINER" && i.Blocking);
    }
    [Fact]
    public void NewRequestsAreVersionTwoWithIndependentAudioAndSubtitleSelections()
    {
        var options = ConversionOptions.Default with
        {
            AudioMode = AudioSelectionMode.Explicit, AudioStreamIndices = [1, 3],
            SubtitleMode = SubtitleSelectionMode.Explicit, SubtitleStreamIndices = [2],
            SelectedStreams = [1, 3],
        };
        var request = options.ToRequest(Guid.NewGuid());
        Assert.Equal(2, request.RequestVersion);
        Assert.Empty(request.SelectedStreamIndices);
        Assert.Equal(AudioSelectionMode.Explicit, request.AudioTracks.Mode);
        Assert.Equal(new[] { 1, 3 }, request.AudioTracks.SourceStreamIndices);
        Assert.Equal(SubtitleSelectionMode.Explicit, request.Subtitles.Mode);
        Assert.Equal(new[] { 2 }, request.Subtitles.SourceStreamIndices);

        var roundtrip = JsonSerializer.Deserialize<ConversionRequest>(CanonicalJson.Serialize(request), CanonicalJson.Options)!;
        Assert.Equal(request.AudioTracks.SourceStreamIndices, roundtrip.AudioTracks.SourceStreamIndices);
        Assert.Equal(request.Subtitles.SourceStreamIndices, roundtrip.Subtitles.SourceStreamIndices);
        var fromRequest = ConversionOptions.FromRequest(roundtrip);
        Assert.Equal(AudioSelectionMode.Explicit, fromRequest.AudioMode);
        Assert.Equal(new[] { 2 }, fromRequest.SubtitleStreamIndices);
    }

    [Fact]
    public void LegacyRequestRemainsDisplayable_AndAudioIndicesDoNotBecomeSubtitleIndices()
    {
        var legacy = JsonSerializer.Deserialize<ConversionRequest>(
            """{"sourceSnapshotId":"11111111-1111-1111-1111-111111111111","selectedStreamIndices":[1],"container":"Mp4"}""",
            CanonicalJson.Options)!;
        Assert.Equal(1, legacy.RequestVersion);
        var options = ConversionOptions.FromRequest(legacy);
        Assert.Equal(AudioSelectionMode.Explicit, options.AudioMode);
        Assert.Equal(new[] { 1 }, options.AudioStreamIndices);
        Assert.Equal(new[] { 1 }, options.SelectedStreams);
        Assert.Equal(SubtitleSelectionMode.AutoPreserve, options.SubtitleMode);
        Assert.Empty(options.SubtitleStreamIndices);

        var upgraded = options.ToRequest(Guid.NewGuid());
        Assert.Equal(2, upgraded.RequestVersion);
        Assert.Equal(new[] { 1 }, upgraded.AudioTracks.SourceStreamIndices);
        Assert.Equal(SubtitleSelectionMode.AutoPreserve, upgraded.Subtitles.Mode);
        Assert.Empty(upgraded.Subtitles.SourceStreamIndices);
    }

    [Fact]
    public void UnknownFutureRequestVersionIsBlocked()
    {
        var source = TestData.MakeSource();
        var request = TestData.MakeRequest(source.MediaId) with { RequestVersion = 99 };
        var issue = Assert.Single(MediaCompatibility.Validate(request, source));
        Assert.True(issue.Blocking);
        Assert.Equal("REQUEST_VERSION", issue.Code);
    }

    [Fact]
    public void PresetRoundTripRetainsSourceSpecificAudioAndSubtitleSelections()
    {
        var options = ConversionOptions.Default with
        {
            AudioMode = AudioSelectionMode.Explicit, AudioStreamIndices = [1],
            SubtitleMode = SubtitleSelectionMode.None, SelectedStreams = [1],
        };
        var applied = options.ApplyPresetTemplate(options.ToTemplate());
        Assert.Equal(AudioSelectionMode.Explicit, applied.AudioMode);
        Assert.Equal(new[] { 1 }, applied.AudioStreamIndices);
        Assert.Equal(SubtitleSelectionMode.None, applied.SubtitleMode);
        Assert.Equal(new[] { 1 }, applied.SelectedStreams);
    }
    [Fact]
    public void AudioAndSubtitleModeChangesStayIndependent()
    {
        var options = ConversionOptions.Default with
        {
            AudioMode = AudioSelectionMode.Explicit, AudioStreamIndices = [1, 3], SelectedStreams = [1, 3],
            SubtitleMode = SubtitleSelectionMode.Explicit, SubtitleStreamIndices = [2],
        };
        var audioAuto = OutputTargets.WithAudioMode(options, AudioSelectionMode.Auto);
        Assert.Empty(audioAuto.AudioStreamIndices);
        Assert.Empty(audioAuto.SelectedStreams);
        Assert.Equal(SubtitleSelectionMode.Explicit, audioAuto.SubtitleMode);
        Assert.Equal(new[] { 2 }, audioAuto.SubtitleStreamIndices);

        var subtitleNone = OutputTargets.WithSubtitleMode(options, SubtitleSelectionMode.None);
        Assert.Empty(subtitleNone.SubtitleStreamIndices);
        Assert.Equal(new[] { 1, 3 }, subtitleNone.AudioStreamIndices);
    }
}