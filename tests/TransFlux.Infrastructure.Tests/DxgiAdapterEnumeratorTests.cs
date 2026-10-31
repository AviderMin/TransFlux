using TransFlux.Domain.Media;
using TransFlux.Hardware.Windows;

namespace TransFlux.Infrastructure.Tests;

public sealed class DxgiAdapterEnumeratorTests
{
    [Fact]
    public void Enumerate_ReturnsAdaptersWithStableIdentity()
    {
        var adapters = DxgiAdapterEnumerator.Enumerate();

        Assert.NotNull(adapters);
        Assert.NotEmpty(adapters);
        Assert.All(adapters, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.AdapterId));
            Assert.StartsWith("LUID-", a.AdapterId);
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
        });

        // This dev machine has an NVIDIA RTX 4060 and an Intel iGPU. At least one hardware
        // adapter with a known vendor must be visible through DXGI.
        Assert.Contains(adapters, a => !a.IsSoftware &&
            (a.Vendor == AdapterVendor.Nvidia || a.Vendor == AdapterVendor.Intel));
    }
}
