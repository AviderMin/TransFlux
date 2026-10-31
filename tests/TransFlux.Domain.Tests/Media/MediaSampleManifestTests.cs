using TransFlux.Tests.Media;

namespace TransFlux.Domain.Tests.Media;

/// <summary>
/// Verifies the generated media sample manifest is present and internally consistent, and
/// that generated sample files (when present) still match their recorded SHA-256/size.
/// </summary>
public class MediaSampleManifestTests
{
    private static readonly string[] RequiredSampleIds =
        { "S01", "S02", "S03", "S04", "S05", "S06", "S07", "S08", "S09" };

    [Fact]
    public void Manifest_Exists_And_Parses()
    {
        Assert.True(File.Exists(MediaSampleLocator.ManifestPath),
            $"Manifest missing at {MediaSampleLocator.ManifestPath}. Run tests/media/generate-samples.ps1.");
        var manifest = MediaSampleLocator.LoadManifest();
        Assert.True(manifest.SchemaVersion >= 1);
        Assert.NotEmpty(manifest.Samples);
    }

    [Fact]
    public void Manifest_CoversRequiredSampleIds()
    {
        var ids = MediaSampleLocator.LoadManifest().Samples
            .Select(s => s.SampleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in RequiredSampleIds)
            Assert.Contains(required, ids);
    }

    [Fact]
    public void Manifest_Entries_HavePurposeAndValidHashFormat()
    {
        foreach (var sample in MediaSampleLocator.LoadManifest().Samples)
        {
            Assert.False(string.IsNullOrWhiteSpace(sample.Purpose), $"{sample.SampleId}: purpose missing");
            Assert.False(string.IsNullOrWhiteSpace(sample.Path), $"{sample.SampleId}: path missing");
            Assert.False(string.IsNullOrWhiteSpace(sample.SampleId), "sampleId missing");
            if (!string.IsNullOrEmpty(sample.Sha256))
                Assert.Matches("^[0-9a-f]{64}$", sample.Sha256);
        }
    }

    [Fact]
    public void GeneratedSamples_WhenPresent_MatchRecordedHashAndSize()
    {
        var manifest = MediaSampleLocator.LoadManifest();
        var existing = manifest.Samples
            .Select(s => (Sample: s, FullPath: MediaSampleLocator.ResolveSamplePath(s)))
            .Where(x => File.Exists(x.FullPath))
            .ToList();

        if (existing.Count == 0 && !Directory.Exists(MediaSampleLocator.SamplesDirectory))
            return; // samples not generated in this checkout; structural checks above still ran

        Assert.NotEmpty(existing);
        foreach (var (sample, fullPath) in existing)
        {
            Assert.Equal(sample.SizeBytes, new FileInfo(fullPath).Length);
            if (!string.IsNullOrEmpty(sample.Sha256))
                Assert.Equal(sample.Sha256, MediaSampleLocator.ComputeSha256(fullPath));
        }
    }
}
