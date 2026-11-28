using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

public partial class NativeWorkbenchWindow : Window
{
    private readonly AuthService _auth;
    private readonly ApiClient _api;

    public NativeWorkbenchWindow()
        : this(
            App.Services.GetRequiredService<AuthService>(),
            App.Services.GetRequiredService<ApiClient>())
    {
    }

    internal NativeWorkbenchWindow(AuthService auth, ApiClient api)
    {
        InitializeComponent();
        _auth = auth;
        _api = api;
        _api.ExternalScopeChanged += OnExternalScopeChanged;
        RefreshAccountState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => RefreshAccountState();

    private void RefreshAccountState()
    {
        AccountStateText.Text = _auth.IsAuthenticated
            ? $"账户状态：{_auth.CurrentUsername} 已登录"
            : "账户状态：未登录";
        ServerStateText.Text = $"服务器：{_api.CurrentBaseUrl}";
        WorkspaceStateText.Text = _auth.IsAuthenticated
            ? "原生入口已就绪。"
            : "原生入口仍可打开；登录后才能读取当前账户的数据。";
    }

    private void OnExternalScopeChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)RefreshAccountState);
            return;
        }

        RefreshAccountState();
    }

    private void OnOpenToday(object sender, RoutedEventArgs e)
        => App.ShowTodayWindow(this);

    private void OnOpenPlanner(object sender, RoutedEventArgs e)
        => App.ShowPlannerWindow(this);

    private void OnOpenNotes(object sender, RoutedEventArgs e)
        => App.ShowNotesWindow();

    private void OnOpenComputerHistory(object sender, RoutedEventArgs e)
        => WindowSummoner.ShowOrActivate(
            Application.Current.Windows.OfType<ComputerHistoryWindow>(),
            () => new ComputerHistoryWindow { Owner = this });

    private void OnOpenAppKnowledge(object sender, RoutedEventArgs e)
        => WindowSummoner.ShowOrActivate(
            Application.Current.Windows.OfType<AppKnowledgeWindow>(),
            () => new AppKnowledgeWindow { Owner = this });

    private void OnOpenDeviceCenter(object sender, RoutedEventArgs e)
        => WindowSummoner.ShowOrActivate(
            Application.Current.Windows.OfType<DeviceCenterWindow>(),
            () => new DeviceCenterWindow { Owner = this });

    private void OnOpenStatus(object sender, RoutedEventArgs e)
        => WindowSummoner.ShowOrActivate(
            Application.Current.Windows.OfType<StatusWindow>(),
            () => new StatusWindow());

    private void OnOpenWebToday(object sender, RoutedEventArgs e)
        => WebCompatibilityLinks.Open(_api.CurrentBaseUrl, "/today");

    private void OnOpenWebQuickNotes(object sender, RoutedEventArgs e)
        => WebCompatibilityLinks.Open(_api.CurrentBaseUrl, "/quick-notes");

    private void OnOpenWebWorkbench(object sender, RoutedEventArgs e)
        => WebCompatibilityLinks.Open(_api.CurrentBaseUrl, "/");

    private void OnClosed(object? sender, EventArgs e)
        => _api.ExternalScopeChanged -= OnExternalScopeChanged;
}
