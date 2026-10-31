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

    /// <summary>Opens a folder path in Explorer; falls back to the default file handler.</summary>
    public static async Task OpenFolderAsync(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
                await Launcher.LaunchFolderAsync(folder);
                return;
            }
            await OpenAsync(path);
        }
        catch (Exception)
        {
            // Ignore: the caller already surfaces the path in the UI.
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
