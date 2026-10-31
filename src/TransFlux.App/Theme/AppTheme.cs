using Microsoft.UI.Xaml;

namespace TransFlux.App.Theming;

/// <summary>Layout constants (DIP) from docs/03 spacing scale (4/8/12/16/24/32).</summary>
public static class Space
{
    public const double Xs = 4;
    public const double Sm = 8;
    public const double Md = 12;
    public const double Lg = 16;
    public const double Xl = 24;
    public const double Xxl = 32;

    public const double PagePadding = 24;
    public const double CompactPadding = 16;
    public const double SettingsPanelMin = 340;
    public const double SettingsPanelMax = 380;
    public const double WindowMinWidth = 920;
    public const double WindowMinHeight = 640;
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
