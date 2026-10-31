using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.Infrastructure;

/// <summary>Shared helpers for output reservation lock files and per-attempt work directories.</summary>
internal static class OutputReservation
{
    public const string LockSuffix = ".transflux-reserve";
    public const string WorkFolderName = ".transflux-work";

    public static string LockPath(string finalPath) => finalPath + LockSuffix;
}

/// <summary>Resolves output directories, allocates non-existing final names and reserves them,
/// and creates a same-volume per-attempt work directory.</summary>
public sealed class OutputPathService : IOutputPathService
{
    private readonly IFileSystemService _fs;

    public OutputPathService(IFileSystemService fs, IClock clock)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        ArgumentNullException.ThrowIfNull(clock);
    }

    public Task<Result<ReservedOutput>> ReserveAsync(OutputReservationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var directory = ResolveDirectory(request);
            _fs.CreateDirectory(directory);

            // Preflight: report free space; a genuinely full volume also fails lock creation below.
            _ = _fs.GetAvailableFreeSpace(directory);

            var extension = request.Container.Extension();
            var stem = !string.IsNullOrWhiteSpace(request.RequestedFileName)
                ? _fs.GetFileNameWithoutExtension(request.RequestedFileName!)
                : _fs.GetFileNameWithoutExtension(request.SourcePath);
            if (string.IsNullOrWhiteSpace(stem)) stem = "output";

            var finalPath = AllocateAndLock(directory, stem, extension);
            var commitToken = Guid.NewGuid().ToString("N");
            var workDirectory = Path.Combine(directory, OutputReservation.WorkFolderName, commitToken);
            _fs.CreateDirectory(workDirectory);

            return Task.FromResult(Result<ReservedOutput>.Ok(new ReservedOutput
            {
                FinalPath = finalPath,
                WorkDirectory = workDirectory,
                CommitToken = commitToken,
                Extension = extension,
            }));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(Result<ReservedOutput>.Operational(
                FailureInfo.Of(FailureCode.AccessDenied, "OUTPUT_ACCESS_DENIED", ex.Message)));
        }
        catch (IOException ex)
        {
            return Task.FromResult(Result<ReservedOutput>.Operational(
                FailureInfo.Of(FailureCode.CommitFailed, "OUTPUT_RESERVE_IO", ex.Message)));
        }
    }

    public void Release(ReservedOutput reserved)
    {
        if (reserved is null) return;
        TryDeleteFile(OutputReservation.LockPath(reserved.FinalPath));
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
                // Name already reserved; try the next increment.
            }
        }
        throw new IOException("Could not allocate a unique output name.");
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
