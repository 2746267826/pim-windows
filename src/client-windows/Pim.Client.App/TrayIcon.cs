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
    private WindowsRecordingService? _recording;
    private System.Windows.Forms.ToolStripMenuItem? _startRecordingItem;
    private System.Windows.Forms.ToolStripMenuItem? _stopRecordingItem;
    private System.Windows.Forms.ToolStripMenuItem? _recordingStatusItem;

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
        var menu = new System.Windows.Forms.ContextMenuStrip();
        BuildContextMenu(menu);
        _recording = App.Services.GetService<WindowsRecordingService>();
        if (_recording is not null)
        {
            _recording.StateChanged += OnRecordingStateChanged;
            ApplyRecordingState(_recording.CurrentState);
        }

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = $"PIM 守护程序 - {GlobalHotKeyGesture.NativeNotes.DisplayName} 打开笔记",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => OpenNativeWorkbenchWindow();
    }

    internal void BuildContextMenu(System.Windows.Forms.ContextMenuStrip menu)
    {
        menu.Items.Add("原生工作台", null, (_, _) => OpenNativeWorkbenchWindow());
        menu.Items.Add("原生今日", null, (_, _) => OpenNativeTodayWindow());
        menu.Items.Add("原生日历与任务", null, (_, _) => OpenNativePlannerWindow());
        menu.Items.Add("打开笔记", null, (_, _) => App.ShowNotesWindow());
        _recordingStatusItem = new System.Windows.Forms.ToolStripMenuItem("录音：未开始")
        {
            Enabled = false,
        };
        menu.Items.Add(_recordingStatusItem);
        _startRecordingItem = new System.Windows.Forms.ToolStripMenuItem(
            "开始笔记录音",
            null,
            async (_, _) => await StartQuickNoteRecordingAsync());
        menu.Items.Add(_startRecordingItem);
        _stopRecordingItem = new System.Windows.Forms.ToolStripMenuItem(
            "停止并保存笔记录音",
            null,
            async (_, _) => await StopNoteRecordingAsync())
        {
            Enabled = false,
        };
        menu.Items.Add(_stopRecordingItem);
        menu.Items.Add("打开状态中心", null, (_, _) => ShowStatusWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("立即同步", null, async (_, _) => await TriggerSyncAsync());
        menu.Items.Add("Web 兼容：打开 Web 工作台", null, (_, _) => OpenWebWorkbench());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var version = GetVersion();
        var serverUrl = ResolveServerUrl(null);
        var trayMenu = BuildMenu(version, serverUrl);
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(trayMenu.Items[0].Text, null, (_, _) => ShowAboutBox(version, serverUrl)));
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(trayMenu.Items[1].Text, null, async (_, _) => await CheckUpdateAsync(version)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("登录...", null, (_, _) => ShowLogin());
        menu.Items.Add("退出", null, (_, _) => ConfirmAndExit());
    }

    internal void ApplyRecordingState(WindowsRecordingState state)
    {
        if (_startRecordingItem is not null)
        {
            _startRecordingItem.Enabled = !state.IsRecording;
        }

        if (_stopRecordingItem is not null)
        {
            _stopRecordingItem.Enabled = state.IsRecording;
        }

        var status = RecordTrayStatus(state);
        if (_recordingStatusItem is not null)
        {
            _recordingStatusItem.Text = status;
        }

        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = state.IsRecording
                ? $"PIM - 录音 第{state.SegmentNumber}段 {state.Elapsed:mm\\:ss}"
                : $"PIM 守护程序 - {GlobalHotKeyGesture.NativeNotes.DisplayName} 打开笔记";
        }
    }

    internal static string RecordTrayStatus(WindowsRecordingState state)
        => state.IsRecording
            ? state.Error is not null
                ? $"录音已暂停 · 第 {state.SegmentNumber} 段 · {state.Elapsed:mm\\:ss}"
                : $"录音中 · 第 {state.SegmentNumber} 段 · {state.Elapsed:mm\\:ss}"
            : "录音：未开始";

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

    private static void OpenNativeTodayWindow()
        => App.ShowTodayWindow();

    private static void OpenNativePlannerWindow()
        => App.ShowPlannerWindow();

    private static void OpenNativeWorkbenchWindow()
        => App.ShowWorkbenchWindow();

    internal static bool ActivateOrCreateWindow<T>(
        IEnumerable<T> windows,
        Action<T> activate,
        Action create)
    {
        var existing = windows.FirstOrDefault();
        if (existing is not null)
        {
            activate(existing);
            return true;
        }

        create();
        return false;
    }

    private static void OpenWebWorkbench()
        => WebCompatibilityLinks.Open(DaemonConfig.Load().ServerUrl, "/");

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

    private static async Task StartQuickNoteRecordingAsync()
    {
        try
        {
            var recording = App.Services.GetRequiredService<WindowsRecordingService>();
            if (recording.IsRecording)
            {
                System.Windows.Forms.MessageBox.Show("已有录音正在进行。", "PIM 笔记",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
                return;
            }

            var store = App.Services.GetRequiredService<Pim.Client.Core.Services.Notes.LocalNoteStore>();
            var note = await store.CreateAsync($"录音笔记 {DateTime.Now:yyyy-MM-dd HH:mm}");
            await recording.StartAsync(note.NoteId);
            App.ShowNotesWindow();
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show($"无法开始主动录音：\n{ex.Message}", "PIM 笔记",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
        }
    }

    private static async Task StopNoteRecordingAsync()
    {
        try
        {
            var recording = App.Services.GetRequiredService<WindowsRecordingService>();
            var result = await recording.StopAsync();
            if (result is null)
            {
                System.Windows.Forms.MessageBox.Show("当前没有正在进行的录音。", "PIM 笔记",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
                return;
            }

            System.Windows.Forms.MessageBox.Show(
                $"录音已保存到本机笔记固定版本。\n时长：{result.Wave.Duration:mm\\:ss}\n云端尚未提交。",
                "PIM 笔记",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show($"录音已保留，但未能写入笔记库：\n{ex.Message}", "PIM 笔记",
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
        if (_recording is not null)
        {
            _recording.StateChanged -= OnRecordingStateChanged;
            _recording = null;
        }

        _notifyIcon?.Dispose();
        _notifyIcon = null;
    }

    private void OnRecordingStateChanged(object? sender, WindowsRecordingState state)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyRecordingState(state));
            return;
        }

        ApplyRecordingState(state);
    }
}
