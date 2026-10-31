using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Client.App.Services;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class LocalAsrWorkerCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-asr-coordinator-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task SelectedFreshInstall_RegistersHeartbeatsAndClaimsWithoutExecution()
    {
        Directory.CreateDirectory(_root);
        var scope = new LocalAccountScope(
            "https://api.example.test/tenant-a",
            Guid.Parse("11111111-1111-1111-1111-111111111111").ToString("D"));
        var runtime = RuntimeManifest(scope.OwnerIdentity);
        var model = ModelManifest();
        WriteInstalledRuntime(scope, runtime);
        WriteInstalledModel(scope, model);
        WriteCatalog(runtime, model);

        var handler = new StubHandler();
        var api = new ApiClient(() => handler);
        api.SetBaseUrl(scope.ServerBaseAddress);
        var runtimeStore = new RuntimeInstallStore(
            _root,
            new NeverDownloader(),
            new FixedDiskProbe());
        var modelStore = new ModelInstallStore(
            _root,
            new NeverDownloader(),
            new FixedDiskProbe());
        var verifier = new InstalledLocalAsrArtifactVerifier(
            () => scope,
            runtimeStore,
            modelStore);
        var transport = new LocalAsrApiTransport(
            api,
            () => scope,
            verifier.VerifyAsync,
            requestTimeout: TimeSpan.FromSeconds(2),
            maxSegmentBytes: 1024);
        var coordinator = new LocalAsrWorkerCoordinator(
            transport,
            verifier,
            new LocalRuntimeAccountGuard(),
            new ShouldNotExecuteAdapter(),
            new FixedResourceProvider(),
            new LocalRuntimeCatalogProvider(
                Path.Combine(_root, "catalog.json"),
                Path.Combine(_root, "missing-shipped")),
            new LocalAsrWorkerSelectionStore(_root),
            () => scope,
            _root,
            "test-node");

        var selected = await coordinator.SelectAsync(scope, runtime.RuntimeId, model.ModelId);
        Assert.True(selected.Selected, selected.Detail);
        Assert.True(coordinator.IsRunnable);

        var result = await coordinator.ProcessOnceAsync(
            scope,
            LocalAsrWorkTrigger.Manual);

        Assert.Equal(LocalAsrWorkerRunStatus.NoWork, result.Status);
        Assert.Contains(
            handler.Paths,
            path => path.EndsWith("/nodes/register", StringComparison.Ordinal));
        Assert.Contains(
            handler.Paths,
            path => path.EndsWith("/heartbeat", StringComparison.Ordinal));
        Assert.Contains(
            handler.Paths,
            path => path.EndsWith("/leases/claim", StringComparison.Ordinal));
        Assert.DoesNotContain(
            handler.Paths,
            path => path.Contains("/ack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingSelection_IsInvalidConfigurationWithoutNetwork()
    {
        Directory.CreateDirectory(_root);
        var scope = new LocalAccountScope(
            "https://api.example.test",
            Guid.Parse("22222222-2222-2222-2222-222222222222").ToString("D"));
        var handler = new StubHandler();
        var api = new ApiClient(() => handler);
        api.SetBaseUrl(scope.ServerBaseAddress);
        var runtimeStore = new RuntimeInstallStore(
            _root,
            new NeverDownloader(),
            new FixedDiskProbe());
        var modelStore = new ModelInstallStore(
            _root,
            new NeverDownloader(),
            new FixedDiskProbe());
        var verifier = new InstalledLocalAsrArtifactVerifier(
            () => scope,
            runtimeStore,
            modelStore);
        var coordinator = new LocalAsrWorkerCoordinator(
            new LocalAsrApiTransport(
                api,
                () => scope,
                verifier.VerifyAsync,
                requestTimeout: TimeSpan.FromSeconds(2),
                maxSegmentBytes: 1024),
            verifier,
            new LocalRuntimeAccountGuard(),
            new ShouldNotExecuteAdapter(),
            new FixedResourceProvider(),
            new LocalRuntimeCatalogProvider(
                Path.Combine(_root, "catalog.json"),
                Path.Combine(_root, "missing-shipped")),
            new LocalAsrWorkerSelectionStore(_root),
            () => scope,
            _root);

        await coordinator.RefreshAvailabilityAsync(scope);
        var result = await coordinator.ProcessOnceAsync(scope, LocalAsrWorkTrigger.Manual);

        Assert.False(coordinator.IsRunnable);
        Assert.Equal(LocalAsrWorkerRunStatus.InvalidConfiguration, result.Status);
        Assert.Equal(LocalRuntimeReasonCodes.InvalidConfiguration, result.ReasonCode);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task InstallationId_ScopesStableNodeIdentityPerWindowsInstall()
    {
        var scope = new LocalAccountScope(
            "https://api.example.test/tenant-a",
            Guid.Parse("33333333-3333-3333-3333-333333333333").ToString("D"));
        var runtime = RuntimeManifest(scope.OwnerIdentity);
        var model = ModelManifest();
        var installA = Path.Combine(_root, "install-a");
        var installB = Path.Combine(_root, "install-b");
        PrepareInstall(installA, scope, runtime, model);
        PrepareInstall(installB, scope, runtime, model);

        var handlerA = new StubHandler();
        var coordinatorA = CreateCoordinator(installA, scope, handlerA);
        Assert.True((await coordinatorA.SelectAsync(
            scope,
            runtime.RuntimeId,
            model.ModelId)).Selected);
        var nodeA = Assert.Single(handlerA.NodeIds.Distinct());

        var handlerA2 = new StubHandler();
        var coordinatorA2 = CreateCoordinator(installA, scope, handlerA2);
        Assert.True((await coordinatorA2.SelectAsync(
            scope,
            runtime.RuntimeId,
            model.ModelId)).Selected);
        var nodeA2 = Assert.Single(handlerA2.NodeIds.Distinct());

        var handlerB = new StubHandler();
        var coordinatorB = CreateCoordinator(installB, scope, handlerB);
        Assert.True((await coordinatorB.SelectAsync(
            scope,
            runtime.RuntimeId,
            model.ModelId)).Selected);
        var nodeB = Assert.Single(handlerB.NodeIds.Distinct());

        Assert.Equal(nodeA, nodeA2);
        Assert.NotEqual(nodeA, nodeB);
    }

    private void WriteCatalog(params object[] entries)
        => WriteCatalogAt(_root, entries);

    private static void WriteCatalogAt(string root, params object[] entries)
    {
        var runtime = (LocalRuntimeManifest)entries[0];
        var model = (LocalModelManifest)entries[1];
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            runtimes = new[]
            {
                new
                {
                    runtimeId = runtime.RuntimeId,
                    displayName = runtime.DisplayName,
                    artifactFileName = runtime.ArtifactFileName,
                    sourceUrl = runtime.SourceUrl,
                    licenseId = runtime.LicenseId,
                    licenseUrl = runtime.LicenseUrl,
                    revision = runtime.Revision,
                    sha256 = runtime.Sha256,
                    sizeBytes = runtime.SizeBytes,
                    archiveKind = "file",
                    executablePath = runtime.ExecutableRelativePath,
                    files = runtime.RequiredFiles.Select(file => new
                    {
                        path = file.RelativePath,
                        sizeBytes = file.SizeBytes,
                        sha256 = file.Sha256,
                    }),
                    execution = new
                    {
                        arguments = runtime.Execution.Arguments,
                        timeoutSeconds = (int)runtime.Execution.Timeout.TotalSeconds,
                        outputFormat = runtime.Execution.OutputFormat,
                    },
                },
            },
            models = new[]
            {
                new
                {
                    modelId = model.ModelId,
                    displayName = model.DisplayName,
                    runtimeId = model.RuntimeId,
                    licenseId = model.LicenseId,
                    licenseUrl = model.LicenseUrl,
                    sourceUrl = model.SourceUrl,
                    revision = model.Revision,
                    sha256 = model.Sha256,
                    sizeBytes = model.SizeBytes,
                    artifactFileName = model.ArtifactFileName,
                    resources = new
                    {
                        cpuCores = model.Resources.CpuCores,
                        memoryBytes = model.Resources.MemoryBytes,
                        gpuRequired = model.Resources.GpuRequired,
                    },
                },
            },
        });
        File.WriteAllText(Path.Combine(root, "catalog.json"), json);
    }

    private void WriteInstalledRuntime(
        LocalAccountScope scope,
        LocalRuntimeManifest manifest)
        => WriteInstalledRuntimeAt(_root, scope, manifest);

    private static void WriteInstalledRuntimeAt(
        string root,
        LocalAccountScope scope,
        LocalRuntimeManifest manifest)
    {
        var runtimeDirectory = Path.Combine(
            root,
            "runtime-accounts",
            PathSafety.HashAccountKey(scope.OwnerIdentity),
            manifest.RuntimeId);
        var version = new string('a', 64);
        var versionDirectory = Path.Combine(runtimeDirectory, "versions", version);
        Directory.CreateDirectory(versionDirectory);
        var executable = Path.Combine(versionDirectory, manifest.ExecutableRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable, Encoding.UTF8.GetBytes("runtime-file"));
        var record = new RuntimeInstallationRecord(
            manifest.RuntimeId,
            scope.OwnerIdentity,
            manifest.Revision,
            manifest.Sha256,
            manifest.SizeBytes,
            manifest.ArtifactFileName,
            manifest.ArtifactKind,
            version,
            manifest.ExecutableRelativePath,
            manifest.RequiredFiles,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(runtimeDirectory, "state.json"),
            JsonSerializer.Serialize(new RuntimeInstallStatus(
                RuntimeInstallState.Installed,
                record,
                null,
                null,
                DateTimeOffset.UtcNow)));
    }

    private void WriteInstalledModel(
        LocalAccountScope scope,
        LocalModelManifest manifest)
        => WriteInstalledModelAt(_root, scope, manifest);

    private static void WriteInstalledModelAt(
        string root,
        LocalAccountScope scope,
        LocalModelManifest manifest)
    {
        var modelDirectory = Path.Combine(
            root,
            "accounts",
            PathSafety.HashAccountKey(scope.OwnerIdentity),
            manifest.ModelId);
        Directory.CreateDirectory(modelDirectory);
        File.WriteAllBytes(
            Path.Combine(modelDirectory, manifest.ArtifactFileName),
            Encoding.UTF8.GetBytes("model-file"));
        var record = new ModelInstallationRecord(
            manifest.ModelId,
            scope.OwnerIdentity,
            manifest.Revision,
            manifest.Sha256,
            manifest.SizeBytes,
            manifest.ArtifactFileName,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(modelDirectory, "state.json"),
            JsonSerializer.Serialize(new ModelInstallStatus(
                ModelInstallState.Installed,
                record,
                null,
                null,
                DateTimeOffset.UtcNow)));
    }

    private static void PrepareInstall(
        string root,
        LocalAccountScope scope,
        LocalRuntimeManifest runtime,
        LocalModelManifest model)
    {
        Directory.CreateDirectory(root);
        WriteInstalledRuntimeAt(root, scope, runtime);
        WriteInstalledModelAt(root, scope, model);
        WriteCatalogAt(root, runtime, model);
    }

    private static LocalAsrWorkerCoordinator CreateCoordinator(
        string root,
        LocalAccountScope scope,
        StubHandler handler)
    {
        var api = new ApiClient(() => handler);
        api.SetBaseUrl(scope.ServerBaseAddress);
        var runtimeStore = new RuntimeInstallStore(
            root,
            new NeverDownloader(),
            new FixedDiskProbe());
        var modelStore = new ModelInstallStore(
            root,
            new NeverDownloader(),
            new FixedDiskProbe());
        var verifier = new InstalledLocalAsrArtifactVerifier(
            () => scope,
            runtimeStore,
            modelStore);
        return new LocalAsrWorkerCoordinator(
            new LocalAsrApiTransport(
                api,
                () => scope,
                verifier.VerifyAsync,
                requestTimeout: TimeSpan.FromSeconds(2),
                maxSegmentBytes: 1024),
            verifier,
            new LocalRuntimeAccountGuard(),
            new ShouldNotExecuteAdapter(),
            new FixedResourceProvider(),
            new LocalRuntimeCatalogProvider(
                Path.Combine(root, "catalog.json"),
                Path.Combine(root, "missing-shipped")),
            new LocalAsrWorkerSelectionStore(root),
            () => scope,
            root,
            "test-node");
    }

    private static LocalRuntimeManifest RuntimeManifest(string owner)
    {
        var bytes = Encoding.UTF8.GetBytes("runtime-file");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new LocalRuntimeManifest(
            "runtime-test",
            "Runtime Test",
            "helper.exe",
            "https://example.invalid/helper.exe",
            "MIT",
            "https://example.invalid/LICENSE",
            "runtime-r1",
            sha,
            bytes.LongLength,
            LocalRuntimeArtifactKind.File,
            "helper.exe",
            [new LocalRuntimeFileManifest("helper.exe", bytes.LongLength, sha)],
            new LocalRuntimeExecutionProfile(
                ["{inputPath}"],
                TimeSpan.FromSeconds(10),
                LocalRuntimeExecutionProfile.PlainText));
    }

    private static LocalModelManifest ModelManifest()
    {
        var bytes = Encoding.UTF8.GetBytes("model-file");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new LocalModelManifest(
            "model-test",
            "Model Test",
            "runtime-test",
            "MIT",
            "https://example.invalid/LICENSE",
            "https://example.invalid/model.bin",
            "model-r1",
            sha,
            bytes.LongLength,
            "model.bin",
            new LocalResourceRequirements(CpuCores: 1, MemoryBytes: 1024));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public List<string> NodeIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path.EndsWith("/leases/claim", StringComparison.Ordinal))
            {
                return Json("""
                    {"code":0,"message":"ok","timestamp":"2026-11-01T00:00:00Z","data":{"status":"empty"}}
                    """);
            }

            string? nodeId = null;
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("nodeId", out var node))
                {
                    nodeId = node.GetString();
                    if (nodeId is not null)
                    {
                        NodeIds.Add(nodeId);
                    }
                }
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            nodeId ??= segments.Length >= 5
                ? segments[segments.Length - 2]
                : "00000000-0000-0000-0000-000000000000";
            return Json(JsonSerializer.Serialize(new
            {
                code = 0,
                message = "ok",
                timestamp = DateTimeOffset.UtcNow,
                data = new
                {
                    capabilities = new[] { LocalAsrApiTransport.LocalAsrCapability },
                    claimable = true,
                    createdAt = DateTimeOffset.UtcNow,
                    enabled = true,
                    lastHeartbeatAt = DateTimeOffset.UtcNow,
                    modelId = "model-test",
                    modelRevision = "model-r1",
                    modelVerified = true,
                    nodeId,
                    nodeIdExternal = "test-node",
                    resourceSnapshotJson = "{}",
                    runtimeId = "runtime-test",
                    runtimeVersion = "runtime-r1",
                    sampledAt = DateTimeOffset.UtcNow,
                },
            }));
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class NeverDownloader : IArtifactDownloader
    {
        public Task DownloadAsync(
            Uri source,
            string destinationPath,
            long maxBytes,
            IProgress<long>? progress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("test must not download");
    }

    private sealed class FixedDiskProbe : IDiskSpaceProbe
    {
        public long GetAvailableFreeBytes(string directoryPath) => long.MaxValue;
    }

    private sealed class FixedResourceProvider : IResourceSnapshotProvider
    {
        public Task<ResourceSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceSnapshot(
                DateTimeOffset.UtcNow,
                Environment.ProcessorCount,
                0,
                1024L * 1024 * 1024,
                null,
                true,
                100));
    }

    private sealed class ShouldNotExecuteAdapter : ILocalSpeechRuntimeAdapter
    {
        public Task<LocalSpeechExecutionResult> ExecuteAsync(
            LocalRuntimeManifest runtimeManifest,
            LocalModelManifest modelManifest,
            LocalSpeechExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no-work claim must not execute");
    }
}
