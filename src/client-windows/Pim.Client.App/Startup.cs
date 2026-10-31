using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.Core.Services;
using Pim.Client.Core.Services.LocalRuntime;

namespace Pim.Client.App;

public static class Startup
{
    public static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core services
        services.AddSingleton<ApiClient>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<TrackerLogger>();
        services.AddSingleton<BrowserBridgeService>(sp =>
        {
            var cfg = DaemonConfig.Load().Tracker;
            var logger = sp.GetRequiredService<TrackerLogger>();
            return new BrowserBridgeService(cfg.BrowserBridgePort, logger);
        });
        services.AddSingleton<NativeTrackerService>(sp =>
        {
            var api = sp.GetRequiredService<ApiClient>();
            var cfg = DaemonConfig.Load().Tracker;
            var logger = sp.GetRequiredService<TrackerLogger>();
            var bridge = sp.GetRequiredService<BrowserBridgeService>();
            return new NativeTrackerService(api, cfg, null, null, bridge, logger);
        });
        services.AddSingleton<KeyStatsProcessManager>();
        services.AddSingleton<KeyStatsCollectorService>();
        services.AddSingleton<DaemonHeartbeatReporter>();
        services.AddSingleton<PlannedOfflineReporter>();
        // 端点心跳（工单 06 / G1）：与守护进程心跳同一循环驱动，失败状态由状态窗口读取。
        services.AddSingleton<EndpointHeartbeatReporter>(sp =>
            new EndpointHeartbeatReporter(
                sp.GetRequiredService<ApiClient>(),
                sp.GetRequiredService<TrackerLogger>()));
        services.AddSingleton<Pim.Client.Core.Services.NotificationActionRouter>();
        services.AddSingleton<EndpointCollectionBoundaryService>();
        services.AddSingleton<NotificationActionRouter>();
        services.AddSingleton<IArtifactDownloader>(
            _ => new HttpArtifactDownloader(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
        services.AddSingleton<IDiskSpaceProbe, DriveInfoDiskSpaceProbe>();
        services.AddSingleton(sp => new ModelInstallStore(
            LocalRuntimePaths.DefaultRoot,
            sp.GetRequiredService<IArtifactDownloader>(),
            sp.GetRequiredService<IDiskSpaceProbe>()));
        services.AddSingleton(_ => new OwnedProcessRegistry(LocalRuntimePaths.OwnedProcessesFile()));
        services.AddSingleton<ControlledProcessRunner>();
        services.AddSingleton<IResourceSnapshotProvider, WindowsResourceSnapshotProvider>();
        services.AddSingleton<TrayIcon>();

        return services.BuildServiceProvider();
    }
}
