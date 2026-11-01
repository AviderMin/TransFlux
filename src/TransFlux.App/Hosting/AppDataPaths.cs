namespace TransFlux.App.Hosting;

/// <summary>Resolves the per-user application data directory. Packaged builds use the
/// package LocalState; unpackaged builds fall back to %LOCALAPPDATA%\TransFlux.</summary>
public static class AppDataPaths
{
    public static string GetRoot()
    {
#if DEBUG
        // Isolated manual smoke tests must not touch the installed user's workspace.
        var testRoot = Environment.GetEnvironmentVariable("TRANSFLUX_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(testRoot))
        {
            var full = Path.GetFullPath(testRoot);
            Directory.CreateDirectory(full);
            return full;
        }
#endif
        try
        {
            var local = global::Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            if (!string.IsNullOrEmpty(local))
            {
                Directory.CreateDirectory(local);
                return local;
            }
        }
        catch
        {
            // Unpackaged / no package identity.
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TransFlux");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetDatabasePath() => Path.Combine(GetRoot(), "state.db");
}
