using System;
using System.IO;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public sealed class NativePlannerTrayEntryTests
{
    [Fact]
    public void TrayMenu_ExposesNativePlannerWithoutDroppingExistingCommands()
    {
        var trayCode = ReadTrayIcon();

        Assert.Contains(
            "ContextMenuStrip.Items.Add(\"原生日历与任务\", null, (_, _) => OpenNativePlannerWindow());",
            trayCode,
            StringComparison.Ordinal);
        foreach (var command in new[]
                 {
                     "打开笔记",
                     "开始笔记录音",
                     "停止并保存笔记录音",
                     "打开状态中心",
                     "立即同步",
                     "在浏览器打开 Web 工作台",
                     "登录...",
                     "退出",
                 })
        {
            Assert.Contains($"\"{command}\"", trayCode, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OpenNativePlannerWindow_ReusesSingletonAndDoesNotFallBackToBrowser()
    {
        var method = ExtractMethod(ReadTrayIcon(), "private static void OpenNativePlannerWindow()");

        Assert.Contains(
            "System.Windows.Application.Current.Windows.OfType<NativePlannerWindow>().FirstOrDefault()",
            method,
            StringComparison.Ordinal);
        Assert.Contains("existing.Activate();", method, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(method, "new NativePlannerWindow().Show();"));
        Assert.True(
            method.IndexOf("if (existing is not null)", StringComparison.Ordinal)
            < method.IndexOf("new NativePlannerWindow().Show();", StringComparison.Ordinal),
            "The existing-window guard must run before the single creation path.");
        Assert.DoesNotContain("OpenWebWorkbench", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", method, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView", method, StringComparison.Ordinal);
    }

    private static string ReadTrayIcon()
    {
        var path = RepoPath("src", "client-windows", "Pim.Client.App", "TrayIcon.cs");
        return File.ReadAllText(path);
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Could not find method {signature}.");
        }

        var end = source.IndexOf("\n    private static", start + signature.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }
}
