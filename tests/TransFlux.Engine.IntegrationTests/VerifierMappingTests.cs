using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Engine.FFmpeg.Verifier;

namespace TransFlux.Engine.IntegrationTests;

public class VerifierMappingTests
{
    [Fact]
    public void AddDecodeMaps_MapsEveryVideoAndAudioTrackExplicitly()
    {
        var snapshot = new SourceSnapshot
        {
            MediaId = Guid.NewGuid(),
            AbsolutePath = @"C:\media\multi.mkv",
            Streams = new[]
            {
                new StreamDescriptor { SourceIndex = 0, Kind = MediaKind.Video, Codec = "h264" },
                new StreamDescriptor { SourceIndex = 2, Kind = MediaKind.Video, Codec = "h264" },
                new StreamDescriptor { SourceIndex = 1, Kind = MediaKind.Audio, Codec = "aac" },
                new StreamDescriptor { SourceIndex = 3, Kind = MediaKind.Audio, Codec = "ac3" },
                new StreamDescriptor { SourceIndex = 4, Kind = MediaKind.Subtitle, Codec = "subrip" },
            },
        };

        var args = new List<string>();
        FfmpegVerifier.AddDecodeMaps(args, snapshot);

        // Global ffprobe indices, not v:N/a:N ordinals (attached pictures count as video ordinals).
        Assert.Equal(new[] { "-map", "0:0", "-map", "0:2", "-map", "0:1", "-map", "0:3" }, args);
    }

    [Fact]
    public async Task SampleDecodeAsync_SubtitleOnlySnapshot_ReturnsOkWithoutRunningFfmpeg()
    {
        var snapshot = new SourceSnapshot
        {
            MediaId = Guid.NewGuid(),
            AbsolutePath = @"C:mediasubtitles-only.mkv",
            Streams = new[]
            {
                new StreamDescriptor { SourceIndex = 0, Kind = MediaKind.Subtitle, Codec = "subrip" },
            },
        };

        var verifier = new FfmpegVerifier(TestSupport.Core);
        var result = await verifier.SampleDecodeAsync(@"C:definitely-missingoutput.mkv", snapshot, full: false, CancellationToken.None);

        Assert.True(result.Ok, "no A/V stream means there is nothing to decode; structural Compare owns the check");
        Assert.False(result.Canceled);
    }

    // ---------------- R08: per-track signatures beyond stream counts ----------------

    private static SourceSnapshot Output(params StreamDescriptor[] streams) => new()
    {
        MediaId = Guid.NewGuid(),
        AbsolutePath = @"C:\media\out.mkv",
        Streams = streams,
        Format = new MediaFormat { FormatName = "matroska" },
    };

    private static ExpectedStreamSignature Video(string codec) => new()
    {
        Kind = MediaKind.Video,
        Codec = codec,
    };

    private static ExpectedStreamSignature Audio(string codec, int channels, bool isDefault, bool isForced = false) => new()
    {
        Kind = MediaKind.Audio,
        Codec = codec,
        Channels = channels,
        IsDefault = isDefault,
        IsForced = isForced,
    };

    private static ExpectedOutput Expected(params ExpectedStreamSignature[] signatures) => new()
    {
        Container = ContainerFormat.Mkv,
        StreamCount = signatures.Length,
        VideoStreamCount = signatures.Count(s => s.Kind == MediaKind.Video),
        AudioStreamCount = signatures.Count(s => s.Kind == MediaKind.Audio),
        TrackSignatures = signatures,
    };

    [Fact]
    public void Compare_AcceptsMatchingTrackSignatures()
    {
        var expected = Expected(
            Video("h264"),
            Audio("aac", 2, isDefault: true),
            Audio("ac3", 6, isDefault: false, isForced: true));
        var actual = Output(
            new StreamDescriptor { SourceIndex = 0, Kind = MediaKind.Video, Codec = "h264" },
            new StreamDescriptor { SourceIndex = 1, Kind = MediaKind.Audio, Codec = "aac", Channels = 2, IsDefault = true },
            new StreamDescriptor { SourceIndex = 2, Kind = MediaKind.Audio, Codec = "ac3", Channels = 6, IsForced = true });

        Assert.Empty(FfmpegVerifier.Compare(expected, actual, @"C:\media\out.mkv"));
    }

    [Fact]
    public void Compare_DetectsAudioChannelCountMismatch()
    {
        var expected = Expected(Video("h264"), Audio("aac", 2, isDefault: true));
        var actual = Output(
            new StreamDescriptor { SourceIndex = 0, Kind = MediaKind.Video, Codec = "h264" },
            new StreamDescriptor { SourceIndex = 1, Kind = MediaKind.Audio, Codec = "aac", Channels = 1, IsDefault = true });

        var problems = FfmpegVerifier.Compare(expected, actual, @"C:\media\out.mkv");

        Assert.Contains(problems, p => p.Contains("audio track 1") && p.Contains("channels 1 != expected 2"));
    }

