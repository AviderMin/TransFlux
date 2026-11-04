using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.Domain.Tests;

public class MediaCompatibilityTests
{
    private static bool Has(IReadOnlyList<CompatibilityIssue> issues, string code) =>
        issues.Any(i => i.Code == code);

    private static SourceSnapshot BasicSdrSource() =>
        TestData.Source(new[] { TestData.Video(), TestData.Audio() });

    [Fact]
    public void NewRequest_DefaultsToVersionTwo()
    {
        var request = new ConversionRequest { SourceSnapshotId = Guid.NewGuid() };
        Assert.Equal(2, request.RequestVersion);
    }

    // ---------------- Video transcode ----------------

    [Fact]
    public void Transcode_SdrBt709_8Bit_Yuv420p_HasNoIssues()
    {
        var issues = MediaCompatibility.Validate(TestData.Request(), BasicSdrSource());
        Assert.Empty(issues);
    }

    [Fact]
    public void Transcode_HdrSource_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() }, ColorClassification.Hdr10);
        var issues = MediaCompatibility.Validate(TestData.Request(), src);
        Assert.True(Has(issues, "NON_SDR"));
        Assert.All(issues, i => Assert.True(i.Blocking));
    }

    [Fact]
    public void Transcode_UnknownColor_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() }, ColorClassification.Unknown);
        var issues = MediaCompatibility.Validate(TestData.Request(), src);
        Assert.True(Has(issues, "NON_SDR"));
    }

    [Fact]
    public void Transcode_NoVideo_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Audio() });
        var issues = MediaCompatibility.Validate(TestData.Request(), src);
        Assert.True(Has(issues, "NO_VIDEO"));
    }

    [Fact]
    public void Transcode_NonVideoContainer_IsBlocked()
    {
        var issues = MediaCompatibility.Validate(
            TestData.Request(container: ContainerFormat.M4a), BasicSdrSource());
        Assert.True(Has(issues, "BAD_CONTAINER"));
    }

    [Fact]
    public void Transcode_HighBitDepth_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(bitDepth: 10), TestData.Audio() });
        var issues = MediaCompatibility.Validate(TestData.Request(), src);
        Assert.True(Has(issues, "HIGH_BIT_DEPTH"));
    }

    [Fact]
    public void Transcode_NonYuv420p_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(pixelFormat: "yuv444p"), TestData.Audio() });
        var issues = MediaCompatibility.Validate(TestData.Request(), src);
        Assert.True(Has(issues, "PIX_FMT"));
    }

    [Fact]
    public void Transcode_Nv12AndYuvj420p_AreAccepted()
    {
        foreach (var pix in new[] { "nv12", "yuvj420p" })
        {
            var src = TestData.Source(new[] { TestData.Video(pixelFormat: pix), TestData.Audio() });
            Assert.Empty(MediaCompatibility.Validate(TestData.Request(), src));
        }
    }

    [Fact]
    public void Transcode_V2ExplicitEmptyAudio_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(requestVersion: 2, audioTracks: new AudioSelection { Mode = AudioSelectionMode.Explicit }),
            src);
        Assert.True(Has(issues, "AUDIO_SELECTION"));
    }

    [Fact]
    public void Transcode_V2ExplicitAudioTrack_IsAllowed()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(requestVersion: 2, audioTracks: AudioSelection.Tracks(1)),
            src);
        Assert.Empty(issues);
    }

    [Fact]
    public void Transcode_V2ExplicitAudioAllInvalidIndices_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(requestVersion: 2, audioTracks: AudioSelection.Tracks(99)),
            src);

        Assert.True(Has(issues, "AUDIO_SELECTION"));
        Assert.Contains(issues, i => i.Code == "AUDIO_SELECTION" && i.Blocking);
    }

    [Fact]
    public void Transcode_V2ExplicitAudioMixedValidAndInvalid_IsAllowed()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(requestVersion: 2, audioTracks: AudioSelection.Tracks(99, 1)),
            src);
        Assert.DoesNotContain(issues, i => i.Code == "AUDIO_SELECTION");
    }

    // ---------------- G2: audio copy during video transcode ----------------

    [Fact]
    public void AudioCopyCapability_IsMkvOnlyAndNotInEncodeList()
    {
        Assert.True(OutputFormatCatalog.SupportsAudioCopy(ContainerFormat.Mkv));
        Assert.False(OutputFormatCatalog.SupportsAudioCopy(ContainerFormat.Mp4));
        Assert.False(OutputFormatCatalog.SupportsAudioCopy(ContainerFormat.Mov));
        Assert.False(OutputFormatCatalog.SupportsAudioCopy(ContainerFormat.Webm));
        Assert.DoesNotContain(AudioCodec.Copy, OutputFormatCatalog.AudioCodecs(ContainerFormat.Mkv));
    }

    [Theory]
    [InlineData("ac3")]
    [InlineData("eac3")]
    [InlineData("truehd")]
    public void Transcode_AudioCopy_Mkv_SupportedSourceCodec_IsAllowed(string sourceCodec)
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio(codec: sourceCodec) });
        var issues = MediaCompatibility.Validate(
            TestData.Request(container: ContainerFormat.Mkv, audioCodec: AudioCodec.Copy), src);

        Assert.Empty(issues);
    }

    [Fact]
    public void Transcode_AudioCopy_Mp4_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio(codec: "ac3") });
        var issues = MediaCompatibility.Validate(
            TestData.Request(container: ContainerFormat.Mp4, audioCodec: AudioCodec.Copy), src);

        Assert.True(Has(issues, "AUDIO_COPY_UNSUPPORTED"));
        Assert.Contains(issues, i => i.Code == "AUDIO_COPY_UNSUPPORTED" && i.Blocking);
    }

    [Fact]
    public void Transcode_AudioCopy_UnsupportedSourceCodec_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio(codec: "pcm_f32le") });
        var issues = MediaCompatibility.Validate(
            TestData.Request(container: ContainerFormat.Mkv, audioCodec: AudioCodec.Copy), src);

        Assert.True(Has(issues, "AUDIO_COPY_UNSUPPORTED"));
    }

    [Fact]
    public void Transcode_AudioCopy_OneUnsupportedOfMultipleTracks_IsBlocked()
    {
        var src = TestData.Source(new[]
        {
            TestData.Video(),
            TestData.Audio(index: 1, codec: "ac3"),
            TestData.Audio(index: 2, codec: "pcm_f32le"),
        });
        var issues = MediaCompatibility.Validate(
            TestData.Request(container: ContainerFormat.Mkv, audioCodec: AudioCodec.Copy), src);

        Assert.True(Has(issues, "AUDIO_COPY_UNSUPPORTED"));
    }

    [Fact]
    public void Transcode_AudioCopy_NoAudio_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(container: ContainerFormat.Mkv, audioCodec: AudioCodec.Copy), src);

        Assert.True(Has(issues, "AUDIO_COPY_UNSUPPORTED"));
    }

    [Fact]
    public void Transcode_AudioCopy_AudioNone_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio(codec: "ac3") });
        var issues = MediaCompatibility.Validate(
            TestData.Request(container: ContainerFormat.Mkv, audioCodec: AudioCodec.Copy,
                requestVersion: 2, audioTracks: AudioSelection.None), src);

        Assert.True(Has(issues, "AUDIO_COPY_UNSUPPORTED"));
    }

    [Fact]
    public void Transcode_V2SelectsNonPrimaryHdrVideo_IsBlocked()
    {
        var sdr = TestData.Video(index: 0, color: ColorClassification.SdrBt709);
        var hdr = TestData.Video(index: 1, bitDepth: 10, pixelFormat: "yuv420p10le",
            colorTransfer: "smpte2084", colorPrimaries: "bt2020", color: ColorClassification.Hdr10);
        var src = TestData.Source(new[] { sdr, hdr });
        var issues = MediaCompatibility.Validate(
            TestData.Request(requestVersion: 2, video: VideoSelection.Stream(1)), src);
        Assert.True(Has(issues, "NON_SDR"));
    }

    [Fact]
    public void Transcode_V2SelectsSdrTrackFromMixedSource_IsAllowed()
    {
        var hdr = TestData.Video(index: 0, bitDepth: 10, pixelFormat: "yuv420p10le",
            colorTransfer: "smpte2084", colorPrimaries: "bt2020", color: ColorClassification.Hdr10);
        var sdr = TestData.Video(index: 1, color: ColorClassification.SdrBt709);
        var src = TestData.Source(new[] { hdr, sdr }, ColorClassification.Hdr10);
        var issues = MediaCompatibility.Validate(
            TestData.Request(requestVersion: 2, video: VideoSelection.Stream(1)), src);
        Assert.False(Has(issues, "NON_SDR"));
    }

    [Fact]
    public void Transcode_TrimOutsideDuration_IsBlocked()
    {
        var trim = new TrimRange { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10) };
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() }, durationUs: 5_000_000);
        var issues = MediaCompatibility.Validate(TestData.Request(trim: trim), src);
        Assert.True(Has(issues, "TRIM_RANGE"));
    }

    [Fact]
    public void Transcode_ValidTrim_HasNoIssues()
    {
        var trim = TrimRange.Create(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() }, durationUs: 5_000_000);
        Assert.Empty(MediaCompatibility.Validate(TestData.Request(trim: trim), src));
    }

    // ---------------- Audio extract ----------------

    [Fact]
    public void AudioExtract_M4aAac_HasNoIssues()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.AudioExtract, container: ContainerFormat.M4a, audioCodec: AudioCodec.Aac),
            src);
        Assert.Empty(issues);
    }

    [Fact]
    public void AudioExtract_NoAudio_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.AudioExtract, container: ContainerFormat.M4a, audioCodec: AudioCodec.Aac),
            src);
        Assert.True(Has(issues, "NO_AUDIO"));
    }

    [Fact]
    public void AudioExtract_NonAudioContainer_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.AudioExtract, container: ContainerFormat.Mp4, audioCodec: AudioCodec.Aac),
            src);
        Assert.True(Has(issues, "BAD_CONTAINER"));
    }

    [Fact]
    public void AudioExtract_CodecMustMatchContainer()
    {
        var src = TestData.Source(new[] { TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.AudioExtract, container: ContainerFormat.Flac, audioCodec: AudioCodec.Aac),
            src);
        Assert.True(Has(issues, "CODEC_CONTAINER"));
    }

    [Fact]
    public void AudioExtract_FlacHighSampleRate_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Audio(codec: "flac", sampleRate: 384_000) });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.AudioExtract, container: ContainerFormat.Flac, audioCodec: AudioCodec.Flac),
            src);
        Assert.True(Has(issues, "SAMPLE_RATE"));
    }

    [Fact]
    public void AudioExtract_FlacNormalSampleRate_HasNoIssues()
    {
        var src = TestData.Source(new[] { TestData.Audio(codec: "flac", sampleRate: 96_000) });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.AudioExtract, container: ContainerFormat.Flac, audioCodec: AudioCodec.Flac),
            src);
        Assert.Empty(issues);
    }

    // ---------------- Remux ----------------

    [Fact]
    public void Remux_Trim_IsBlocked()
    {
        var trim = new TrimRange { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(3) };
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv, trim: trim), src);
        Assert.True(Has(issues, "TRIM_REMUX"));
    }

    [Fact]
    public void Remux_UnsupportedContainer_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.M4a), src);
        Assert.True(Has(issues, "BAD_CONTAINER"));
    }

    [Fact]
    public void Remux_EmptySource_IsBlocked()
    {
        var src = TestData.Source(Array.Empty<StreamDescriptor>());
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);
        Assert.True(Has(issues, "EMPTY"));
    }

    [Fact]
    public void Remux_V2ExplicitAudioAllInvalidIndices_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv,
                requestVersion: 2, audioTracks: AudioSelection.Tracks(99)),
            src);

        Assert.True(Has(issues, "AUDIO_SELECTION"));
        Assert.Contains(issues, i => i.Code == "AUDIO_SELECTION" && i.Blocking);
    }

    [Fact]
    public void Remux_V2ExplicitAudioMixedValidAndInvalid_IsAllowed()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv,
                requestVersion: 2, audioTracks: AudioSelection.Tracks(99, 1)),
            src);
        Assert.DoesNotContain(issues, i => i.Code == "AUDIO_SELECTION");
    }

    [Fact]
    public void Remux_H264ToMkv_HasNoIssues()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);
        Assert.Empty(issues);
    }

    [Fact]
    public void Remux_HevcToMkv_IsAllowed()
    {
        var src = TestData.Source(new[] { TestData.Video(codec: "hevc"), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);
        Assert.Empty(issues);
    }

    [Fact]
    public void Remux_Vp9ToMkv_IsAllowed()
    {
        var src = TestData.Source(new[] { TestData.Video(codec: "vp9"), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);
        Assert.Empty(issues);
    }

    [Fact]
    public void Remux_HevcToMp4_IsAllowed()
    {
        var src = TestData.Source(new[] { TestData.Video(codec: "hevc"), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mp4), src);
        Assert.Empty(issues);
    }

    [Fact]
    public void Remux_10BitToMp4_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(bitDepth: 10), TestData.Audio() });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mp4), src);
        Assert.True(Has(issues, "REMUX_DEPTH"));
    }

    [Fact]
    public void Remux_FlacAudioToMp4_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio(codec: "flac") });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mp4), src);
        Assert.True(Has(issues, "REMUX_AUDIO"));
    }

    [Fact]
    public void Remux_BitmapSubtitle_ToMkv_IsAllowed()
    {
        var src = TestData.Source(new[]
        {
            TestData.Video(), TestData.Audio(), TestData.Subtitle(codec: "hdmv_pgs_subtitle"),
        });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);
        Assert.Empty(issues);
    }

    [Fact]
    public void Remux_BitmapSubtitle_ToMp4_IsBlocked()
    {
        var src = TestData.Source(new[]
        {
            TestData.Video(), TestData.Audio(), TestData.Subtitle(codec: "hdmv_pgs_subtitle"),
        });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mp4), src);
        Assert.True(Has(issues, "REMUX_SUBTITLE_BITMAP"));
    }

    [Fact]
    public void Remux_AssToMp4_IsBlocked()
    {
        var src = TestData.Source(new[]
        {
            TestData.Video(), TestData.Audio(), TestData.Subtitle(codec: "ass"),
        });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mp4), src);
        Assert.True(Has(issues, "REMUX_SUBTITLE"));
    }

    [Fact]
    public void Remux_SubripToMkv_IsAllowed()
    {
        var src = TestData.Source(new[]
        {
            TestData.Video(), TestData.Audio(), TestData.Subtitle(codec: "subrip"),
        });
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);
        Assert.Empty(issues);
    }

    [Fact]
    public void Remux_DolbyVision_IsBlocked()
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() }, ColorClassification.DolbyVision);
        var issues = MediaCompatibility.Validate(
            TestData.Request(mode: ConversionMode.Remux, container: ContainerFormat.Mkv), src);
        Assert.True(Has(issues, "REMUX_DV"));
    }

    // ---------------- Stream selection ----------------

    [Fact]
    public void SelectPrimaryVideo_PrefersSelectedStreamIndex()
    {
        var v0 = TestData.Video(index: 0, isDefault: true);
        var v1 = TestData.Video(index: 1, isDefault: false);
        var src = TestData.Source(new[] { v0, v1 });
        var request = TestData.Request(selected: new[] { 1 });
        Assert.Same(v1, MediaCompatibility.SelectPrimaryVideo(request, src));
        Assert.Same(v0, MediaCompatibility.SelectPrimaryVideo(TestData.Request(), src));
    }

    [Fact]
    public void SelectPrimaryAudio_PrefersSelectedStreamIndex()
    {
        var a0 = TestData.Audio(index: 1, isDefault: true);
        var a1 = TestData.Audio(index: 2, channels: 6, channelLayout: "5.1", isDefault: false);
        var src = TestData.Source(new[] { TestData.Video(index: 0), a0, a1 });
        var request = TestData.Request(selected: new[] { 2 });
        Assert.Same(a1, MediaCompatibility.SelectPrimaryAudio(request, src));
        Assert.Same(a0, MediaCompatibility.SelectPrimaryAudio(TestData.Request(), src));
    }

    [Fact]
    public void IssueHelpers_SetBlockingFlag()
    {
        Assert.True(CompatibilityIssue.Block("x", "blocked").Blocking);
        Assert.False(CompatibilityIssue.Warn("y", "warning").Blocking);
    }

    // ---------------- N07: playlist/concat/image-sequence input formats ----------------

    [Theory]
    [InlineData("concat")]
    [InlineData("ffconcat")]
    [InlineData("hls")]
    [InlineData("applehttp")]
    [InlineData("dash")]
    [InlineData("image2")]
    [InlineData("image2pipe")]
    public void PlaylistConcatAndImageSequenceInputs_AreBlocked(string formatName)
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() }, formatName: formatName);
        var issues = MediaCompatibility.Validate(TestData.Request(), src);

        Assert.True(Has(issues, "INPUT_FORMAT"));
        Assert.Contains(issues, i => i.Code == "INPUT_FORMAT" && i.Blocking);
    }

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2")]
    [InlineData("matroska,webm")]
    [InlineData("ogg")]
    [InlineData("flac")]
    [InlineData("wav")]
    [InlineData("mp3")]
    [InlineData("aac")]
    public void NormalSingleFileInputFormats_AreNotRejected(string formatName)
    {
        var src = TestData.Source(new[] { TestData.Video(), TestData.Audio() }, formatName: formatName);
        var issues = MediaCompatibility.Validate(TestData.Request(), src);

        Assert.DoesNotContain(issues, i => i.Code == "INPUT_FORMAT");
    }
}
