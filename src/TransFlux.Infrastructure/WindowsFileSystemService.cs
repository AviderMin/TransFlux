using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TransFlux.Application.Contracts;

namespace TransFlux.Infrastructure;

/// <summary>Windows implementation of <see cref="IFileSystemService"/>. Uses Win32 handles for
/// file identity and an atomic no-replace move (MoveFileEx without REPLACE_EXISTING).</summary>
public sealed class WindowsFileSystemService : IFileSystemService
{
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public long GetAvailableFreeSpace(string path)
    {
        var full = Path.GetFullPath(path);
        string target = Directory.Exists(full) ? full : (Path.GetDirectoryName(full) ?? ".");
        if (!string.IsNullOrEmpty(target) && GetDiskFreeSpaceEx(target, out var freeToCaller, out _, out _))
            return freeToCaller > long.MaxValue ? long.MaxValue : (long)freeToCaller;

        try
        {
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root))
            {
                var di = new DriveInfo(root);
                if (di.IsReady) return di.AvailableFreeSpace;
            }
        }
        catch
        {
            // Unknown / UNC unavailable.
        }
        return 0;
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public string NormalizePath(string path) => NormalizeCore(path);

    private static string NormalizeCore(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length > 3 && (full[^1] == Path.DirectorySeparatorChar || full[^1] == Path.AltDirectorySeparatorChar))
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full;
    }

    public bool PathsReferToSameFile(string a, string b)
    {
        var pa = TryGetFinalPath(a);
        var pb = TryGetFinalPath(b);
        if (pa is not null && pb is not null)
            return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
        return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetFinalPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return null;
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
            var sb = new StringBuilder(1024);
            var len = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
            if (len == 0) return null;
            if (len >= sb.Capacity)
            {
                sb = new StringBuilder((int)len + 1);
                len = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
                if (len == 0) return null;
            }
            var result = sb.ToString();
            const string longPrefix = @"\\?\";
            if (result.StartsWith(longPrefix, StringComparison.Ordinal)) result = result[longPrefix.Length..];
            return NormalizeCore(result);
        }
        catch
        {
            return null;
        }
    }

    public Task<FileIdentity?> GetIdentityAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult<FileIdentity?>(GetIdentity(path));
        }
        catch
        {
            return Task.FromResult<FileIdentity?>(null);
        }
    }

    public FileIdentity GetIdentity(string path)
    {
        var full = NormalizePath(path);
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(fs.SafeFileHandle, out var info))
            throw new IOException($"GetFileInformationByHandle failed for '{full}': {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var fileId = ((long)info.FileIndexHigh << 32) | info.FileIndexLow;
        var ft = ((long)info.LastWriteTime.HighDateTime << 32) | info.LastWriteTime.LowDateTime;
        var modified = new DateTimeOffset(DateTime.FromFileTimeUtc(ft), TimeSpan.Zero);
        return new FileIdentity(full, size, modified, info.VolumeSerialNumber.ToString("X8"), fileId.ToString("X16"));
    }

    public IReadOnlyList<string> EnumerateFiles(string directory, bool recursive)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directory, "*", option).ToList();
    }

    public string GetFileNameWithoutExtension(string path) => Path.GetFileNameWithoutExtension(path);

    public string GetExtension(string path) => Path.GetExtension(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public DateTimeOffset GetLastWriteTimeUtc(string path)
        => new(File.GetLastWriteTimeUtc(path));

    public void MoveNoReplace(string source, string destination)
    {
        if (File.Exists(destination))
            throw new IOException($"Destination already exists: '{destination}'.");

        if (!MoveFileEx(source, destination, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err is ErrorFileExists or ErrorAlreadyExists)
                throw new IOException($"Destination already exists: '{destination}'.");
            throw new IOException($"Move failed '{source}' -> '{destination}': {new Win32Exception(err).Message} (Win32 {err}).");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder lpszFilePath,
        uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
