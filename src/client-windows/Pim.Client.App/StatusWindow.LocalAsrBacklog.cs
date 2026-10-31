using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.App.Services;
using Pim.Client.Core.Services.LocalRuntime;

namespace Pim.Client.App;

public partial class StatusWindow
{
    private LocalAsrBacklogViewModel? _localAsrBacklog;
    private bool _updatingLocalAsrBacklogUi;

    private void InitializeLocalAsrBacklog()
    {
        _localAsrBacklog = App.Services.GetRequiredService<LocalAsrBacklogViewModel>();
        _localAsrBacklog.PropertyChanged += OnLocalAsrBacklogPropertyChanged;
        _ = InitializeLocalAsrBacklogAsync();
    }

    private async Task InitializeLocalAsrBacklogAsync()
    {
        if (_localAsrBacklog is null)
        {
            return;
        }

        try
        {
            await _localAsrBacklog.InitializeAsync();
            await SyncLocalAsrBacklogScopeAsync();
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException)
        {
            LocalAsrBacklogStateText.Text =
                "后台处理设置不可用：" + ex.GetType().Name;
        }
        finally
        {
            UpdateLocalAsrBacklogView();
        }
    }

    private async Task SyncLocalAsrBacklogScopeAsync()
    {
        if (_localAsrBacklog is null)
        {
            return;
        }

        LocalAccountScope? scope = null;
        if (_authService.IsAuthenticated
            && !string.IsNullOrWhiteSpace(_authService.CurrentUserId)
            && Uri.TryCreate(_authService.ServerUrl, UriKind.Absolute, out var serverUri)
            && (serverUri.Scheme == Uri.UriSchemeHttps
                || serverUri.Scheme == Uri.UriSchemeHttp))
        {
            // 仅用于边界比对；真实请求仍走 ApiClient 配置的完整 base URL（含子路径）。
            scope = new LocalAccountScope(
                serverUri.GetLeftPart(UriPartial.Authority),
                _authService.CurrentUserId);
        }

        await _localAsrBacklog.SetScopeAsync(scope);
    }

    private async void OnAutoAsrToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingLocalAsrBacklogUi || _localAsrBacklog is null)
        {
            return;
        }

        try
        {
            var enabled = AutoAsrCheckBox.IsChecked == true;
            if (!await _localAsrBacklog.SetAutomaticProcessingAsync(enabled))
            {
                _updatingLocalAsrBacklogUi = true;
                AutoAsrCheckBox.IsChecked = false;
                _updatingLocalAsrBacklogUi = false;
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LocalAsrBacklogStateText.Text =
                "保存后台处理设置失败：" + ex.GetType().Name;
        }
        finally
        {
            UpdateLocalAsrBacklogView();
        }
    }

    private async void OnProcessAsrOnce(object sender, RoutedEventArgs e)
    {
        if (_localAsrBacklog is null)
        {
            return;
        }

        ProcessAsrOnceButton.IsEnabled = false;
        try
        {
            await _localAsrBacklog.ProcessOnceAsync(LocalAsrWorkTrigger.Manual);
        }
        catch (Exception ex)
        {
            LocalAsrBacklogStateText.Text =
                "手动处理失败：" + ex.GetType().Name;
        }
        finally
        {
            UpdateLocalAsrBacklogView();
        }
    }

    private async void OnStopAsr(object sender, RoutedEventArgs e)
    {
        if (_localAsrBacklog is null)
        {
            return;
        }

        StopAsrButton.IsEnabled = false;
        try
        {
            await _localAsrBacklog.StopAsync();
        }
        catch (Exception ex)
        {
            LocalAsrBacklogStateText.Text =
                "停止处理失败：" + ex.GetType().Name;
        }
        finally
        {
            UpdateLocalAsrBacklogView();
        }
    }

    private void OnLocalAsrBacklogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(UpdateLocalAsrBacklogView));
    }

    private void UpdateLocalAsrBacklogView()
    {
        if (_localAsrBacklog is null || LocalAsrBacklogStateText is null)
        {
            return;
        }

        _updatingLocalAsrBacklogUi = true;
        try
        {
            AutoAsrCheckBox.IsChecked = _localAsrBacklog.AutomaticProcessingRequested;
            AutoAsrCheckBox.IsEnabled = _localAsrBacklog.IsWorkerAvailable;
            ProcessAsrOnceButton.IsEnabled =
                _localAsrBacklog.IsWorkerAvailable
                && _localAsrBacklog.IsScopeReady
                && !_localAsrBacklog.IsProcessing;
            StopAsrButton.IsEnabled =
                _localAsrBacklog.IsProcessing
                || _localAsrBacklog.AutomaticProcessingActive;

            var nextAttempt = _localAsrBacklog.NextAutomaticAttemptAtUtc is { } next
                ? $" · 下次自动检查 {next.ToLocalTime():HH:mm:ss}"
                : string.Empty;
            var failures = _localAsrBacklog.ConsecutiveFailures == 0
                ? string.Empty
                : $" · 连续失败 {_localAsrBacklog.ConsecutiveFailures}";
            LocalAsrBacklogStateText.Text =
                _localAsrBacklog.StatusText + nextAttempt + failures;
        }
        finally
        {
            _updatingLocalAsrBacklogUi = false;
        }
    }

    private void DisposeLocalAsrBacklog()
    {
        if (_localAsrBacklog is null)
        {
            return;
        }

        _localAsrBacklog.PropertyChanged -= OnLocalAsrBacklogPropertyChanged;
        _localAsrBacklog = null;
    }
}
