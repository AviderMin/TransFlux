namespace TransFlux.Infrastructure.Tests;

public sealed class WindowsFileSystemServiceTests
{
    [Fact]
    public void MoveNoReplace_Throws_WhenDestinationExists_And_MovesOtherwise()
    {
        using var dir = new TempDir();
        var fs = new WindowsFileSystemService();

        var src = dir.NewFile("a.txt", "a");
        var dst = dir.NewFile("b.txt", "b");
        Assert.Throws<IOException>(() => fs.MoveNoReplace(src, dst));
        Assert.True(File.Exists(src));
        Assert.Equal("b", File.ReadAllText(dst));

        var src2 = dir.NewFile("c.txt", "c");
        var dst2 = Path.Combine(dir.Path, "d.txt");
        fs.MoveNoReplace(src2, dst2);
        Assert.True(File.Exists(dst2));
        Assert.False(File.Exists(src2));
        Assert.Equal("c", File.ReadAllText(dst2));
    }

    [Fact]
    public void PathsReferToSameFile_And_Identity_Work()
    {
        using var dir = new TempDir();
        var fs = new WindowsFileSystemService();

        var p = dir.NewFile("e.txt", "12345");
        var other = dir.NewFile("f.txt", "zzz");

        Assert.True(fs.PathsReferToSameFile(p, p));
        Assert.False(fs.PathsReferToSameFile(p, other));

        // Same file via a path containing "..".
        var alias = Path.Combine(dir.Path, "sub", "..", "e.txt");
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        Assert.True(fs.PathsReferToSameFile(p, alias));

        var identity = fs.GetIdentity(p);
        Assert.Equal(5, identity.SizeBytes);
        Assert.NotNull(identity.VolumeSerial);
        Assert.NotNull(identity.FileId);
        Assert.Equal(fs.NormalizePath(p), identity.FullPath);
    }

    [Fact]
    public void NormalizePath_TrimsTrailingSeparators_And_ResolvesRelativeParts()
    {
        using var dir = new TempDir();
        var fs = new WindowsFileSystemService();

        var withSeparator = dir.Path + Path.DirectorySeparatorChar;
        Assert.Equal(fs.NormalizePath(dir.Path), fs.NormalizePath(withSeparator));

        var expected = Path.Combine(dir.Path, "child");
        var messy = Path.Combine(dir.Path, "a", "..", "child");
        Assert.Equal(fs.NormalizePath(expected), fs.NormalizePath(messy));
    }

    [Fact]
    public void EnumerateFiles_HonorsRecursiveFlag()
    {
        using var dir = new TempDir();
        var fs = new WindowsFileSystemService();
        dir.NewFile("top.txt", "1");
        dir.NewFile(Path.Combine("nested", "deep.txt"), "2");

        Assert.Single(fs.EnumerateFiles(dir.Path, recursive: false));
        Assert.Equal(2, fs.EnumerateFiles(dir.Path, recursive: true).Count);
    }

    [Fact]
    public void GetAvailableFreeSpace_ReturnsPositiveForTempVolume()
    {
        using var dir = new TempDir();
        var fs = new WindowsFileSystemService();
        Assert.True(fs.GetAvailableFreeSpace(dir.Path) > 0);
    }
}
