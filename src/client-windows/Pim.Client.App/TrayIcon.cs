using System.Diagnostics;
using System.Drawing;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.App.Services;
using Pim.Client.Core;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

public partial class TrayIcon : IDisposable
{
    private System.Windows.Forms.NotifyIcon? _notifyIcon;

    /// <summary>安全告警气泡（登录凭据保存失败等）。NotifyIcon 未就绪时静默，由调用方日志兜底。</summary>
    public void NotifyWarning(string text)
    {
        _notifyIcon?.ShowBalloonTip(
            timeout: 5000,
            tipTitle: "PIM 守护程序",
            text,
            System.Windows.Forms.ToolTipIcon.Warning);
    }

    public void Show()
    {
        if (_notifyIcon is { Visible: true })
        {
            return;
        }

        _notifyIcon?.Dispose();
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "PIM 守护程序 - 点击查看状态",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
        };

        _notifyIcon.ContextMenuStrip.Items.Add("打开状态中心", null, (_, _) => ShowStatusWindow());
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add("立即同步", null, async (_, _) => await TriggerSyncAsync());
        _notifyIcon.ContextMenuStrip.Items.Add("在浏览器打开 Web 工作台", null, (_, _) => OpenWebWorkbench());
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var version = GetVersion();
        var serverUrl = ResolveServerUrl(null);
        var trayMenu = BuildMenu(version, serverUrl);
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem(trayMenu.Items[0].Text, null, (_, _) => ShowAboutBox(version, serverUrl)));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem(trayMenu.Items[1].Text, null, async (_, _) => await CheckUpdateAsync(version)));
        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add("登录...", null, (_, _) => ShowLogin());
        _notifyIcon.ContextMenuStrip.Items.Add("退出", null, (_, _) => ConfirmAndExit());

        _notifyIcon.DoubleClick += (_, _) => ShowStatusWindow();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Pim.Client.App;component/app.ico"));
            if (streamInfo?.Stream is not null)
            {
                return new Icon(streamInfo.Stream);
            }
        }
        catch (IOException)
        {
        }

        return SystemIcons.Application;
    }

    private static void ShowStatusWindow()
    {
        var existing = System.Windows.Application.Current.Windows.OfType<StatusWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        new StatusWindow().Show();
    }

    private static void OpenWebWorkbench()
    {
        try
        {
            var config = DaemonConfig.Load();
            var root = string.IsNullOrWhiteSpace(config.ServerUrl)
                ? ClientDefaults.DefaultServerUrl
                : ApiClient.NormalizeServerUrl(config.ServerUrl);
            root = root.TrimEnd('/');
            if (root.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            {
                root = root[..^"/api/v1".Length].TrimEnd('/');
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                root = ClientDefaults.DefaultServerUrl;
            }

            var url = $"{root}/today";
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show($"打开 Web 工作台失败：{ex.Message}", "PIM",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    private static void ShowLogin()
    {
        new LoginWindow().ShowDialog();
    }

    private static async Task TriggerSyncAsync()
    {
        try
        {
            var keyStatsCollector = App.Services.GetRequiredService<KeyStatsCollectorService>();
            await keyStatsCollector.SyncNowAsync();

            var tracker = App.Services.GetService<NativeTrackerService>();
            var uploadErrors = BuildUploadErrorMessage(tracker?.LastError, keyStatsCollector.LastUploadError);
            if (uploadErrors is not null)
            {
                System.Windows.Forms.MessageBox.Show($"同步已执行，但上传仍有错误：\n{uploadErrors}", "PIM",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
            }
            else
            {
                System.Windows.Forms.MessageBox.Show("同步完成", "PIM",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show($"同步失败：{ex.Message}", "PIM",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    private static string? BuildUploadErrorMessage(string? trackerError, string? keyStatsError)
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(trackerError))
        {
            errors.Add($"Tracker: {trackerError}");
        }

        if (!string.IsNullOrWhiteSpace(keyStatsError))
        {
            errors.Add($"KeyStats: {keyStatsError}");
        }

        return errors.Count == 0 ? null : string.Join("\n", errors);
    }

    private void ConfirmAndExit()
    {
        var result = System.Windows.Forms.MessageBox.Show(
            "确定要退出 PIM 守护程序吗？",
            "PIM",
            System.Windows.Forms.MessageBoxButtons.YesNo,
            System.Windows.Forms.MessageBoxIcon.Question);

        if (result == System.Windows.Forms.DialogResult.Yes)
        {
            Dispose();
            System.Windows.Application.Current.Shutdown();
        }
    }

    private static string ResolveServerUrl(string? serverUrl)
    {
        if (!string.IsNullOrWhiteSpace(serverUrl)) return serverUrl!.Trim();
        try { return DaemonConfig.Load().ServerUrl?.Trim() ?? ClientDefaults.DefaultServerUrl; }
        catch { return ClientDefaults.DefaultServerUrl; }
    }

    private static void ShowAboutBox(string version, string serverUrl)
    {
        var url = ResolveServerUrl(serverUrl);
        System.Windows.Forms.MessageBox.Show($"PIM Daemon v{version}\nAPI: {url}", "关于");
    }

    /// <summary>
    /// 检查更新（接口边界契约化后走 <see cref="ApiClient"/> 的显式方法：
    /// 路径来自契约生成物 <c>ContractOperation.GetClientShellLatest</c>，响应类型是生成的
    /// <c>ClientShellLatestResponse</c> —— 不再有手写路径与手写 <c>LatestDto</c>）。
    /// 服务器地址取自同一个 <see cref="ApiClient"/>（与守护程序其余请求同源、同样规范化）。
    /// </summary>
    private static async Task CheckUpdateAsync(string version)
    {
        try
        {
            var api = App.Services.GetRequiredService<ApiClient>();
            // 显式 5 秒超时（改造前那个独立 HttpClient 就是 5 秒；共享 ApiClient 默认是 100 秒，
            // 用户点「检查更新」不该在弱网下静默挂 100 秒）。
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var latest = await api.GetClientShellLatestAsync(timeout.Token);

            if (latest?.Error != null)
            {
                Logger.Warn($"Daemon update check failed: {latest.Error} checkedAt={latest.CheckedAt}");
                System.Windows.Forms.MessageBox.Show($"检查失败：{latest.Error}", "PIM");
            }
            else if (latest?.WindowsVersion != null && UpdateChecker.IsNewer(version, latest.WindowsVersion))
            {
                System.Windows.Forms.MessageBox.Show($"发现新版 {latest.WindowsVersion}\n{latest.WindowsUrl}", "PIM");
            }
            else
            {
                System.Windows.Forms.MessageBox.Show("已是最新版本", "PIM");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Daemon update check failed: {ex.Message}");
            System.Windows.Forms.MessageBox.Show($"检查失败：{ex.Message}", "PIM");
        }
    }

    public void Dispose()
    {
        _notifyIcon?.Dispose();
        _notifyIcon = null;
    }
}
