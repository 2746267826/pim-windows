using System.IO;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class WindowsCompanionShellTests
{
    [Fact]
    public void LegacyEmbeddedShellAndWebViewDependencyAreRemoved()
    {
        var projectFile = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "Pim.Client.App.csproj"));
        var appCode = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "App.xaml.cs"));

        Assert.DoesNotContain("Microsoft.Web.WebView2", projectFile, StringComparison.Ordinal);
        Assert.False(File.Exists(RepoPath(
            "src", "client-windows", "Pim.Client.App", "MainShellWindow.xaml")));
        Assert.False(File.Exists(RepoPath(
            "src", "client-windows", "Pim.Client.App", "MainShellWindow.xaml.cs")));
        Assert.False(File.Exists(RepoPath(
            "src", "client-windows", "Pim.Client.App", "EmbeddedWebViewHost.cs")));
        Assert.DoesNotContain("ShowMainShellWindow", appCode, StringComparison.Ordinal);
        Assert.DoesNotContain("EmbeddedWebViewHost", appCode, StringComparison.Ordinal);
        Assert.Contains("ShowWorkbenchWindow", appCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWorkbenchIsThePrimaryEntryAndKeepsExplicitWebCompatibility()
    {
        var trayCode = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "TrayIcon.cs"));
        var workbenchXaml = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "NativeWorkbenchWindow.xaml"));

        Assert.Contains("原生工作台", trayCode, StringComparison.Ordinal);
        Assert.Contains("OpenNativeWorkbenchWindow", trayCode, StringComparison.Ordinal);
        Assert.Contains("Web 兼容：打开 Web 工作台", trayCode, StringComparison.Ordinal);
        Assert.Contains("Title=\"PIM 原生工作台\"", workbenchXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"原生今日\"", workbenchXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"原生日历与任务\"", workbenchXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"原生笔记\"", workbenchXaml, StringComparison.Ordinal);
        Assert.Contains("今日（Web 兼容）", workbenchXaml, StringComparison.Ordinal);
        Assert.Contains("旧快速记录（Web 兼容）", workbenchXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView", workbenchXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void WebCompatibilityLinksAreSystemBrowserOnlyAndCarryNoCredentials()
    {
        var links = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "WebCompatibilityLinks.cs"));

        Assert.Contains("Process.Start", links, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = true", links, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView", links, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", links, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionStorage", links, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerDropsLegacyShellEntryAndPayloadButKeepsUpgradeCleanup()
    {
        var installer = File.ReadAllText(RepoPath("installer", "pim-setup.iss"));

        Assert.DoesNotContain("Name: \"{group}\\PIM Shell\"", installer, StringComparison.Ordinal);
        Assert.Contains("Excludes: \"Pim.Shell.App.*\"", installer, StringComparison.Ordinal);
        Assert.Contains("Name: \"{group}\\PIM Shell.lnk\"", installer, StringComparison.Ordinal);
        Assert.Contains("Pim.Shell.App.exe", installer, StringComparison.Ordinal);
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate)
                || Directory.Exists(Path.Combine(current, "src", "client-windows")))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }
}
