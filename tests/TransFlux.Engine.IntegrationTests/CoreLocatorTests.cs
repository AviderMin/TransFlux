using System.Text.Json;
using TransFlux.Engine.FFmpeg.Core;

namespace TransFlux.Engine.IntegrationTests;

public class CoreLocatorTests
{
    [Fact]
    public void Locate_FindsPinnedCoreWithHashAndVersion()
    {
        var core = FfmpegCoreLocator.Locate();
        Assert.True(File.Exists(core.FfmpegPath), core.FfmpegPath);
        Assert.True(File.Exists(core.FfprobePath), core.FfprobePath);
        Assert.False(string.IsNullOrWhiteSpace(core.CoreHash));
        Assert.Equal(64, core.CoreHash.Length);
        Assert.Matches(@"^N-\d+-g[0-9a-f]+-\d{8}$", core.Version);
    }

    [Fact]
    public void Locate_IsDeterministic()
    {
        var a = FfmpegCoreLocator.Locate();
        var b = FfmpegCoreLocator.Locate();
        Assert.Equal(a.FfmpegPath, b.FfmpegPath);
        Assert.Equal(a.CoreHash, b.CoreHash);
    }

    [Fact]
    public void Locate_WithWrongManifestHash_ThrowsCoreIntegrityExceptionCarryingBothHashes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-core-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ff = Path.Combine(dir, "ffmpeg.exe");
            var fp = Path.Combine(dir, "ffprobe.exe");
            File.WriteAllText(ff, "not a real ffmpeg");
            File.WriteAllText(fp, "not a real ffprobe");

            var manifest = new
            {
                schemaVersion = 1,
                ffmpeg = new { version = "fake", relativePath = "ffmpeg.exe", sha256 = new string('A', 64) },
                ffprobe = new { version = "fake", relativePath = "ffprobe.exe", sha256 = new string('B', 64) },
            };
            File.WriteAllText(Path.Combine(dir, "core-manifest.json"), JsonSerializer.Serialize(manifest));

            var ex = Assert.Throws<CoreIntegrityException>(() => FfmpegCoreLocator.Locate(dir));
            Assert.Equal(new string('A', 64), ex.ExpectedHash);
            Assert.Equal(64, ex.ActualHash.Length);
            Assert.NotEqual(ex.ExpectedHash, ex.ActualHash);
            Assert.Contains("ffmpeg.exe", ex.FilePath);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}