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
    public void TrayMenu_FallbackVersion_WhenNull()
    {
        var menu = TrayIcon.BuildMenu(version: null);
        Assert.NotEmpty(menu.Items);
        Assert.Contains(menu.Items, i => i.Text.Contains("关于"));
    }

    [Theory]
    [InlineData(true, null, 2, "00:01:05", "录音中 · 第 2 段 · 00:01:05")]
    [InlineData(true, "系统睡眠，录音已暂停；唤醒后将尝试继续。", 3, "00:00:09", "录音已暂停 · 第 3 段 · 00:00:09")]
    [InlineData(false, null, 0, "00:00:00", "录音：未开始")]
    public void RecordingStatus_ShowsSegmentAndPauseState(
        bool isRecording,
        string? error,
        int segment,
        string elapsed,
        string expected)
    {
        var time = TimeSpan.Parse(elapsed);
        var state = new WindowsRecordingState(
            isRecording,
            Guid.NewGuid(),
            isRecording ? Guid.NewGuid() : null,
            segment,
            time,
            0.25,
            error);

        Assert.Equal(expected, TrayIcon.RecordTrayStatus(state));
    }

    [Fact]
    public void RecordingStateMakesStartAndStopMenuItemsMutuallyExclusive()
    {
        using var tray = new TrayIcon();
        using var menu = new System.Windows.Forms.ContextMenuStrip();
        tray.BuildContextMenu(menu);
        var start = menu.Items
            .OfType<System.Windows.Forms.ToolStripMenuItem>()
            .Single(item => item.Text == "开始笔记录音");
        var stop = menu.Items
            .OfType<System.Windows.Forms.ToolStripMenuItem>()
            .Single(item => item.Text == "停止并保存笔记录音");

        tray.ApplyRecordingState(new WindowsRecordingState(
            true,
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            TimeSpan.FromSeconds(12),
            0.25,
            null));

        Assert.False(start.Enabled);
        Assert.True(stop.Enabled);

        tray.ApplyRecordingState(new WindowsRecordingState(
            false,
            null,
            null,
            0,
            TimeSpan.Zero,
            0,
            null));

        Assert.True(start.Enabled);
        Assert.False(stop.Enabled);
    }
}
