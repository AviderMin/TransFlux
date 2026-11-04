using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace TransFlux.Engine.FFmpeg.Core;

/// <summary>
/// Resolves the trusted ffmpeg.exe / ffprobe.exe pair. Search order:
/// 1) explicitDirectory (authoritative when supplied); 2) TRANSFLUX_FFMPEG_DIR (authoritative
/// when set); 3) walk up from AppContext.BaseDirectory for tools/ffmpeg/core-manifest.json or a
/// "ffmpeg" folder beside the executable. PATH and Environment.CurrentDirectory are never consulted.
/// A core is accepted only when core-manifest.json is present and every hash in it matches;
/// missing, malformed or hash-less manifests fail closed with <see cref="CoreIntegrityException"/>.
/// The explicit <paramref name="allowUnverified"/> switch exists only for isolated development
/// tooling; production callers must never set it.
/// </summary>
public static class FfmpegCoreLocator
{
    private static readonly ConcurrentDictionary<string, FfmpegCorePaths> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static FfmpegCorePaths Locate(string? explicitDirectory = null, bool allowUnverified = false)
    {
        var searched = new List<string>();
        foreach (var dir in EnumerateSearchRoots(explicitDirectory))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (searched.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;
            searched.Add(dir);
            var found = TryLocateIn(dir, allowUnverified);
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
        // An explicit directory or environment override is authoritative: never silently fall
        // back to a different core when the caller asked for a specific one.
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            yield return explicitDirectory!;
            yield break;
        }

        var env = Environment.GetEnvironmentVariable("TRANSFLUX_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env!;
            yield break;
        }

        foreach (var start in new[] { AppContext.BaseDirectory })
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

    private static FfmpegCorePaths? TryLocateIn(string directory, bool allowUnverified)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;

            var manifest = Path.Combine(directory, "core-manifest.json");
            if (File.Exists(manifest)) return LoadFromManifest(manifest);

            // Fail closed: a directory without a pinned manifest is not a trusted core.
            // The only exception is the explicit, isolated development escape hatch.
            if (!allowUnverified) return null;

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
        catch (CoreNotFoundException) { throw; }
        catch (Exception ex)
        {
            // A manifest that exists but cannot be parsed is an integrity failure, not a
            // reason to quietly search for a different (possibly malicious) core elsewhere.
            var manifest = Path.Combine(directory, "core-manifest.json");
            if (File.Exists(manifest))
                throw new CoreIntegrityException(manifest, "<malformed core-manifest.json>", ex.GetType().Name + ": " + ex.Message);
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

        var runtime = new List<(string Path, string Hash)>();
        if (root.TryGetProperty("runtimeFiles", out var files))
            foreach (var file in files.EnumerateArray())
            {
                var relative = file.GetProperty("relativePath").GetString()!;
                var path = Path.GetFullPath(Path.Combine(manifestDir, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(Path.GetFullPath(manifestDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(path)) throw new CoreNotFoundException($"Missing or invalid media runtime: {relative}");
                runtime.Add((path, file.GetProperty("sha256").GetString()!));
            }
        var bundleHash = runtime.Count == 0 ? ffSha.ToUpperInvariant() : Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("|", new[] { ffSha, fpSha }.Concat(runtime.Select(r => r.Hash))))));
        var stamps = new[] { ffPath, fpPath }.Concat(runtime.Select(r => r.Path)).Select(p =>
            { var info = new FileInfo(p); return $"{p}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"; });
        var cacheKey = $"{manifestPath}|{bundleHash}|{string.Join('|', stamps)}";
        return Cache.GetOrAdd(cacheKey, _ =>
        {
            VerifySha(ffPath, ffSha);
            VerifySha(fpPath, fpSha);
            foreach (var item in runtime) VerifySha(item.Path, item.Hash);
            return new FfmpegCorePaths(ffPath, fpPath, bundleHash, version);
        });
    }

    /// <summary>
    /// Resolves a manifest entry strictly inside the manifest directory subtree (same
    /// containment rule as runtimeFiles) so a tampered manifest cannot point at binaries
    /// outside the shipped core folder.
    /// </summary>
    private static string? ResolveRelative(string manifestDir, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(manifestDir);
        var candidate = Path.GetFullPath(Path.Combine(root, normalized));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(candidate) ? candidate : null;
    }

    private static void VerifySha(string path, string expectedHex)
    {
        var actual = ComputeSha256Hex(path);
        if (string.IsNullOrWhiteSpace(expectedHex))
            throw new CoreIntegrityException(path, "<missing sha256 in core-manifest.json>", actual);
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new CoreIntegrityException(path, expectedHex.ToUpperInvariant(), actual);
    }
}
