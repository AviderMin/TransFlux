using Microsoft.UI.Xaml;

namespace TransFlux.App.Theming;

/// <summary>Window chrome facts shared by the host and the root component.</summary>
public static class AppChrome
{
    /// <summary>Windows 11 ships the Mica backdrop; older systems fall back to an opaque root background.</summary>
    public static bool MicaEnabled { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
}

/// <summary>Maps the persisted theme preference to a WinUI <see cref="ElementTheme"/>.</summary>
public static class AppTheme
{
    public static ElementTheme Resolve(string? theme) => theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}
