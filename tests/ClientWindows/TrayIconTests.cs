using Pim.Client.App;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class TrayIconTests
{
    [Fact]
    public void TrayMenu_ContainsAboutAndCheckUpdate()
    {
        var menu = TrayIcon.BuildMenu(version: "2026.08.212");
        Assert.Contains(menu.Items, i => i.Text == "关于" || i.Text.StartsWith("关于") || i.Text.Contains("关于"));
        Assert.Contains(menu.Items, i => i.Text == "检查更新");
    }

    [Fact]
    public void TrayMenu_AboutContainsVersion()
    {
        var menu = TrayIcon.BuildMenu(version: "2026.08.212");
        var about = menu.Items.First(i => i.Text.Contains("关于"));
        Assert.Contains("2026.08.212", about.Text);
    }

    [Fact]
    public void TrayMenu_AboutUsesSingleVAndShowsDisplayWithMachine()
    {
        var menu = TrayIcon.BuildMenu(version: "26.11.1234", displayVersion: "1.0.0");
        var about = menu.Items.First(i => i.Text.Contains("关于"));

        Assert.Equal("关于 PIM v1.0.0 (26.11.1234)", about.Text);
        Assert.DoesNotContain("vv", about.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MachineVersionHasNoDisplayFormatting()
    {
        var version = TrayIcon.GetVersion();

        Assert.DoesNotContain("v", version, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(", version);
        Assert.DoesNotContain(")", version);
    }

    [Fact]
    public void TrayMenu_FallbackVersion_WhenNull()
    {
        var menu = TrayIcon.BuildMenu(version: null);
        Assert.NotEmpty(menu.Items);
        Assert.Contains(menu.Items, i => i.Text.Contains("关于"));
    }
}
