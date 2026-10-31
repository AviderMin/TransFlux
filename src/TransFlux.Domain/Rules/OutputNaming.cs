using System.Text;

namespace TransFlux.Domain.Rules;

public static class OutputNaming
{
    public const string DefaultOutputFolderName = "TransFlux Output";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9",
    };

    private static readonly char[] InvalidChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>Sanitizes a file name stem (no extension): strips illegal characters,
    /// trailing dots/spaces, Windows reserved device names and clamps length.</summary>
    public static string SanitizeStem(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch < 32 || Array.IndexOf(InvalidChars, ch) >= 0) sb.Append('_');
            else sb.Append(ch);
        }
        var result = sb.ToString().TrimEnd(' ', '.');
        if (result.Length == 0) result = "output";
        if (ReservedNames.Contains(result)) result = "_" + result;
        const int max = 180;
        if (result.Length > max) result = result[..max];
        return result;
    }

    /// <summary>Allocates a non-existing candidate path using incremental numbering.
    /// Does not itself reserve the name.</summary>
    public static string AllocateUniqueStem(string directory, string stem, string extension, Func<string, bool> exists)
    {
        var cleanStem = SanitizeStem(stem);
        var candidate = Path.Combine(directory, cleanStem + extension);
        if (!exists(candidate)) return candidate;
        for (var i = 2; i < 10_000; i++)
        {
            candidate = Path.Combine(directory, string.Format("{0} ({1}){2}", cleanStem, i, extension));
            if (!exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("Could not allocate a unique output name.");
    }

    public static string DefaultOutputDirectory(string sourcePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
        return Path.Combine(dir, DefaultOutputFolderName);
    }
}
