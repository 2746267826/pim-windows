using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public sealed class WindowPlacementTests
{
    private static readonly WindowRectangle Primary = new(0, 0, 1920, 1040);
    private static readonly WindowRectangle Secondary = new(1920, 0, 1280, 1024);
    private static readonly WindowRectangle[] Monitors = [Primary, Secondary];

    [Fact]
    public void EnsureVisible_KeepsWindowOnSecondaryMonitor()
    {
        var window = new WindowRectangle(2200, 120, 900, 700);

        var result = WindowPlacement.EnsureVisible(window, Monitors);

        Assert.Equal(window, result);
    }

    [Fact]
    public void EnsureVisible_MovesWindowFromDisconnectedMonitorIntoPrimary()
    {
        var window = new WindowRectangle(5000, 300, 800, 600);

        var result = WindowPlacement.EnsureVisible(window, Monitors);

        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
        Assert.InRange(result.X, Primary.X, Primary.Right - result.Width);
        Assert.InRange(result.Y, Primary.Y, Primary.Bottom - result.Height);
    }

    [Fact]
    public void EnsureVisible_KeepsAUsablePartiallyVisibleWindow()
    {
        var window = new WindowRectangle(-500, 100, 800, 600);

        var result = WindowPlacement.EnsureVisible(window, Monitors);

        Assert.Equal(window, result);
    }

    [Fact]
    public void EnsureVisible_RepositionsWhenOnlyAnUnusableSliverIsVisible()
    {
        var window = new WindowRectangle(3190, 100, 800, 600);

        var result = WindowPlacement.EnsureVisible(window, Monitors);

        Assert.NotEqual(window, result);
        Assert.True(result.Right <= Secondary.Right);
        Assert.True(result.X >= Primary.X);
    }

    [Fact]
    public void EnsureVisible_ClampsOversizedWindowToWorkArea()
    {
        var window = new WindowRectangle(10, 20, 3000, 2000);

        var result = WindowPlacement.EnsureVisible(window, Monitors);

        Assert.Equal(Primary.Width, result.Width);
        Assert.Equal(Primary.Height, result.Height);
        Assert.Equal(Primary.X, result.X);
        Assert.Equal(Primary.Y, result.Y);
    }

    [Fact]
    public void EnsureVisible_WithoutMonitorDataDegradesToUnchangedBounds()
    {
        var window = new WindowRectangle(-2000, 100, 800, 600);

        var result = WindowPlacement.EnsureVisible(window, Array.Empty<WindowRectangle>());

        Assert.Equal(window, result);
    }
}
