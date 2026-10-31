using Windows.Storage;
using Windows.Storage.Pickers;

namespace TransFlux.App.Interop;

/// <summary>
/// Native file/folder picker interop. WinUI 3 pickers must be initialized with
/// the owning window's HWND, so these run on the UI thread from event handlers
/// (never from render). See docs/03 EmptyImport and docs/04 Interop boundary.
/// </summary>
public static class FilePickerInterop
{
    private static readonly string[] DefaultMediaExtensions =
    {
        ".mp4", ".mkv", ".mov", ".m4v", ".avi", ".webm", ".ts", ".m2ts",
        ".m4a", ".flac", ".wav", ".mp3", ".aac", ".ogg", ".opus",
    };

    public static async Task<IReadOnlyList<string>> PickFilesAsync(Microsoft.UI.Xaml.Window? window)
    {
        if (window is null) return Array.Empty<string>();

        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
        };
        foreach (var ext in DefaultMediaExtensions)
            picker.FileTypeFilter.Add(ext);

        Initialize(window, picker);

        var files = await picker.PickMultipleFilesAsync();
        return files is null
            ? Array.Empty<string>()
            : files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    public static async Task<string?> PickFolderAsync(Microsoft.UI.Xaml.Window? window)
    {
        if (window is null) return null;

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
        };
        picker.FileTypeFilter.Add("*");

        Initialize(window, picker);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>Enumerates media files in a folder (recursively for "add folder").</summary>
    public static IReadOnlyList<string> EnumerateMediaFiles(string folder, bool recursive)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = System.IO.FileAttributes.System | System.IO.FileAttributes.Hidden,
        };
        try
        {
            return Directory.EnumerateFiles(folder, "*", options)
                .Where(IsMediaFile)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    public static bool IsMediaFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return DefaultMediaExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static void Initialize(Microsoft.UI.Xaml.Window window, object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
