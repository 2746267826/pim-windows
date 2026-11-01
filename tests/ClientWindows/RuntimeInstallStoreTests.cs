using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class RuntimeInstallStoreTests : IDisposable
{
    private static readonly byte[] ExeBytes = "synthetic-runtime-exe"u8.ToArray();
    private static readonly byte[] DllBytes = "synthetic-runtime-dll"u8.ToArray();
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-runtime-store-" + Guid.NewGuid().ToString("N"));

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
    public async Task Install_VerifiedZip_ExtractsEveryDeclaredFileAndIsIdempotent()
    {
        var zip = CreateZip(
            ("runner.exe", ExeBytes, 0),
            ("runner.dll", DllBytes, 0));
        var downloader = new FakeDownloader(zip);
        var store = CreateStore(downloader);
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes), ("runner.dll", DllBytes));

        var installed = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Installed, installed.State);
        Assert.True(installed.IsExecutable);
        Assert.Equal(1, downloader.CallCount);
        var executable = store.ResolveInstalledFilePath(
            "account-a",
            installed.Installation!,
            "runner.exe");
        var library = store.ResolveInstalledFilePath(
            "account-a",
            installed.Installation!,
            "runner.dll");
        Assert.NotNull(executable);
        Assert.NotNull(library);
        Assert.Equal(ExeBytes, await File.ReadAllBytesAsync(executable!));
        Assert.Equal(DllBytes, await File.ReadAllBytesAsync(library!));

        var again = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Installed, again.State);
        Assert.Equal(1, downloader.CallCount);
        Assert.Equal(
            RuntimeInstallState.Installed,
            (await store.VerifyAsync(manifest, "account-a")).State);
    }

    [Fact]
    public async Task Install_RejectsArchiveTraversal()
    {
        var zip = CreateZip(("../runner.exe", ExeBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.ArchiveEntryUnsafe, status.ReasonCode);
        Assert.False(File.Exists(Path.Combine(_root, "runner.exe")));
    }

    [Fact]
    public async Task Install_RejectsAbsoluteArchivePath()
    {
        var zip = CreateZip(("C:/runner.exe", ExeBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.ArchiveEntryUnsafe, status.ReasonCode);
    }

    [Fact]
    public async Task Install_RejectsSymbolicLinks()
    {
        var symlinkAttributes = 0xA000 << 16;
        var zip = CreateZip(("runner.exe", ExeBytes, symlinkAttributes));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.ArchiveEntryUnsafe, status.ReasonCode);
    }

    [Fact]
    public async Task Install_RejectsWindowsReparseAttributes()
    {
        var reparseAttributes = (int)FileAttributes.ReparsePoint;
        var zip = CreateZip(("runner.exe", ExeBytes, reparseAttributes));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.ArchiveEntryUnsafe, status.ReasonCode);
    }

    [Fact]
    public async Task Install_RejectsReparseDirectoryEntries()
    {
        var reparseDirectory = (int)(FileAttributes.Directory | FileAttributes.ReparsePoint);
        var zip = CreateZip(("linked/", Array.Empty<byte>(), reparseDirectory));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.ArchiveEntryUnsafe, status.ReasonCode);
    }

    [Fact]
    public async Task Install_RejectsCaseInsensitiveDuplicateEntries()
    {
        var zip = CreateZip(
            ("runner.exe", ExeBytes, 0),
            ("RUNNER.EXE", ExeBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.ArchiveEntryUnsafe, status.ReasonCode);
    }

    [Fact]
    public async Task Install_RejectsUndeclaredArchiveFiles()
    {
        var zip = CreateZip(
            ("runner.exe", ExeBytes, 0),
            ("side-load.dll", DllBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.ArchiveEntryUnsafe, status.ReasonCode);
    }

    [Fact]
    public async Task Install_RejectsArchiveEntryCountBudget()
    {
        var zip = CreateZip(
            ("runner.exe", ExeBytes, 0),
            ("runner.dll", DllBytes, 0));
        var store = CreateStore(
            new FakeDownloader(zip),
            maxArchiveEntries: 1);
        var manifest = CreateManifest(
            zip,
            ("runner.exe", ExeBytes),
            ("runner.dll", DllBytes));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.ArchiveLimitExceeded, status.ReasonCode);
    }

    [Fact]
    public async Task Install_RejectsArchiveEntryAndCumulativeSizeBudgets()
    {
        var large = Enumerable.Repeat((byte)'x', 600 * 1024).ToArray();
        var zip = CreateZip(
            ("runner.exe", large, 0),
            ("runner.dll", large, 0));
        var entryStore = CreateStore(
            new FakeDownloader(zip),
            maxArchiveEntryBytes: 512 * 1024,
            maxExtractedBytes: 4 * 1024 * 1024);
        var cumulativeStore = CreateStore(
            new FakeDownloader(zip),
            maxArchiveEntryBytes: 1024 * 1024,
            maxExtractedBytes: 1024 * 1024);
        var manifest = CreateManifest(
            zip,
            ("runner.exe", large),
            ("runner.dll", large));

        var entryStatus = await entryStore.InstallAsync(manifest, "account-a");
        var cumulativeStatus = await cumulativeStore.InstallAsync(manifest, "account-a");

        Assert.Equal(LocalRuntimeReasonCodes.ArchiveLimitExceeded, entryStatus.ReasonCode);
        Assert.Equal(LocalRuntimeReasonCodes.ArchiveLimitExceeded, cumulativeStatus.ReasonCode);
    }

    [Fact]
    public async Task Install_DllDigestMismatch_IsNotPublished()
    {
        var zip = CreateZip(
            ("runner.exe", ExeBytes, 0),
            ("runner.dll", DllBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var differentDll = Enumerable.Repeat((byte)'x', DllBytes.Length).ToArray();
        var manifest = CreateManifest(
            zip,
            ("runner.exe", ExeBytes),
            ("runner.dll", differentDll));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.RuntimeFileHashMismatch, status.ReasonCode);
        var versions = Path.Combine(
            _root,
            "runtime-accounts",
            PathSafety.HashAccountKey("account-a"),
            manifest.RuntimeId,
            "versions");
        Assert.True(!Directory.Exists(versions) || !Directory.EnumerateFiles(
            versions,
            "runner.dll",
            SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Install_Cancelled_CleansArchiveAndExtraction()
    {
        var zip = CreateZip(("runner.exe", ExeBytes, 0));
        var store = CreateStore(new FakeDownloader(zip, cancel: true));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Cancelled, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.Cancelled, status.ReasonCode);
        var staging = Path.Combine(
            _root,
            "runtime-accounts",
            PathSafety.HashAccountKey("account-a"),
            ".staging");
        Assert.True(!Directory.Exists(staging)
                    || !Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any());
        Assert.False(Directory.Exists(Path.Combine(
            _root,
            "runtime-accounts",
            PathSafety.HashAccountKey("account-a"),
            manifest.RuntimeId,
            "versions")));
    }

    [Fact]
    public async Task Install_DiskInsufficient_DoesNotDownload()
    {
        var zip = CreateZip(("runner.exe", ExeBytes, 0));
        var downloader = new FakeDownloader(zip);
        var store = CreateStore(downloader, freeBytes: 1);
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));

        var status = await store.InstallAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Failed, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.DiskInsufficient, status.ReasonCode);
        Assert.Equal(0, downloader.CallCount);
    }

    [Fact]
    public async Task Recover_ReconstructsVersionMovedBeforeStateWrite()
    {
        var zip = CreateZip(("runner.exe", ExeBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));
        var installed = await store.InstallAsync(manifest, "account-a");
        var runtimeDirectory = Path.Combine(
            _root,
            "runtime-accounts",
            PathSafety.HashAccountKey("account-a"),
            manifest.RuntimeId);
        File.WriteAllText(
            Path.Combine(runtimeDirectory, "pending.json"),
            JsonSerializer.Serialize(installed.Installation));
        File.Delete(Path.Combine(runtimeDirectory, "state.json"));

        var report = await store.RecoverAsync(verifyInstalledHashes: true);

        Assert.Equal(1, report.Reconstructed);
        Assert.False(File.Exists(Path.Combine(runtimeDirectory, "pending.json")));
        Assert.Equal(
            RuntimeInstallState.Installed,
            (await store.GetStatusAsync("account-a", manifest.RuntimeId)).State);
    }

    [Fact]
    public async Task Recover_RejectsPendingRecordOwnedByAnotherAccount()
    {
        var zip = CreateZip(("runner.exe", ExeBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));
        var installed = await store.InstallAsync(manifest, "account-a");
        var accountARuntimeDirectory = Path.Combine(
            _root,
            "runtime-accounts",
            PathSafety.HashAccountKey("account-a"),
            manifest.RuntimeId);
        var accountBRuntimeDirectory = Path.Combine(
            _root,
            "runtime-accounts",
            PathSafety.HashAccountKey("account-b"),
            manifest.RuntimeId);
        Directory.CreateDirectory(accountBRuntimeDirectory);
        File.Copy(
            Path.Combine(accountARuntimeDirectory, "state.json"),
            Path.Combine(accountBRuntimeDirectory, "state.json"),
            overwrite: false);
        File.WriteAllText(
            Path.Combine(accountBRuntimeDirectory, "pending.json"),
            JsonSerializer.Serialize(installed.Installation));

        var report = await store.RecoverAsync(verifyInstalledHashes: true);
        var accountBStatus = await store.GetStatusAsync("account-b", manifest.RuntimeId);

        Assert.Equal(0, report.Reconstructed);
        Assert.Equal(1, report.MarkedCorrupt);
        Assert.Equal(RuntimeInstallState.Corrupt, accountBStatus.State);
        Assert.Equal(
            LocalRuntimeReasonCodes.RuntimeOwnershipMismatch,
            accountBStatus.ReasonCode);
        Assert.Equal(
            RuntimeInstallState.Installed,
            (await store.GetStatusAsync("account-a", manifest.RuntimeId)).State);
    }

    [Fact]
    public async Task Recover_RejectsPendingRecordForAnotherRuntimeDirectory()
    {
        var zip = CreateZip(("runner.exe", ExeBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));
        var installed = await store.InstallAsync(manifest, "account-a");
        var wrongRuntimeId = "whisper-cpp-other";
        var wrongRuntimeDirectory = Path.Combine(
            _root,
            "runtime-accounts",
            PathSafety.HashAccountKey("account-a"),
            wrongRuntimeId);
        Directory.CreateDirectory(wrongRuntimeDirectory);
        File.WriteAllText(
            Path.Combine(wrongRuntimeDirectory, "pending.json"),
            JsonSerializer.Serialize(installed.Installation));

        var report = await store.RecoverAsync(verifyInstalledHashes: true);
        var status = await store.GetStatusAsync("account-a", wrongRuntimeId);

        Assert.Equal(0, report.Reconstructed);
        Assert.Equal(1, report.MarkedCorrupt);
        Assert.Equal(RuntimeInstallState.Corrupt, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.RuntimeOwnershipMismatch, status.ReasonCode);
    }

    [Fact]
    public async Task Recover_RejectsStructurallyInvalidPendingRecord()
    {
        var zip = CreateZip(("runner.exe", ExeBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));
        var installed = await store.InstallAsync(manifest, "account-a");
        var runtimeDirectory = Path.Combine(
            _root,
            "runtime-accounts",
            PathSafety.HashAccountKey("account-a"),
            manifest.RuntimeId);
        File.WriteAllText(
            Path.Combine(runtimeDirectory, "pending.json"),
            JsonSerializer.Serialize(installed.Installation! with
            {
                ExecutableRelativePath = "../escape.exe",
            }));
        File.Delete(Path.Combine(runtimeDirectory, "state.json"));

        var report = await store.RecoverAsync(verifyInstalledHashes: true);
        var status = await store.GetStatusAsync("account-a", manifest.RuntimeId);

        Assert.Equal(0, report.Reconstructed);
        Assert.Equal(1, report.MarkedCorrupt);
        Assert.Equal(RuntimeInstallState.Corrupt, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.RuntimeInstallCorrupt, status.ReasonCode);
    }

    [Fact]
    public async Task Verify_DetectsTamperedDll()
    {
        var zip = CreateZip(
            ("runner.exe", ExeBytes, 0),
            ("runner.dll", DllBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes), ("runner.dll", DllBytes));
        var installed = await store.InstallAsync(manifest, "account-a");
        var library = store.ResolveInstalledFilePath(
            "account-a",
            installed.Installation!,
            "runner.dll");
        await File.WriteAllTextAsync(library!, "tampered");

        var status = await store.VerifyAsync(manifest, "account-a");

        Assert.Equal(RuntimeInstallState.Corrupt, status.State);
        Assert.Equal(LocalRuntimeReasonCodes.SizeMismatch, status.ReasonCode);
    }

    [Fact]
    public async Task StatusAndUninstall_AreAccountScoped()
    {
        var zip = CreateZip(("runner.exe", ExeBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));
        await store.InstallAsync(manifest, "account-a");

        var other = await store.GetStatusAsync("account-b", manifest.RuntimeId);
        var removed = await store.UninstallAsync("account-a", manifest.RuntimeId);

        Assert.Equal(RuntimeInstallState.NotInstalled, other.State);
        Assert.True(removed);
        Assert.Equal(
            RuntimeInstallState.NotInstalled,
            (await store.GetStatusAsync("account-a", manifest.RuntimeId)).State);
    }

    [Fact]
    public async Task Uninstall_RejectsTraversalRuntimeId()
    {
        var zip = CreateZip(("runner.exe", ExeBytes, 0));
        var store = CreateStore(new FakeDownloader(zip));
        var manifest = CreateManifest(zip, ("runner.exe", ExeBytes));
        await store.InstallAsync(manifest, "account-a");
        var sentinel = Path.Combine(_root, "outside-runtime.txt");
        await File.WriteAllTextAsync(sentinel, "keep");

        var removed = await store.UninstallAsync("account-a", "../outside-runtime.txt");

        Assert.False(removed);
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
    }

    private RuntimeInstallStore CreateStore(
        FakeDownloader downloader,
        long freeBytes = 1024L * 1024 * 1024,
        int maxArchiveEntries = 16,
        long maxArchiveEntryBytes = 1024 * 1024,
        long maxExtractedBytes = 4 * 1024 * 1024) =>
        new(
            _root,
            downloader,
            new FakeDiskSpaceProbe(freeBytes),
            stalePartialAge: TimeSpan.FromHours(1),
            stagingMarginBytes: 0,
            maxArchiveEntries: maxArchiveEntries,
            maxArchiveEntryBytes: maxArchiveEntryBytes,
            maxExtractedBytes: maxExtractedBytes);

    private static LocalRuntimeManifest CreateManifest(
        byte[] zip,
        params (string Path, byte[] Bytes)[] files)
    {
        var required = files.Select(file =>
            new LocalRuntimeFileManifest(
                file.Path,
                file.Bytes.Length,
                Hash(file.Bytes))).ToArray();
        return new LocalRuntimeManifest(
            "whisper-cpp-test",
            "Whisper test runtime",
            "runtime.zip",
            "https://example.invalid/runtime.zip",
            "MIT",
            "https://example.invalid/LICENSE",
            "test-revision",
            Hash(zip),
            zip.Length,
            LocalRuntimeArtifactKind.Zip,
            required[0].RelativePath,
            required,
            new LocalRuntimeExecutionProfile(
                new[] { "--mode", "whisper-json", "--input", "{inputPath}" },
                TimeSpan.FromMinutes(2),
                LocalRuntimeExecutionProfile.WhisperCppJson));
    }

    private static byte[] CreateZip(
        params (string Path, byte[] Bytes, int ExternalAttributes)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.NoCompression);
                entry.ExternalAttributes = file.ExternalAttributes;
                using var stream = entry.Open();
                stream.Write(file.Bytes);
            }
        }

        return output.ToArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
