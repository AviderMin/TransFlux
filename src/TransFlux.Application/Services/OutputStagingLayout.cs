namespace TransFlux.Application.Services;

/// <summary>
/// Single source of truth for the per-attempt staging layout:
/// output directory / .transflux-work / commit token / attempt file.
/// Shared by the scheduler, the output path service and the startup orphan sweep so a path can
/// be validated as staging before anything is recursively deleted.
/// </summary>
public static class OutputStagingLayout
{
    public const string WorkFolderName = ".transflux-work";

    /// <summary>True only for a directory named like &lt;output&gt;/.transflux-work/&lt;token&gt;.</summary>
    public static bool IsWorkDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(directory);
            var parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(Path.GetFileName(parent), WorkFolderName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the validated .transflux-work directory containing the attempt temp file,
    /// or null when the path is missing or does not follow the staging layout.</summary>
    public static string? GetWorkDirectory(string? tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(tempPath));
            return IsWorkDirectory(directory) ? directory : null;
        }
        catch
        {
            return null;
        }
    }
}
