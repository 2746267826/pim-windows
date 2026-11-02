using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.App.Services;
using Pim.Client.Core.Services;
using Pim.Client.Core.Services.LocalRuntime;
using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

public static class Startup
{
    public static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core services
        services.AddSingleton<ApiClient>();
        services.AddSingleton<AuthService>();
        services.AddSingleton(_ => new LocalNoteStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PIM", "Notes")));
        services.AddSingleton<ServerNoteMaterialBundleBuilder>();
        services.AddSingleton<CloudNoteMaterialService>();
        services.AddSingleton<NoteTaskSuggestionService>(sp => new NoteTaskSuggestionService(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<LocalNoteStore>(),
            () => sp.GetRequiredService<CloudNoteMaterialService>().CurrentAccountScope,
            () => sp.GetRequiredService<CloudNoteMaterialService>().AccountGeneration));
        services.AddSingleton<WindowsRecordingService>();
        services.AddSingleton<TrackerLogger>();
        services.AddSingleton<TrackerHistoryStore>();
        services.AddSingleton<RemoteComputerHistoryService>();
        services.AddSingleton<DeviceCenterService>();
        services.AddSingleton<CalendarTaskService>();
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
            return new NativeTrackerService(
                api,
                cfg,
                null,
                null,
                bridge,
                logger,
                historyStore: sp.GetRequiredService<TrackerHistoryStore>());
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
            _ => new HttpArtifactDownloader(HttpArtifactDownloader.CreateHttpClient()));
        services.AddSingleton<IDiskSpaceProbe, DriveInfoDiskSpaceProbe>();
        services.AddSingleton(sp => new ModelInstallStore(
            LocalRuntimePaths.DefaultRoot,
            sp.GetRequiredService<IArtifactDownloader>(),
            sp.GetRequiredService<IDiskSpaceProbe>()));
        services.AddSingleton(sp => new RuntimeInstallStore(
            LocalRuntimePaths.DefaultRoot,
            sp.GetRequiredService<IArtifactDownloader>(),
            sp.GetRequiredService<IDiskSpaceProbe>()));
        services.AddSingleton(_ => new OwnedProcessRegistry(LocalRuntimePaths.OwnedProcessesFile()));
        services.AddSingleton<ControlledProcessRunner>();
        services.AddSingleton<IControlledProcessRunner>(
            sp => sp.GetRequiredService<ControlledProcessRunner>());
        services.AddSingleton<LocalRuntimeAccountGuard>();
        services.AddSingleton<ILocalSpeechRuntimeAdapter, LocalRuntimeExecutionAdapter>();
        services.AddSingleton<IResourceSnapshotProvider, WindowsResourceSnapshotProvider>();
        services.AddSingleton(sp => new InstalledLocalAsrArtifactVerifier(
            () => CurrentLocalAsrScope(sp.GetRequiredService<AuthService>()),
            sp.GetRequiredService<RuntimeInstallStore>(),
            sp.GetRequiredService<ModelInstallStore>()));
        services.AddSingleton(sp =>
        {
            var verifier = sp.GetRequiredService<InstalledLocalAsrArtifactVerifier>();
            return new LocalAsrApiTransport(
                sp.GetRequiredService<ApiClient>(),
                () => CurrentLocalAsrScope(sp.GetRequiredService<AuthService>()),
                verifier.VerifyAsync);
        });
        services.AddSingleton<ILocalAsrNodeTransport>(
            sp => sp.GetRequiredService<LocalAsrApiTransport>());
        services.AddSingleton(_ => new LocalRuntimeCatalogProvider());
        services.AddSingleton<ILocalAsrWorkerSelectionStore>(
            _ => new LocalAsrWorkerSelectionStore(LocalRuntimePaths.DefaultRoot));
        services.AddSingleton(sp => new LocalAsrWorkerCoordinator(
            sp.GetRequiredService<LocalAsrApiTransport>(),
            sp.GetRequiredService<InstalledLocalAsrArtifactVerifier>(),
            sp.GetRequiredService<LocalRuntimeAccountGuard>(),
            sp.GetRequiredService<ILocalSpeechRuntimeAdapter>(),
            sp.GetRequiredService<IResourceSnapshotProvider>(),
            sp.GetRequiredService<LocalRuntimeCatalogProvider>(),
            sp.GetRequiredService<ILocalAsrWorkerSelectionStore>(),
            () => CurrentLocalAsrScope(sp.GetRequiredService<AuthService>()),
            LocalRuntimePaths.DefaultRoot));
        services.AddSingleton<ILocalAsrWorkerRunner>(
            sp => sp.GetRequiredService<LocalAsrWorkerCoordinator>());
        services.AddSingleton<ILocalAsrWorkerSelection>(
            sp => sp.GetRequiredService<LocalAsrWorkerCoordinator>());
        services.AddSingleton<ILocalAsrWorkerRunnerAvailability>(
            sp => sp.GetRequiredService<LocalAsrWorkerCoordinator>());
        services.AddSingleton<ILocalAsrSchedulerSettingsStore>(
            _ => new LocalAsrSchedulerSettingsStore(
                LocalRuntimePaths.SchedulerSettingsFile()));
        services.AddSingleton(sp => new LocalAsrBacklogViewModel(
            sp.GetRequiredService<ILocalAsrWorkerRunner>(),
            sp.GetRequiredService<ILocalAsrSchedulerSettingsStore>()));
        services.AddSingleton<TrayIcon>();

        return services.BuildServiceProvider();
    }

    private static LocalAccountScope? CurrentLocalAsrScope(AuthService auth)
    {
        if (!auth.IsAuthenticated || string.IsNullOrWhiteSpace(auth.CurrentUserId))
        {
            return null;
        }

        try
        {
            var serverUrl = ApiClient.NormalizeServerUrl(auth.ServerUrl);
            return new LocalAccountScope(serverUrl, auth.CurrentUserId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
