using TransFlux.Application.Contracts;
using TransFlux.Domain.Plans;

namespace TransFlux.Infrastructure.Tests;

public sealed class OutputPathServiceTests
{
    [Fact]
    public async Task Reserve_AllocatesUniqueName_ReservesLock_AndReleaseCleans()
    {
        using var dir = new TempDir();
        var fs = new WindowsFileSystemService();
        var svc = new OutputPathService(fs, new SystemClock());
        var source = dir.NewFile("clip.mp4", "source-bytes");

        var request = new OutputReservationRequest { SourcePath = source, Container = ContainerFormat.Mp4 };

        var r1 = await svc.ReserveAsync(request, default);
        Assert.True(r1.IsSuccess);
        var o1 = r1.Value!;
        Assert.Equal(Path.Combine(dir.Path, "TransFlux Output"), Path.GetDirectoryName(o1.FinalPath));
        Assert.EndsWith(".mp4", o1.FinalPath);
        Assert.True(Directory.Exists(o1.WorkDirectory));
        Assert.True(File.Exists(o1.FinalPath + ".transflux-reserve"));
        Assert.Contains(Path.Combine("TransFlux Output", ".transflux-work"), o1.WorkDirectory);

        var r2 = await svc.ReserveAsync(request, default);
        Assert.True(r2.IsSuccess);
        Assert.NotEqual(o1.FinalPath, r2.Value!.FinalPath);

        // If the first final file now exists, the next reservation must still avoid it.
        File.WriteAllText(o1.FinalPath, "existing-output");
        var r3 = await svc.ReserveAsync(request, default);
        Assert.True(r3.IsSuccess);
        Assert.NotEqual(o1.FinalPath, r3.Value!.FinalPath);

        svc.Release(o1);
        Assert.False(File.Exists(o1.FinalPath + ".transflux-reserve"));
        Assert.False(Directory.Exists(o1.WorkDirectory));

        svc.Release(r2.Value!);
        svc.Release(r3.Value!);
    }

    [Fact]
    public async Task Reserve_HonorsPreferredDirectory_And_SanitizesName()
    {
        using var dir = new TempDir();
        var fs = new WindowsFileSystemService();
        var svc = new OutputPathService(fs, new SystemClock());
        var source = dir.NewFile("clip.mp4", "x");
        var alt = Path.Combine(dir.Path, "custom-out");

        var result = await svc.ReserveAsync(new OutputReservationRequest
        {
            SourcePath = source,
            Container = ContainerFormat.Mkv,
            PreferredDirectory = alt,
            RequestedFileName = "My:Clip*Name",
        }, default);

        Assert.True(result.IsSuccess);
        var reserved = result.Value!;
        Assert.Equal(alt, Path.GetDirectoryName(reserved.FinalPath));
        Assert.EndsWith(".mkv", reserved.FinalPath);
        Assert.DoesNotContain(":", Path.GetFileName(reserved.FinalPath));
        Assert.DoesNotContain("*", Path.GetFileName(reserved.FinalPath));
        Assert.True(Directory.Exists(alt));
        svc.Release(reserved);
    }

    private static async Task<(OutputPathService Service, ReservedOutput Reserved)> ReserveInAsync(TempDir dir)
    {
        var service = new OutputPathService(new WindowsFileSystemService(), new SystemClock());
        var source = dir.NewFile("input.mp4", "source-bytes");
        var result = await service.ReserveAsync(new OutputReservationRequest
        {
            SourcePath = source,
            Container = ContainerFormat.Mp4,
            PreferredDirectory = dir.Path,
        }, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.TechnicalDetail);
        return (service, result.Value!);
    }

    [Fact]
    public async Task ReleaseReservation_DeletesLockOnly_AndKeepsWorkDirectoryAndStagedOutput()
    {
        using var dir = new TempDir();
        var (service, reserved) = await ReserveInAsync(dir);
        var staged = Path.Combine(reserved.WorkDirectory, "attempt-1.mp4");
        File.WriteAllText(staged, "verified-output");

        service.ReleaseReservation(reserved);

        Assert.False(File.Exists(reserved.FinalPath + ".transflux-reserve"));
        Assert.True(Directory.Exists(reserved.WorkDirectory));
        Assert.True(File.Exists(staged), "releasing the naming reservation must not delete staged output");
        Assert.False(File.Exists(reserved.FinalPath));
    }

    [Fact]
    public async Task DeleteWorkDirectory_DeletesStagingButKeepsReservationLock()
    {
        using var dir = new TempDir();
        var (service, reserved) = await ReserveInAsync(dir);
        var staged = Path.Combine(reserved.WorkDirectory, "attempt-1.mp4");
        File.WriteAllText(staged, "verified-output");

        service.DeleteWorkDirectory(reserved);

        Assert.True(File.Exists(reserved.FinalPath + ".transflux-reserve"));
        Assert.False(Directory.Exists(reserved.WorkDirectory));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public async Task LegacyRelease_DeletesReservationAndWorkDirectory()
    {
        using var dir = new TempDir();
        var (service, reserved) = await ReserveInAsync(dir);
        var staged = Path.Combine(reserved.WorkDirectory, "attempt-1.mp4");
        File.WriteAllText(staged, "verified-output");

        service.Release(reserved);

        Assert.False(File.Exists(reserved.FinalPath + ".transflux-reserve"));
        Assert.False(Directory.Exists(reserved.WorkDirectory));
        Assert.False(File.Exists(staged));
    }
}
