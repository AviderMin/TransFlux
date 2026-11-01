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
    private const int DefaultBatchSize = 64;
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "TransFlux Output",
        ".transflux-work",
    };
    private static readonly string[] DefaultMediaExtensions =
    {
        ".mp4", ".mkv", ".mov", ".m4v", ".avi", ".webm", ".ts", ".m2ts",
        ".m4a", ".flac", ".wav", ".mp3", ".aac", ".ogg", ".opus", ".oga", ".aiff", ".aif", ".wma", ".wmv", ".mka", ".mts", ".mpeg", ".mpg", ".vob", ".flv", ".3gp", ".mxf", ".ac3", ".weba",
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
        picker.FileTypeFilter.Add("*"); // Let ffprobe determine content for uncommon local media extensions.

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

    public static async Task<string?> PickSaveReportAsync(Microsoft.UI.Xaml.Window? window, string suggestedName)
    {
        if (window is null) return null;
        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("诊断报告", new List<string> { ".txt" });
        Initialize(window, picker);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    /// <summary>
    /// Enumerates media files without following reparse points, yielding bounded batches so
    /// the caller can import incrementally and cancel between batches.
    /// </summary>
    public static async Task<int> EnumerateMediaFilesAsync(
        string folder,
        bool recursive,
        Func<IReadOnlyList<string>, Task> onBatch,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludedDirectories = null,
        int batchSize = DefaultBatchSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(onBatch);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("文件夹不存在或当前不可访问。");

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludedDirectories is not null)
            foreach (var path in excludedDirectories)
                if (!string.IsNullOrWhiteSpace(path)) excluded.Add(NormalizeDirectory(path));

        var found = 0;
        var batch = new List<string>(Math.Min(batchSize, DefaultBatchSize));
        foreach (var path in EnumeratePaths(folder, recursive, excluded, cancellationToken))
        {
            batch.Add(path);
            found++;
            if (batch.Count < batchSize) continue;
            await onBatch(batch.ToArray()).ConfigureAwait(false);
            batch.Clear();
        }
        if (batch.Count > 0) await onBatch(batch.ToArray()).ConfigureAwait(false);
        return found;
    }

    /// <summary>Compatibility helper for bounded, non-UI callers; new UI imports use the batch API.</summary>
    public static IReadOnlyList<string> EnumerateMediaFiles(string folder, bool recursive)
    {
        if (!Directory.Exists(folder)) return Array.Empty<string>();
        try
        {
            return EnumeratePaths(folder, recursive, new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumeratePaths(
        string folder,
        bool recursive,
        HashSet<string> excluded,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(NormalizeDirectory(folder));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsMediaFile(path)) yield return path;
            }
            if (!recursive) continue;

            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fullPath;
                System.IO.FileAttributes attributes;
                try
                {
                    fullPath = NormalizeDirectory(child);
                    attributes = File.GetAttributes(fullPath);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
                const System.IO.FileAttributes skipped = System.IO.FileAttributes.ReparsePoint | System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System;
                if ((attributes & skipped) != 0 || IsExcludedDirectory(fullPath, excluded)) continue;
                pending.Push(fullPath);
            }
        }
    }

    private static bool IsExcludedDirectory(string path, HashSet<string> excluded)
        => excluded.Contains(path) || ExcludedDirectoryNames.Contains(Path.GetFileName(path));

    private static string NormalizeDirectory(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

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
