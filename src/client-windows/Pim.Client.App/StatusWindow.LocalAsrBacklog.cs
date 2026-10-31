using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.App.Services;
using Pim.Client.Core.Services;
using Pim.Client.Core.Services.LocalRuntime;

namespace Pim.Client.App;

public partial class StatusWindow
{
    private LocalAsrBacklogViewModel? _localAsrBacklog;
    private bool _updatingLocalAsrBacklogUi;
    private bool _localAsrBacklogViewClosed;

    private void InitializeLocalAsrBacklog()
    {
        _localAsrBacklog = App.Services.GetRequiredService<LocalAsrBacklogViewModel>();
        _localAsrBacklog.PropertyChanged += OnLocalAsrBacklogPropertyChanged;
        _ = InitializeLocalAsrBacklogAsync();
    }

    private async Task InitializeLocalAsrBacklogAsync()
    {
        if (_localAsrBacklog is null || _localAsrBacklogViewClosed)
        {
            return;
        }

        try
        {
            await _localAsrBacklog.InitializeAsync();
            if (_localAsrBacklogViewClosed)
            {
                return;
            }

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
            if (!_localAsrBacklogViewClosed)
            {
                UpdateLocalAsrBacklogView();
            }
        }
    }

    private async Task SyncLocalAsrBacklogScopeAsync()
    {
        if (_localAsrBacklog is null || _localAsrBacklogViewClosed)
        {
            return;
        }

        var scope = CurrentLocalAsrScope();

        await _localAsrBacklog.SetScopeAsync(scope);
    }

    private LocalAccountScope? CurrentLocalAsrScope()
    {
        if (!_authService.IsAuthenticated
            || string.IsNullOrWhiteSpace(_authService.CurrentUserId))
        {
            return null;
        }

        try
        {
            var serverUrl = ApiClient.NormalizeServerUrl(_authService.ServerUrl);
            return new LocalAccountScope(serverUrl, _authService.CurrentUserId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async void OnAutoAsrToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingLocalAsrBacklogUi
            || _localAsrBacklog is null
            || _localAsrBacklogViewClosed)
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
        if (_localAsrBacklog is null || _localAsrBacklogViewClosed)
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
        if (_localAsrBacklog is null || _localAsrBacklogViewClosed)
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
        if (_localAsrBacklog is null
            || _localAsrBacklogViewClosed
            || LocalAsrBacklogStateText is null)
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
        _localAsrBacklogViewClosed = true;
        if (_localAsrBacklog is null)
        {
            return;
        }

        _localAsrBacklog.PropertyChanged -= OnLocalAsrBacklogPropertyChanged;
        _localAsrBacklog = null;
    }
}
