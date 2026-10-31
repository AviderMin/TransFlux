using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace TransFlux.Engine.FFmpeg.Core;

/// <summary>
/// Resolves the trusted ffmpeg.exe / ffprobe.exe pair. Search order:
/// 1) explicitDirectory; 2) TRANSFLUX_FFMPEG_DIR; 3) walk up from AppContext.BaseDirectory
/// and Environment.CurrentDirectory for tools/ffmpeg/core-manifest.json or a folder that
/// contains ffmpeg.exe + ffprobe.exe; 4) a "ffmpeg" folder beside the executable.
/// When core-manifest.json is present its SHA-256 values are verified; a mismatch throws
/// <see cref="CoreIntegrityException"/>. PATH is never consulted.
/// </summary>
public static class FfmpegCoreLocator
{
    private static readonly ConcurrentDictionary<string, FfmpegCorePaths> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static FfmpegCorePaths Locate(string? explicitDirectory = null)
    {
        var searched = new List<string>();
        foreach (var dir in EnumerateSearchRoots(explicitDirectory))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (searched.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;
            searched.Add(dir);
            var found = TryLocateIn(dir);
            if (found is not null) return found;
        }

        throw new CoreNotFoundException(
            "Could not locate the pinned ffmpeg/ffprobe core. Searched: " +
            string.Join("; ", searched) +
            ". Set TRANSFLUX_FFMPEG_DIR or pass an explicit directory.");
    }

    /// <summary>Full-resolution SHA-256 (uppercase hex) used for integrity checks and hashing.</summary>
    public static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private static IEnumerable<string> EnumerateSearchRoots(string? explicitDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            yield return explicitDirectory!;

        var env = Environment.GetEnvironmentVariable("TRANSFLUX_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            yield return env!;

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            string full;
            try { full = Path.GetFullPath(start); }
            catch { continue; }

            var dir = new DirectoryInfo(full);
            while (dir is not null)
            {
                // tools/ffmpeg holds core-manifest.json in the source tree / package layout.
                yield return Path.Combine(dir.FullName, "tools", "ffmpeg");
                // A folder named "ffmpeg" beside the executable.
                yield return Path.Combine(dir.FullName, "ffmpeg");
                // The directory itself (a bin folder beside the exes, or a manifest folder).
                yield return dir.FullName;
                dir = dir.Parent;
            }
        }
    }

    private static FfmpegCorePaths? TryLocateIn(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;

            var manifest = Path.Combine(directory, "core-manifest.json");
            if (File.Exists(manifest)) return LoadFromManifest(manifest);

            if (TryExes(directory, out var ff, out var fp, out var coreHash, out var version))
                return Cache.GetOrAdd(KeyFor(ff, fp), _ => new FfmpegCorePaths(ff, fp, coreHash, version));

            var bin = Path.Combine(directory, "bin");
            if (Directory.Exists(bin) && TryExes(bin, out ff, out fp, out coreHash, out version))
                return Cache.GetOrAdd(KeyFor(ff, fp), _ => new FfmpegCorePaths(ff, fp, coreHash, version));

            return null;
        }
        catch (CoreIntegrityException)
        {
            throw; // Integrity failures must not be swallowed by search.
        }
        catch
        {
            return null;
        }
    }

    private static bool TryExes(string directory, out string ffmpeg, out string ffprobe, out string coreHash, out string version)
    {
        ffmpeg = Path.Combine(directory, "ffmpeg.exe");
        ffprobe = Path.Combine(directory, "ffprobe.exe");
        coreHash = string.Empty;
        version = string.Empty;
        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe)) return false;
        coreHash = ComputeSha256Hex(ffmpeg);
        return true;
    }

    private static string KeyFor(string ffmpeg, string ffprobe)
    {
        var fi = new FileInfo(ffmpeg);
        return $"{ffmpeg}|{ffprobe}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
    }

    private static FfmpegCorePaths LoadFromManifest(string manifestPath)
    {
        var manifestDir = Path.GetDirectoryName(manifestPath)!;
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;

        if (!root.TryGetProperty("ffmpeg", out var ff) || !root.TryGetProperty("ffprobe", out var fp))
            throw new CoreIntegrityException(manifestPath, "<missing ffmpeg/ffprobe entries>", "<none>");

        var ffRel = ff.GetProperty("relativePath").GetString()
            ?? throw new CoreIntegrityException(manifestPath, "<missing ffmpeg relativePath>", "<none>");
        var fpRel = fp.GetProperty("relativePath").GetString()
            ?? throw new CoreIntegrityException(manifestPath, "<missing ffprobe relativePath>", "<none>");
        var ffSha = ff.GetProperty("sha256").GetString() ?? string.Empty;
        var fpSha = fp.GetProperty("sha256").GetString() ?? string.Empty;
        var version = ff.TryGetProperty("version", out var v) ? (v.GetString() ?? string.Empty) : string.Empty;

        var ffPath = ResolveRelative(manifestDir, ffRel)
            ?? throw new CoreNotFoundException($"Manifest '{manifestPath}' points at missing ffmpeg '{ffRel}'.");
        var fpPath = ResolveRelative(manifestDir, fpRel)
            ?? throw new CoreNotFoundException($"Manifest '{manifestPath}' points at missing ffprobe '{fpRel}'.");

        var cacheKey = $"{manifestPath}|{ffSha}|{fpSha}";
        return Cache.GetOrAdd(cacheKey, _ =>
        {
            VerifySha(ffPath, ffSha);
            VerifySha(fpPath, fpSha);
            return new FfmpegCorePaths(ffPath, fpPath, ffSha.ToUpperInvariant(), version);
        });
    }

    private static string? ResolveRelative(string manifestDir, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var dir = new DirectoryInfo(manifestDir);
        while (dir is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir.FullName, normalized));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static void VerifySha(string path, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return; // manifests without a hash still locate+hash.
        var actual = ComputeSha256Hex(path);
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new CoreIntegrityException(path, expectedHex.ToUpperInvariant(), actual);
    }
}
