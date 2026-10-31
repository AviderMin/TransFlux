using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace TransFlux.App.Interop;

/// <summary>Window sizing helpers. Best-effort; the host may also size the window.</summary>
public static class WindowInterop
{
    public const int DefaultWidthDip = 1280;
    public const int DefaultHeightDip = 820;

    public static void ApplyInitialSize(Window? window, int widthDip = DefaultWidthDip, int heightDip = DefaultHeightDip)
    {
        if (window?.AppWindow is null) return;
        try
        {
            var scale = (window.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
            var w = (int)Math.Round(widthDip * scale);
            var h = (int)Math.Round(heightDip * scale);
            window.AppWindow.ResizeClient(new SizeInt32(w, h));
        }
        catch (Exception)
        {
            // Sizing is cosmetic; never fail startup over it.
        }
    }

    public static void SetTitle(Window? window, string title)
    {
        if (window is null) return;
        try { window.Title = title; } catch (Exception) { }
    }
}
