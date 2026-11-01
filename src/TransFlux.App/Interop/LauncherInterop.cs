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
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法打开输出文件。请确认文件仍存在，并检查默认应用关联。", ex);
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
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法打开输出文件夹。请确认该位置仍然存在。", ex);
        }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            var args = System.IO.File.Exists(path) ? $"/select,\"{path}\"" : $"\"{System.IO.Path.GetDirectoryName(path) ?? path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法在资源管理器中显示输出。请确认该位置仍然存在。", ex);
        }
    }

    public static void CopyText(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法复制输出路径。", ex);
        }
    }
}
