using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

public partial class DeviceCenterWindow : Window
{
    private readonly DeviceCenterService _devices;
    private readonly AuthService _auth;
    private readonly ApiClient _api;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private long _selectionGeneration;
    private long _qualityGeneration;
    private bool _closed;

    public DeviceCenterWindow()
    {
        InitializeComponent();
        _devices = App.Services.GetRequiredService<DeviceCenterService>();
        _auth = App.Services.GetRequiredService<AuthService>();
        _api = App.Services.GetRequiredService<ApiClient>();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
        => await RefreshDevicesAsync();

    private async void OnRefresh(object sender, RoutedEventArgs e)
        => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _selectionGeneration);
        var account = CurrentAccount();
        try
        {
            if (account is null)
            {
                DevicesList.ItemsSource = null;
                ListStateText.Text = "请先登录 PIM 账户；设备目录只从当前账户读取。";
                DetailText.Text = "未登录，无法读取设备。";
                LoadQualityButton.IsEnabled = false;
                return;
            }

            ListStateText.Text = "正在读取设备…";
            var devices = await _devices.GetDevicesAsync(DateTimeOffset.UtcNow);
            if (_closed || generation != Interlocked.Read(ref _selectionGeneration)
                || account != CurrentAccount())
            {
                return;
            }

            DevicesList.ItemsSource = devices.Select(DeviceRow.From).ToArray();
            ListStateText.Text = devices.Count == 0
                ? "账户内还没有设备心跳。请先让 Windows 或其它端连接服务器。"
                : $"共 {devices.Count} 台设备；在线状态以最近 5 分钟心跳为准。";
            if (devices.Count == 0)
            {
                DetailText.Text = "暂无设备。";
            }
        }
        catch (Exception ex)
        {
            if (!_closed && generation == Interlocked.Read(ref _selectionGeneration)
                && account == CurrentAccount())
            {
                DevicesList.ItemsSource = null;
                ListStateText.Text = "读取设备失败。";
                ShowFailure("读取设备失败", ex);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        var generation = Interlocked.Increment(ref _qualityGeneration);
        if (DevicesList.SelectedItem is not DeviceRow row)
        {
            DetailText.Text = "选择左侧设备查看详情。";
            QualityText.Text = "尚未检查。";
            LoadQualityButton.IsEnabled = false;
            return;
        }

        LoadQualityButton.IsEnabled = true;
        QualityText.Text = "尚未检查。";
        DetailText.Text =
            $"设备标识：{row.Device.DeviceId}\n" +
            $"平台：{row.Device.Platform}\n" +
            $"在线状态：{row.Snapshot.PresenceLabel}\n" +
            $"最近心跳：{FormatHeartbeat(row.Device.LastHeartbeatAt)}\n" +
            $"App 版本：{row.Device.AppVersion ?? "未上报"}\n" +
            $"上传状态：{row.Device.UploadStatus}\n" +
            $"采集缓存：{row.Device.CollectionCacheCount} 条\n" +
            $"联网限制阻止：{row.Device.OnlineOnlyBlockedCount} 条";
        _ = generation;
    }

    private async void OnLoadQuality(object sender, RoutedEventArgs e)
    {
        if (DevicesList.SelectedItem is not DeviceRow row)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _qualityGeneration);
        var account = CurrentAccount();
        LoadQualityButton.IsEnabled = false;
        QualityText.Text = "正在检查…";
        try
        {
            var quality = await _devices.GetCollectionQualityAsync(row.Device.DeviceId);
            if (_closed || generation != Interlocked.Read(ref _qualityGeneration)
                || account != CurrentAccount()
                || !ReferenceEquals(DevicesList.SelectedItem, row))
            {
                return;
            }

            QualityText.Text =
                $"检查时间：{quality.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                $"平台：{quality.Platform}\n" +
                $"上传状态：{quality.UploadStatus}\n" +
                $"问题数：{quality.IssueCount}\n\n" +
                (quality.IssueCount == 0
                    ? "未发现采集质量问题。"
                    : "服务器报告存在质量问题；详细原因尚未在客户端契约中提供。");
        }
        catch (Exception ex)
        {
            if (!_closed && generation == Interlocked.Read(ref _qualityGeneration)
                && account == CurrentAccount()
                && ReferenceEquals(DevicesList.SelectedItem, row))
            {
                QualityText.Text = "检查采集质量失败。";
                ShowFailure("检查采集质量失败", ex);
            }
        }
        finally
        {
            if (!_closed && ReferenceEquals(DevicesList.SelectedItem, row))
            {
                LoadQualityButton.IsEnabled = true;
            }
        }
    }

    private AccountIdentity? CurrentAccount()
    {
        if (!_auth.IsAuthenticated || string.IsNullOrWhiteSpace(_auth.CurrentUserId))
        {
            return null;
        }

        return new AccountIdentity(_auth.CurrentUserId, _api.CurrentBaseUrl);
    }

    private static string FormatHeartbeat(DateTimeOffset? value)
        => value is null ? "从未心跳" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private void ShowFailure(string title, Exception exception)
    {
        var message = exception is HttpRequestException
            ? "无法连接服务器。请检查网络、服务器地址与登录状态。"
            : exception.Message;
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        Interlocked.Increment(ref _selectionGeneration);
        Interlocked.Increment(ref _qualityGeneration);
    }

    private sealed record AccountIdentity(string UserId, string ServerUrl);

    private sealed class DeviceRow
    {
        private DeviceRow(DeviceSnapshot snapshot)
        {
            Snapshot = snapshot;
            Device = snapshot.Status;
            PresenceBrush = snapshot.IsHeartbeatFresh
                ? new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(20, 184, 166))
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(100, 116, 139));
        }

        public DeviceSnapshot Snapshot { get; }
        public EndpointStatusDto Device { get; }
        public System.Windows.Media.Brush PresenceBrush { get; }
        public string PresenceLabel => Snapshot.PresenceLabel;
        public string Title => $"{Device.Platform} · {Device.DeviceId}";
        public string Subtitle =>
            $"{Snapshot.PresenceLabel} · 上传 {Device.UploadStatus} · 缓存 {Device.CollectionCacheCount}";

        public static DeviceRow From(DeviceSnapshot snapshot) => new(snapshot);
    }
}
