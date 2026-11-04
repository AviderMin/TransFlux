using System.Text.Json;
using TransFlux.Engine.FFmpeg.Core;

namespace TransFlux.Engine.IntegrationTests;

public class CoreLocatorTests
{
    [Fact]
    public void RuntimeDllTampering_InvalidatesPriorSuccessfulVerification()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-core-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe", "avcodec.dll" }) File.WriteAllText(Path.Combine(dir, name), name);
        object Entry(string name) => new { relativePath = name, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, name)))) };
        File.WriteAllText(Path.Combine(dir, "core-manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 2, ffmpeg = Entry("ffmpeg.exe"), ffprobe = Entry("ffprobe.exe"), runtimeFiles = new[] { Entry("avcodec.dll") },
        }));
        var valid = FfmpegCoreLocator.Locate(dir);
        Assert.Equal(64, valid.CoreHash.Length);
        File.AppendAllText(Path.Combine(dir, "avcodec.dll"), "tampered");
        var failure = Assert.Throws<CoreIntegrityException>(() => FfmpegCoreLocator.Locate(dir));
        Assert.EndsWith("avcodec.dll", failure.FilePath);
    }

    [Fact]
    public void Locate_FindsPinnedCoreWithHashAndVersion()
    {
        var core = FfmpegCoreLocator.Locate();
        Assert.True(File.Exists(core.FfmpegPath), core.FfmpegPath);
        Assert.True(File.Exists(core.FfprobePath), core.FfprobePath);
        Assert.False(string.IsNullOrWhiteSpace(core.CoreHash));
        Assert.Equal(64, core.CoreHash.Length);
        Assert.Equal("8.0.1-full_build-www.gyan.dev", core.Version);
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

    [Fact]
    public void Locate_WithoutManifest_IsRejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-core-nomanifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ffmpeg.exe"), "fake");
            File.WriteAllText(Path.Combine(dir, "ffprobe.exe"), "fake");
            Assert.Throws<CoreNotFoundException>(() => FfmpegCoreLocator.Locate(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Locate_WithMissingManifestHash_IsRejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-core-nohash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ffmpeg.exe"), "fake-ffmpeg");
            File.WriteAllText(Path.Combine(dir, "ffprobe.exe"), "fake-ffprobe");
            var manifest = new
            {
                schemaVersion = 2,
                ffmpeg = new { relativePath = "ffmpeg.exe", sha256 = "" },
                ffprobe = new { relativePath = "ffprobe.exe", sha256 = "" },
            };
            File.WriteAllText(Path.Combine(dir, "core-manifest.json"), JsonSerializer.Serialize(manifest));
            var ex = Assert.Throws<CoreIntegrityException>(() => FfmpegCoreLocator.Locate(dir));
            Assert.Contains("missing sha256", ex.ExpectedHash);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Locate_WithMalformedManifest_IsRejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-core-badjson-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ffmpeg.exe"), "fake");
            File.WriteAllText(Path.Combine(dir, "ffprobe.exe"), "fake");
            File.WriteAllText(Path.Combine(dir, "core-manifest.json"), "{ not json");
            var ex = Assert.Throws<CoreIntegrityException>(() => FfmpegCoreLocator.Locate(dir));
            Assert.Contains("malformed", ex.ExpectedHash);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Locate_AllowUnverifiedEscapeHatch_AcceptsRawExes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-core-dev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ffmpeg.exe"), "dev-ffmpeg");
            File.WriteAllText(Path.Combine(dir, "ffprobe.exe"), "dev-ffprobe");
            var core = FfmpegCoreLocator.Locate(dir, allowUnverified: true);
            Assert.Equal(64, core.CoreHash.Length);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
