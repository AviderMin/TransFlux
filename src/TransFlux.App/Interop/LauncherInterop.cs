using System.Diagnostics;
using Windows.System;

namespace TransFlux.App.Interop;

/// <summary>Opens a verified output with the system default app, or reveals it in Explorer.</summary>
public static class LauncherInterop
{
    public static async Task OpenAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception)
        {
            // Ignore: the caller already surfaces the final path.
        }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            var args = System.IO.File.Exists(path) ? $"/select,\"{path}\"" : $"\"{System.IO.Path.GetDirectoryName(path) ?? path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    public static void CopyText(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception) { }
    }
}