    [Fact]
    public void Compare_DetectsDefaultAndForcedDispositionReversal()
    {
        var expected = Expected(
            Video("h264"),
            Audio("aac", 2, isDefault: true),
            Audio("aac", 2, isDefault: false, isForced: true));
        var actual = Output(
            new StreamDescriptor { SourceIndex = 0, Kind = MediaKind.Video, Codec = "h264" },
            new StreamDescriptor { SourceIndex = 1, Kind = MediaKind.Audio, Codec = "aac", Channels = 2, IsDefault = false },
            new StreamDescriptor { SourceIndex = 2, Kind = MediaKind.Audio, Codec = "aac", Channels = 2, IsDefault = true, IsForced = false });

        var problems = FfmpegVerifier.Compare(expected, actual, @"C:\media\out.mkv");

        Assert.Contains(problems, p => p.Contains("audio track 1") && p.Contains("default disposition False != expected True"));
        Assert.Contains(problems, p => p.Contains("audio track 2") && p.Contains("default disposition True != expected False"));
        Assert.Contains(problems, p => p.Contains("audio track 2") && p.Contains("forced disposition False != expected True"));
    }

    [Fact]
    public void Compare_DetectsSwappedTrackOrderByCodec()
    {
        var expected = Expected(Video("h264"), Audio("aac", 2, isDefault: true), Audio("ac3", 6, isDefault: false));
        var actual = Output(
            new StreamDescriptor { SourceIndex = 0, Kind = MediaKind.Video, Codec = "h264" },
            new StreamDescriptor { SourceIndex = 1, Kind = MediaKind.Audio, Codec = "ac3", Channels = 6 },
            new StreamDescriptor { SourceIndex = 2, Kind = MediaKind.Audio, Codec = "aac", Channels = 2, IsDefault = true });

        var problems = FfmpegVerifier.Compare(expected, actual, @"C:\media\out.mkv");

        Assert.Contains(problems, p => p.Contains("audio track 1") && p.Contains("codec 'ac3' != expected 'aac'"));
        Assert.Contains(problems, p => p.Contains("audio track 2") && p.Contains("codec 'aac' != expected 'ac3'"));
    }

    // ---------------- R04: Dolby Vision DOVI/RPU retention ----------------

    private static ExpectedOutput DvExpected() => new()
    {
        Container = ContainerFormat.Mkv,
        StreamCount = 1,
        VideoStreamCount = 1,
        TrackSignatures = new[] { Video("hevc") },
        DolbyVision = new DolbyVisionExpectation
        {
            Profile = 8,
            BlSignalCompatibilityId = 1,
            RpuRequired = true,
            ElRequired = true,
            BlRequired = true,
        },
    };

    [Fact]
    public void Compare_AcceptsRetainedDolbyVisionConfiguration()
    {
        var actual = Output(new StreamDescriptor
        {
            SourceIndex = 0, Kind = MediaKind.Video, Codec = "hevc",
            DvProfile = 8, DvBlSignalCompatibilityId = 1, DvRpuPresent = true,
            DvElPresent = true, DvBlPresent = true,
        });

        Assert.Empty(FfmpegVerifier.Compare(DvExpected(), actual, @"C:\media\out.mkv"));
    }

    [Fact]
    public void Compare_DetectsLostDolbyVisionConfigurationRecord()
    {
        var actual = Output(new StreamDescriptor { SourceIndex = 0, Kind = MediaKind.Video, Codec = "hevc" });

        var problems = FfmpegVerifier.Compare(DvExpected(), actual, @"C:\media\out.mkv");

        Assert.Contains(problems, p => p.Contains("Dolby Vision") && p.Contains("missing"));
    }

    [Fact]
    public void Compare_DetectsLostDolbyVisionRpuFlag()
    {
        var actual = Output(new StreamDescriptor
        {
            SourceIndex = 0, Kind = MediaKind.Video, Codec = "hevc",
            DvProfile = 8, DvBlSignalCompatibilityId = 1, DvRpuPresent = false,
        });

        var problems = FfmpegVerifier.Compare(DvExpected(), actual, @"C:\media\out.mkv");

        Assert.Contains(problems, p => p.Contains("RPU present flag missing"));
    }

    [Fact]
    public void Compare_DetectsLostDolbyVisionEnhancementLayerFlag()
    {
        var actual = Output(new StreamDescriptor
        {
            SourceIndex = 0, Kind = MediaKind.Video, Codec = "hevc",
            DvProfile = 8, DvBlSignalCompatibilityId = 1, DvRpuPresent = true,
            DvElPresent = false, DvBlPresent = true,
        });

        var problems = FfmpegVerifier.Compare(DvExpected(), actual, @"C:\media\out.mkv");

        Assert.Contains(problems, p => p.Contains("enhancement layer present flag missing"));
    }

    [Fact]
    public void Compare_DetectsLostDolbyVisionBaseLayerFlag()
    {
        var actual = Output(new StreamDescriptor
        {
            SourceIndex = 0, Kind = MediaKind.Video, Codec = "hevc",
            DvProfile = 8, DvBlSignalCompatibilityId = 1, DvRpuPresent = true,
            DvElPresent = true, DvBlPresent = false,
        });

        var problems = FfmpegVerifier.Compare(DvExpected(), actual, @"C:\media\out.mkv");

        Assert.Contains(problems, p => p.Contains("base layer present flag missing"));
    }

    [Fact]
    public void Compare_DetectsWrongDolbyVisionProfileAndCompatibilityId()
    {
        var actual = Output(new StreamDescriptor
        {
            SourceIndex = 0, Kind = MediaKind.Video, Codec = "hevc",
            DvProfile = 5, DvBlSignalCompatibilityId = 0, DvRpuPresent = true,
        });

        var problems = FfmpegVerifier.Compare(DvExpected(), actual, @"C:\media\out.mkv");

        Assert.Contains(problems, p => p.Contains("Dolby Vision profile 5 != expected 8"));
        Assert.Contains(problems, p => p.Contains("BL signal compatibility id 0 != expected 1"));
    }
}
