using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class ModelInstallStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-model-store-" + Guid.NewGuid().ToString("N"));

    private static readonly byte[] ModelBytes = "synthetic-model-bytes"u8.ToArray();

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
    }

    [Fact]
    public async Task Install_WritesVerifiedArtifactAndIsIdempotent()
    {
        var downloader = new FakeDownloader(ModelBytes);
        var store = CreateStore(downloader);
        var manifest = CreateManifest(ModelBytes);

        var installed = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(ModelInstallState.Installed, installed.State);
        Assert.True(installed.IsExecutable);
        Assert.Equal(1, downloader.CallCount);
        var artifact = Path.Combine(
            store.Root,
            "accounts",
            PathSafety.HashAccountKey("account-a"),
            manifest.ModelId,
            manifest.ArtifactFileName);
        Assert.True(File.Exists(artifact));
        Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(artifact));
        Assert.Empty(Directory.GetFiles(Path.Combine(store.Root, ".staging")));

        var again = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(ModelInstallState.Installed, again.State);
        Assert.Equal(1, downloader.CallCount);
        Assert.Equal(ModelInstallState.Installed, (await store.VerifyAsync("account-a", manifest.ModelId)).State);
    }

    [Fact]
    public async Task Install_HashMismatch_RemovesTemporaryAndArtifact()
    {
        var store = CreateStore(new FakeDownloader(ModelBytes));
        var manifest = CreateManifest(ModelBytes, sha256: new string('0', 64));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(ModelInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.HashMismatch, status.ReasonCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(store.Root, ".staging")));
        var modelDir = Path.Combine(store.Root, "accounts", PathSafety.HashAccountKey("account-a"), manifest.ModelId);
        Assert.Empty(Directory.GetFiles(modelDir, "*.part"));
    }

    [Fact]
    public async Task Install_Cancelled_IsDurableAndCleansPartials()
    {
        var store = CreateStore(new FakeDownloader(ModelBytes, cancel: true));
        var manifest = CreateManifest(ModelBytes);

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(ModelInstallState.Cancelled, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.Cancelled, status.ReasonCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(store.Root, ".staging")));
        var reloaded = await store.GetStatusAsync("account-a", manifest.ModelId);
        Assert.Equal(ModelInstallState.Cancelled, reloaded.State);
    }

    [Fact]
    public async Task Install_DiskInsufficient_DoesNotDownload()
    {
        var downloader = new FakeDownloader(ModelBytes);
        var store = CreateStore(downloader, freeBytes: 1);
        var manifest = CreateManifest(ModelBytes);

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(ModelInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.DiskInsufficient, status.ReasonCode);
        Assert.Equal(0, downloader.CallCount);
    }

    [Fact]
    public async Task Install_UnverifiedManifest_IsRejectedWithoutDownload()
    {
        var downloader = new FakeDownloader(ModelBytes);
        var store = CreateStore(downloader);
        var manifest = CreateManifest(ModelBytes, sha256: string.Empty);

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(ModelInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.ManifestUnverified, status.ReasonCode);
        Assert.Equal(0, downloader.CallCount);
    }

    [Fact]
    public async Task Verify_DetectsCorruptedArtifact()
    {
        var store = CreateStore(new FakeDownloader(ModelBytes));
        var manifest = CreateManifest(ModelBytes);
        await store.InstallAsync(manifest, "account-a");
        var artifact = Path.Combine(
            store.Root,
            "accounts",
            PathSafety.HashAccountKey("account-a"),
            manifest.ModelId,
            manifest.ArtifactFileName);
        await File.WriteAllBytesAsync(artifact, "tampered"u8.ToArray());

        var status = await store.VerifyAsync("account-a", manifest.ModelId);

        Assert.Equal(ModelInstallState.Corrupt, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.HashMismatch, status.ReasonCode);
    }

    [Fact]
    public async Task Recover_ReconstructsInstallInterruptedBeforeStateWrite()
    {
        var store = CreateStore(new FakeDownloader(ModelBytes));
        var manifest = CreateManifest(ModelBytes);
        var installed = await store.InstallAsync(manifest, "account-a");
        var modelDir = Path.Combine(store.Root, "accounts", PathSafety.HashAccountKey("account-a"), manifest.ModelId);
        var statePath = Path.Combine(modelDir, "state.json");
        var pendingPath = Path.Combine(modelDir, "pending.json");
        await File.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(installed.Installation));
        File.Delete(statePath);

        var report = await store.RecoverAsync(verifyInstalledHashes: true);

        Assert.Equal(1, report.Reconstructed);
        Assert.False(File.Exists(pendingPath));
        var recovered = await store.GetStatusAsync("account-a", manifest.ModelId);
        Assert.Equal(ModelInstallState.Installed, recovered.State);
    }

    [Fact]
    public async Task Recover_DeletesStalePartials()
    {
        var store = CreateStore(new FakeDownloader(ModelBytes), stalePartialAge: TimeSpan.FromHours(1));
        var staging = Path.Combine(store.Root, ".staging");
        Directory.CreateDirectory(staging);
        var partial = Path.Combine(staging, "old.part");
        await File.WriteAllTextAsync(partial, "partial");
        File.SetLastWriteTimeUtc(partial, DateTime.UtcNow.AddHours(-3));

        var report = await store.RecoverAsync();

        Assert.Equal(1, report.StalePartialsDeleted);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task GetStatus_MissingArtifact_ReportsCorrupt()
    {
        var store = CreateStore(new FakeDownloader(ModelBytes));
        var manifest = CreateManifest(ModelBytes);
        await store.InstallAsync(manifest, "account-a");
        var modelDir = Path.Combine(store.Root, "accounts", PathSafety.HashAccountKey("account-a"), manifest.ModelId);
        File.Delete(Path.Combine(modelDir, manifest.ArtifactFileName));

        var status = await store.GetStatusAsync("account-a", manifest.ModelId);

        Assert.Equal(ModelInstallState.Corrupt, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.ArtifactMissing, status.ReasonCode);
    }

    [Fact]
    public async Task Status_IsScopedToOwningAccount()
    {
        var store = CreateStore(new FakeDownloader(ModelBytes));
        var manifest = CreateManifest(ModelBytes);
        await store.InstallAsync(manifest, "account-a");

        var other = await store.GetStatusAsync("account-b", manifest.ModelId);
        var uninstalled = await store.UninstallAsync("account-a", manifest.ModelId);

        Assert.Equal(ModelInstallState.NotInstalled, other.State);
        Assert.True(uninstalled);
        Assert.Equal(
            ModelInstallState.NotInstalled,
            (await store.GetStatusAsync("account-a", manifest.ModelId)).State);
    }

    [Fact]
    public async Task Status_TamperedOwner_ReportsOwnershipMismatch()
    {
        var store = CreateStore(new FakeDownloader(ModelBytes));
        var manifest = CreateManifest(ModelBytes);
        await store.InstallAsync(manifest, "account-a");
        var modelDir = Path.Combine(store.Root, "accounts", PathSafety.HashAccountKey("account-a"), manifest.ModelId);
        var statePath = Path.Combine(modelDir, "state.json");
        var json = await File.ReadAllTextAsync(statePath);
        var status = JsonSerializer.Deserialize<ModelInstallStatus>(json)!;
        var tampered = status with
        {
            Installation = status.Installation! with { AccountId = "account-b" },
        };
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(tampered));

        var read = await store.GetStatusAsync("account-a", manifest.ModelId);

        Assert.Equal(ModelInstallState.Corrupt, read.State);
        Assert.Equal(LocalRuntimeReasonCodes.OwnershipMismatch, read.ReasonCode);
    }

    [Fact]
    public async Task Install_InvalidArtifactName_IsRejected()
    {
        var store = CreateStore(new FakeDownloader(ModelBytes));
        var manifest = CreateManifest(ModelBytes) with { ArtifactFileName = "../escape.bin" };

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(ModelInstallState.Failed, status.State);
    }

    private ModelInstallStore CreateStore(
        FakeDownloader downloader,
        long freeBytes = 1024L * 1024 * 1024,
        TimeSpan? stalePartialAge = null) =>
        new(
            _root,
            downloader,
            new FakeDiskSpaceProbe(freeBytes),
            stalePartialAge: stalePartialAge,
            stagingMarginBytes: 0);

    private static LocalModelManifest CreateManifest(byte[] bytes, string? sha256 = null) =>
        new(
            "whisper-test-cpu",
            "Whisper test",
            "whisper-cpp-cpu",
            "MIT",
            "https://example.invalid/LICENSE",
            "https://example.invalid/model.bin",
            "r1",
            sha256 ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.Length,
            "model.bin",
            new LocalResourceRequirements(CpuCores: 2, MemoryBytes: 1024));

    private sealed class FakeDownloader : IArtifactDownloader
    {
        private readonly byte[] _bytes;
        private readonly bool _cancel;

        public FakeDownloader(byte[] bytes, bool cancel = false)
        {
            _bytes = bytes;
            _cancel = cancel;
        }

        public int CallCount { get; private set; }

        public async Task DownloadAsync(
            Uri source,
            string destinationPath,
            long maxBytes,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (_cancel)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, _bytes, cancellationToken);
            progress?.Report(_bytes.Length);
        }
    }

    private sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
    {
        private readonly long _freeBytes;

        public FakeDiskSpaceProbe(long freeBytes)
        {
            _freeBytes = freeBytes;
        }

        public long GetAvailableFreeBytes(string directoryPath) => _freeBytes;
    }
}
