using TransFlux.Application.Contracts;
using TransFlux.Application.Services;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.Infrastructure;

/// <summary>Shared helpers for output reservation lock files and per-attempt work directories.</summary>
internal static class OutputReservation
{
    public const string LockSuffix = ".transflux-reserve";
    public const string WorkFolderName = OutputStagingLayout.WorkFolderName;

    public static string LockPath(string finalPath) => finalPath + LockSuffix;
}

/// <summary>Resolves output directories, allocates non-existing final names and reserves them,
/// and creates a same-volume per-attempt work directory.</summary>
public sealed class OutputPathService : IOutputPathService
{
    private readonly IFileSystemService _fs;

    /// <summary>Whether a directory is a per-attempt .transflux-work staging directory. Exposed
    /// so maintenance/cleanup code can validate before recursively deleting anything.</summary>
    public static bool IsWorkDirectory(string? directory) => OutputStagingLayout.IsWorkDirectory(directory);

    public OutputPathService(IFileSystemService fs, IClock clock)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        ArgumentNullException.ThrowIfNull(clock);
    }

    public Task<Result<ReservedOutput>> ReserveAsync(OutputReservationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? reservedFinalPath = null;
        try
        {
            var directory = ResolveDirectory(request);
            _fs.CreateDirectory(directory);

            // The reservation request has no expected output size. Zero (also returned when the
            // platform cannot determine free space) is the only universally insufficient value.
            var availableFreeSpace = _fs.GetAvailableFreeSpace(directory);
            if (availableFreeSpace <= 0)
            {
                return Task.FromResult(Result<ReservedOutput>.Operational(
                    FailureInfo.Of(FailureCode.DiskFull, "OUTPUT_DISK_FULL",
                        $"Output free space is unavailable or insufficient for '{directory}'.")));
            }

            var extension = request.Container.Extension();
            var stem = !string.IsNullOrWhiteSpace(request.RequestedFileName)
                ? _fs.GetFileNameWithoutExtension(request.RequestedFileName!)
                : _fs.GetFileNameWithoutExtension(request.SourcePath);
            if (string.IsNullOrWhiteSpace(stem)) stem = "output";

            reservedFinalPath = AllocateAndLock(directory, stem, extension);
            // Never allow the source itself (including hard links, 8.3 aliases and case variants)
            // to be selected as the destination.
            if (_fs.PathsReferToSameFile(request.SourcePath, reservedFinalPath))
            {
                CleanupFailedReservation(reservedFinalPath);
                return Task.FromResult(Result<ReservedOutput>.Operational(
                    FailureInfo.Of(FailureCode.OutputConflict, "OUTPUT_SAME_AS_SOURCE",
                        "The output path resolves to the source file; choose another name or folder.")));
            }
            var commitToken = Guid.NewGuid().ToString("N");
            var workDirectory = Path.Combine(directory, OutputReservation.WorkFolderName, commitToken);
            _fs.CreateDirectory(workDirectory);

            return Task.FromResult(Result<ReservedOutput>.Ok(new ReservedOutput
            {
                FinalPath = reservedFinalPath,
                WorkDirectory = workDirectory,
                CommitToken = commitToken,
                Extension = extension,
            }));
        }
        catch (UnauthorizedAccessException ex)
        {
            CleanupFailedReservation(reservedFinalPath);
            return Task.FromResult(Result<ReservedOutput>.Operational(
                FailureInfo.Of(FailureCode.AccessDenied, "OUTPUT_ACCESS_DENIED", ex.Message)));
        }
        catch (IOException ex)
        {
            CleanupFailedReservation(reservedFinalPath);
            return Task.FromResult(Result<ReservedOutput>.Operational(
                FailureInfo.Of(FailureCode.CommitFailed, "OUTPUT_RESERVE_IO", ex.Message)));
        }
    }

    /// <summary>Legacy combined cleanup kept for compatibility. Releases the naming reservation
    /// and deletes the attempt work directory. Unresolved commits must not call this.</summary>
    public void Release(ReservedOutput reserved)
    {
        ReleaseReservation(reserved);
        DeleteWorkDirectory(reserved);
    }

    public void ReleaseReservation(ReservedOutput reserved)
    {
        if (reserved is null) return;
        TryDeleteFile(OutputReservation.LockPath(reserved.FinalPath));
    }

    public void DeleteWorkDirectory(ReservedOutput reserved)
    {
        if (reserved is null) return;
        // Defense in depth: recursive deletion is only ever allowed for the documented staging
        // layout, never for a caller-constructed directory.
        if (!OutputStagingLayout.IsWorkDirectory(reserved.WorkDirectory)) return;
        try
        {
            if (Directory.Exists(reserved.WorkDirectory)) Directory.Delete(reserved.WorkDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; leftover work files are removed by a later sweep.
        }
    }

    private string ResolveDirectory(OutputReservationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.PreferredDirectory))
            return _fs.NormalizePath(request.PreferredDirectory!);
        return OutputNaming.DefaultOutputDirectory(request.SourcePath);
    }

    private string AllocateAndLock(string directory, string stem, string extension)
    {
        var clean = OutputNaming.SanitizeStem(stem);
        for (var i = 1; i < 10_000; i++)
        {
            var name = i == 1 ? clean + extension : $"{clean} ({i}){extension}";
            var candidate = Path.Combine(directory, name);
            if (_fs.FileExists(candidate)) continue;

            var lockPath = OutputReservation.LockPath(candidate);
            try
            {
                // CreateNew fails if the reservation lock already exists -> that name is taken.
                using (new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return candidate;
            }
            catch (IOException)
            {
                // Only an existing lock means the name is taken. Other I/O failures (for example
                // a full or unavailable volume) must be surfaced instead of scanning 10,000 names.
                if (_fs.FileExists(lockPath)) continue;
                throw;
            }
        }
        throw new IOException("Could not allocate a unique output name.");
    }

    private static void CleanupFailedReservation(string? finalPath)
    {
        if (!string.IsNullOrEmpty(finalPath)) TryDeleteFile(OutputReservation.LockPath(finalPath));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
