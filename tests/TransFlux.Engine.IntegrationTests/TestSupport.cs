using System.Security.Cryptography;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Engine.FFmpeg.Core;
using TransFlux.Engine.FFmpeg.Processes;

namespace TransFlux.Engine.IntegrationTests;

internal static class TestSupport
{
    public static FfmpegCorePaths Core { get; } = FfmpegCoreLocator.Locate();

    public static string TempRoot { get; } = CreateTempRoot();

    private static string CreateTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-engine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string PathFor(string name)
    {
        var path = Path.Combine(TempRoot, name);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return path;
    }

    public static string NewWorkDir(string prefix)
    {
        var dir = Path.Combine(TempRoot, prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task<string> GenerateVideoAsync(
        string fileName = "sample.mp4", int seconds = 2, int width = 320, int height = 240, bool withAudio = true)
    {
        var outPath = PathFor(fileName);
        if (File.Exists(outPath)) return outPath;

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc=duration={seconds}:size={width}x{height}:rate=30",
        };
        if (withAudio)
            args.AddRange(new[] { "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}:sample_rate=48000" });
        args.AddRange(new[] { "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "ultrafast" });
        if (withAudio)
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-shortest" });
        args.Add(outPath);

        var result = await ProcessCapture.RunAsync(
            Core.FfmpegPath, args, TimeSpan.FromSeconds(120), 1 << 20, 1 << 20, CancellationToken.None);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Sample generation failed ({result.ExitCode}): {result.Stderr}");
        return outPath;
    }

    public static async Task<string> GenerateRotatedVideoAsync(string basePath, string fileName, int displayRotation)
    {
        var outPath = PathFor(fileName);
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-display_rotation", displayRotation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", basePath, "-c", "copy", outPath,
        };
        var result = await ProcessCapture.RunAsync(
            Core.FfmpegPath, args, TimeSpan.FromSeconds(60), 1 << 20, 1 << 20, CancellationToken.None);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Rotated sample generation failed ({result.ExitCode}): {result.Stderr}");
        return outPath;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static CapabilitySnapshot EmptyCapabilities() => new() { CapabilityHash = "test-capability-hash" };

    public static ConversionRequest TranscodeRequest(SourceSnapshot source, ContainerFormat container = ContainerFormat.Mp4)
        => new()
        {
            SourceSnapshotId = source.MediaId,
            Mode = ConversionMode.VideoTranscode,
            Container = container,
            Quality = QualityTier.Balanced,
            Speed = SpeedTier.Balanced,
        };

    public static AttemptContext BuildAttempt(ValidatedPlan plan, SourceSnapshot source, ConversionRequest request, string? tempPath = null)
    {
        var candidate = plan.CandidatePaths[0];
        var path = tempPath ?? Path.Combine(NewWorkDir("work"), "out" + plan.ExpectedOutput.Container.Extension());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new AttemptContext
        {
            JobId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            Candidate = candidate,
            Source = source,
            Request = request,
            TempPath = path,
            FfmpegPath = Core.FfmpegPath,
            FfprobePath = Core.FfprobePath,
            EffectiveDurationUs = plan.ExpectedOutput.DurationUs ?? source.DurationUs,
        };
    }
}

internal sealed class CollectingProgress : IProgress<TransFlux.Application.Contracts.EngineProgress>
{
    private readonly List<TransFlux.Application.Contracts.EngineProgress> _items = new();
    private readonly object _gate = new();

    public void Report(TransFlux.Application.Contracts.EngineProgress value)
    {
        lock (_gate) _items.Add(value);
    }

    public IReadOnlyList<TransFlux.Application.Contracts.EngineProgress> Items
    {
        get { lock (_gate) return _items.ToArray(); }
    }
}
