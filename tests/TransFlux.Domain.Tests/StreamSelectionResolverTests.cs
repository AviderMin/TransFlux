using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.Domain.Tests;

/// <summary>Versioned stream-selection resolution tests (T02/R03/R07).</summary>
public sealed class StreamSelectionResolverTests
{
    private static SourceSnapshot Source() => TestData.Source(new[]
    {
        TestData.Video(index: 0), TestData.Audio(index: 1), TestData.Audio(index: 3),
        TestData.Subtitle(index: 2, codec: "subrip"),
    });

    [Fact]
    public void V2_IgnoresLegacySelectedStreamIndices()
    {
        var request = TestData.Request(requestVersion: 2, selected: new[] { 1 });
        var resolved = StreamSelectionResolver.Resolve(request, Source());
        Assert.Equal(new[] { 1, 3 }, resolved.Audio.Select(a => a.SourceIndex));
        Assert.Equal(new[] { 2 }, resolved.Subtitles.Select(s => s.SourceIndex));
    }

    [Fact]
    public void V1_NonEmptySelection_StillFiltersAudioAndSubtitlesTogether()
    {
        var resolved = StreamSelectionResolver.Resolve(TestData.Request(selected: new[] { 1 }), Source());
        Assert.Equal(new[] { 1 }, resolved.Audio.Select(a => a.SourceIndex));
        Assert.Empty(resolved.Subtitles);
    }

    [Fact]
    public void AudioExtractV2_MultipleExplicitTracks_IsBlockedWithSingleTrackMessage()
    {
        var request = TestData.Request(mode: ConversionMode.AudioExtract, container: ContainerFormat.M4a,
            requestVersion: 2, audioTracks: AudioSelection.Tracks(1, 3));
        var blocking = Assert.Single(MediaCompatibility.Validate(request, Source()), i => i.Blocking);
        Assert.Equal("AUDIO_EXTRACT_SINGLE", blocking.Code);
        Assert.Contains("一次只输出一条音轨", blocking.Message);
    }

    [Fact]
    public void AudioExtractV1_MultipleSelectedAudio_KeepsLegacySingleTrackBehavior()
    {
        var request = TestData.Request(mode: ConversionMode.AudioExtract, container: ContainerFormat.M4a,
            selected: new[] { 1, 3 });
        Assert.DoesNotContain(MediaCompatibility.Validate(request, Source()), i => i.Code == "AUDIO_EXTRACT_SINGLE");
        Assert.Equal(1, MediaCompatibility.SelectPrimaryAudio(request, Source())!.SourceIndex);
    }
}
