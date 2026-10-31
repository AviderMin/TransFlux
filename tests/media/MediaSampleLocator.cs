using System.Security.Cryptography;
using System.Text.Json;

namespace TransFlux.Tests.Media;

/// <summary>One entry in tests/media/manifest.json.</summary>
public sealed record MediaSampleEntry
{
    public required string SampleId { get; init; }
    public required string Path { get; init; }
    public required string Purpose { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string? Streams { get; init; }
    public string? ProbeSummary { get; init; }
    public string? Notes { get; init; }
}

public sealed record MediaManifest
{
    public int SchemaVersion { get; init; }
    public string GeneratedUtc { get; init; } = string.Empty;
    public string Generator { get; init; } = string.Empty;
    public string FfmpegVersion { get; init; } = string.Empty;
    public IReadOnlyList<MediaSampleEntry> Samples { get; init; } = Array.Empty<MediaSampleEntry>();
}

/// <summary>
/// Shared QA helper that resolves the repository root and the generated sample media
/// described by tests/media/manifest.json. Used by tests that need real media files.
/// </summary>
public static class MediaSampleLocator
{
    public const string RepoRootEnvironmentVariable = "TRANSFLUX_REPO_ROOT";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<string> Root = new(FindRepoRoot);

    public static string RepoRoot => Root.Value;
    public static string MediaDirectory => Path.Combine(RepoRoot, "tests", "media");
    public static string SamplesDirectory => Path.Combine(MediaDirectory, "samples");
    public static string ManifestPath => Path.Combine(MediaDirectory, "manifest.json");

    public static MediaManifest LoadManifest()
    {
        if (!File.Exists(ManifestPath))
            throw new FileNotFoundException(
                $"Media manifest not found at {ManifestPath}. Run tests/media/generate-samples.ps1 first.");
        var json = File.ReadAllText(ManifestPath);
        return JsonSerializer.Deserialize<MediaManifest>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Could not parse media manifest at {ManifestPath}.");
    }

    public static string ResolveSamplePath(MediaSampleEntry entry)
        => Path.GetFullPath(Path.Combine(RepoRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)));

    public static bool TryGetSample(string sampleId, out MediaSampleEntry? entry, out string fullPath)
    {
        entry = LoadManifest().Samples.FirstOrDefault(
            s => string.Equals(s.SampleId, sampleId, StringComparison.OrdinalIgnoreCase));
        fullPath = entry is null ? string.Empty : ResolveSamplePath(entry);
        return entry is not null;
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string FindRepoRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(RepoRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
            return Path.GetFullPath(fromEnvironment);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) ||
                File.Exists(Path.Combine(directory.FullName, "TransFlux.slnx")) ||
                (Directory.Exists(Path.Combine(directory.FullName, "docs")) &&
                 Directory.Exists(Path.Combine(directory.FullName, "tests"))))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the TransFlux repository root. Set TRANSFLUX_REPO_ROOT.");
    }
}
