using TransFlux.App.Theming;

namespace TransFlux.Application.Tests;

/// <summary>UI review fix round: layout constants match the docs/03 scale and window spec.</summary>
public sealed class UiLayoutConstantsTests
{
    [Fact]
    public void SpacingFollowsTheDocumentedScale()
    {
        Assert.Equal(4, Space.Xs);
        Assert.Equal(8, Space.Sm);
        Assert.Equal(12, Space.Md);
        Assert.Equal(16, Space.Lg);
        Assert.Equal(24, Space.Xl);
        Assert.Equal(32, Space.Xxl);
    }

    [Fact]
    public void WindowAndPanelSizesMatchDocs03()
    {
        Assert.Equal(920, Space.WindowMinWidth);
        Assert.Equal(640, Space.WindowMinHeight);
    }
}
