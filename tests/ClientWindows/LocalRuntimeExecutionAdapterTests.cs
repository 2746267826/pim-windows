using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class LocalRuntimeExecutionAdapterTests : IDisposable
{
    private static readonly byte[] ModelBytes = "synthetic-model"u8.ToArray();
    private static readonly byte[] InputBytes = "synthetic-input-audio"u8.ToArray();
    private static readonly LocalAccountScope Scope =
        new("https://api.example.test", "account-a");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-runtime-exec-" + Guid.NewGuid().ToString("N"));

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
    public async Task Execute_InstalledVerifiedRuntimeAndModel_LaunchesHelperAndParsesWhisperJson()
    {
        var model = CreateModelManifest();
        var runtime = CreateRuntimeManifestFromHelper();
        var modelStore = new ModelInstallStore(
            _root,
            new FakeDownloader(ModelBytes),
            new FakeDiskSpaceProbe());
        var runtimeStore = new RuntimeInstallStore(
            _root,
            new FakeDownloader(runtime.ArchiveBytes),
            new FakeDiskSpaceProbe(),
            stagingMarginBytes: 0);
        Assert.Equal(
            ModelInstallState.Installed,
            (await modelStore.InstallAsync(model.Manifest, "account-a")).State);
        Assert.Equal(
            RuntimeInstallState.Installed,
            (await runtimeStore.InstallAsync(runtime.Manifest, "account-a")).State);
        var input = Path.Combine(_root, "input.wav");
        await File.WriteAllBytesAsync(input, InputBytes);
        var adapter = new LocalRuntimeExecutionAdapter(
            runtimeStore,
            modelStore,
            new ControlledProcessRunner(),
            new LocalRuntimeAccountGuard());

        var result = await adapter.ExecuteAsync(
            runtime.Manifest,
            model.Manifest,
            new LocalSpeechExecutionRequest(Scope, input, "en"));

        Assert.True(result.Succeeded, result.ErrorCode + " " + result.Detail);
        Assert.Equal("synthetic transcript", result.Text);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(TimeSpan.Zero, segment.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), segment.End);
        Assert.Equal("synthetic transcript", segment.Text);
        Assert.Equal(0, result.Process?.ExitCode);
    }

    [Fact]
    public async Task Execute_TamperedRuntimeDll_IsRejectedBeforeLaunch()
    {
        var model = CreateModelManifest();
        var runtime = CreateRuntimeManifestFromHelper();
        var modelStore = new ModelInstallStore(
            _root,
            new FakeDownloader(ModelBytes),
            new FakeDiskSpaceProbe());
        var runtimeStore = new RuntimeInstallStore(
            _root,
            new FakeDownloader(runtime.ArchiveBytes),
            new FakeDiskSpaceProbe(),
            stagingMarginBytes: 0);
        await modelStore.InstallAsync(model.Manifest, "account-a");
        var installed = await runtimeStore.InstallAsync(runtime.Manifest, "account-a");
        var dll = runtime.Manifest.RequiredFiles.First(file =>
            file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        var dllPath = runtimeStore.ResolveInstalledFilePath(
            "account-a",
            installed.Installation!,
            dll.RelativePath);
        await File.WriteAllTextAsync(dllPath!, "tampered");
        var input = Path.Combine(_root, "input.wav");
        await File.WriteAllBytesAsync(input, InputBytes);
        var adapter = new LocalRuntimeExecutionAdapter(
            runtimeStore,
            modelStore,
            new ControlledProcessRunner(),
            new LocalRuntimeAccountGuard());

        var result = await adapter.ExecuteAsync(
            runtime.Manifest,
            model.Manifest,
            new LocalSpeechExecutionRequest(Scope, input, "en"));

        Assert.False(result.Succeeded);
        Assert.Equal(LocalRuntimeReasonCodes.RuntimeNotInstalled, result.ErrorCode);
        Assert.Null(result.Process);
    }

    [Fact]
    public async Task Execute_MissingInput_IsRejectedBeforeStartingProcess()
    {
        var adapter = new LocalRuntimeExecutionAdapter(
            null!,
            null!,
            null!,
            new LocalRuntimeAccountGuard());
        var model = CreateModelManifest();
        var runtime = CreateRuntimeManifestFromHelper();

        var result = await adapter.ExecuteAsync(
            runtime.Manifest,
            model.Manifest,
            new LocalSpeechExecutionRequest(
                Scope,
                Path.Combine(_root, "missing.wav"),
                "en"));

        Assert.False(result.Succeeded);
        Assert.Equal(LocalRuntimeReasonCodes.InputMissing, result.ErrorCode);
    }

    [Fact]
    public async Task Execute_WithoutExplicitAccountScope_FailsClosed()
    {
        var input = Path.Combine(_root, "input.wav");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(input, InputBytes);
        var adapter = new LocalRuntimeExecutionAdapter(
            null!,
            null!,
            null!,
            new LocalRuntimeAccountGuard());

        var result = await adapter.ExecuteAsync(
            CreateRuntimeManifestFromHelper().Manifest,
            CreateModelManifest().Manifest,
            new LocalSpeechExecutionRequest("account-a", input, "en"));

        Assert.False(result.Succeeded);
        Assert.Equal(LocalRuntimeReasonCodes.AccountRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Execute_ScopeOwnerMismatch_FailsBeforeRuntimeAccess()
    {
        var input = Path.Combine(_root, "input.wav");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(input, InputBytes);
        var adapter = new LocalRuntimeExecutionAdapter(
            null!,
            null!,
            null!,
            new LocalRuntimeAccountGuard());

        var result = await adapter.ExecuteAsync(
            CreateRuntimeManifestFromHelper().Manifest,
            CreateModelManifest().Manifest,
            new LocalSpeechExecutionRequest(
                "account-a",
                input,
                "en",
                new LocalAccountScope("https://api.example.test", "account-b")));

        Assert.False(result.Succeeded);
        Assert.Equal(LocalRuntimeReasonCodes.OwnershipMismatch, result.ErrorCode);
    }

    [Theory]
    [InlineData("whisper-array")]
    [InlineData("whisper-null")]
    [InlineData("whisper-primitive")]
    public async Task Execute_WhisperRootMustBeObject_FailsClosed(string mode)
    {
        var result = await ExecuteHelperModeAsync(new[] { "--mode", mode });

        Assert.False(result.Succeeded);
        Assert.Equal(LocalRuntimeReasonCodes.OutputParseFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Execute_WhisperHugeOffset_FailsClosed()
    {
        var result = await ExecuteHelperModeAsync(new[] { "--mode", "whisper-huge-offset" });

        Assert.False(result.Succeeded);
        Assert.Equal(LocalRuntimeReasonCodes.OutputParseFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Execute_WhisperSegmentsWithNoText_FailsClosed()
    {
        var result = await ExecuteHelperModeAsync(new[] { "--mode", "whisper-empty-segments" });

        Assert.False(result.Succeeded);
        Assert.Equal(LocalRuntimeReasonCodes.OutputParseFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Execute_WhisperEmptyTranscription_FailsClosed()
    {
        var result = await ExecuteHelperModeAsync(
            new[] { "--mode", "whisper-empty-transcription" });

        Assert.False(result.Succeeded);
        Assert.Equal(LocalRuntimeReasonCodes.OutputParseFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Execute_WhisperEmptyTopText_UsesNonEmptySegmentText()
    {
        var result = await ExecuteHelperModeAsync(
            new[] { "--mode", "whisper-empty-top-text" });

        Assert.True(result.Succeeded, result.ErrorCode + " " + result.Detail);
        Assert.Equal("segment transcript", result.Text);
        Assert.Single(result.Segments);
    }

    [Fact]
    public async Task Execute_PlainTextTruncatedOutput_IsNeverSuccessful()
    {
        var result = await ExecuteHelperModeAsync(
            new[] { "--mode", "flood", "--bytes", (4 * 1024 * 1024 + 1).ToString() },
            LocalRuntimeExecutionProfile.PlainText);

        Assert.False(result.Succeeded);
        Assert.Equal(LocalRuntimeReasonCodes.OutputTruncated, result.ErrorCode);
        Assert.True(result.Process?.OutputTruncated);
    }

    [Theory]
    [InlineData(LocalRuntimeExecutionProfile.PlainText)]
    [InlineData(LocalRuntimeExecutionProfile.WhisperCppJson)]
    public async Task Execute_TruncatedFlagIsTerminalEvenIfRunnerClaimsSuccess(
        string outputFormat)
    {
        var model = CreateModelManifest();
        var runtime = CreateRuntimeManifestFromHelper(outputFormat: outputFormat);
        var modelStore = new ModelInstallStore(
            _root,
            new FakeDownloader(ModelBytes),
            new FakeDiskSpaceProbe());
        var runtimeStore = new RuntimeInstallStore(
            _root,
            new FakeDownloader(runtime.ArchiveBytes),
            new FakeDiskSpaceProbe(),
            stagingMarginBytes: 0);
        await modelStore.InstallAsync(model.Manifest, "account-a");
        await runtimeStore.InstallAsync(runtime.Manifest, "account-a");
        var input = Path.Combine(_root, "input.wav");
        await File.WriteAllBytesAsync(input, InputBytes);
        var adapter = new LocalRuntimeExecutionAdapter(
            runtimeStore,
            modelStore,
            new StubProcessRunner(new RuntimeLaunchResult(
                Succeeded: true,
                ExitCode: 0,
                Cancelled: false,
                TimedOut: false,
                OutputTruncated: true,
                StandardOutput: "truncated text",
                StandardError: string.Empty,
                ErrorCode: null,
                Duration: TimeSpan.Zero)),
            new LocalRuntimeAccountGuard());

        var result = await adapter.ExecuteAsync(
            runtime.Manifest,
            model.Manifest,
            new LocalSpeechExecutionRequest(Scope, input, "en"));

        Assert.False(result.Succeeded);
        Assert.Equal(LocalRuntimeReasonCodes.OutputTruncated, result.ErrorCode);
        Assert.True(result.Process?.OutputTruncated);
        Assert.Empty(result.Text);
        Assert.Empty(result.Segments);
    }

    private async Task<LocalSpeechExecutionResult> ExecuteHelperModeAsync(
        IReadOnlyList<string> arguments,
        string outputFormat = LocalRuntimeExecutionProfile.WhisperCppJson)
    {
        var model = CreateModelManifest();
        var runtime = CreateRuntimeManifestFromHelper(arguments, outputFormat);
        var modelStore = new ModelInstallStore(
            _root,
            new FakeDownloader(ModelBytes),
            new FakeDiskSpaceProbe());
        var runtimeStore = new RuntimeInstallStore(
            _root,
            new FakeDownloader(runtime.ArchiveBytes),
            new FakeDiskSpaceProbe(),
            stagingMarginBytes: 0);
        await modelStore.InstallAsync(model.Manifest, "account-a");
        await runtimeStore.InstallAsync(runtime.Manifest, "account-a");
        var input = Path.Combine(_root, "input.wav");
        await File.WriteAllBytesAsync(input, InputBytes);
        var adapter = new LocalRuntimeExecutionAdapter(
            runtimeStore,
            modelStore,
            new ControlledProcessRunner(),
            new LocalRuntimeAccountGuard());

        return await adapter.ExecuteAsync(
            runtime.Manifest,
            model.Manifest,
            new LocalSpeechExecutionRequest(Scope, input, "en"));
    }

    private static (LocalModelManifest Manifest, byte[] Bytes) CreateModelManifest() =>
        (new LocalModelManifest(
            "whisper-test-model",
            "Whisper test model",
            "whisper-test-runtime",
            "MIT",
            "https://example.invalid/LICENSE",
            "https://example.invalid/model.bin",
            "model-revision",
            Sha256(ModelBytes),
            ModelBytes.Length,
            "model.bin",
            new LocalResourceRequirements(CpuCores: 1, MemoryBytes: 1024)),
        ModelBytes);

    private static (LocalRuntimeManifest Manifest, byte[] ArchiveBytes) CreateRuntimeManifestFromHelper(
        IReadOnlyList<string>? arguments = null,
        string outputFormat = LocalRuntimeExecutionProfile.WhisperCppJson)
    {
        var helperDirectory = Path.Combine(AppContext.BaseDirectory, "helper");
        var executablePath = Path.Combine(helperDirectory, "Pim.LocalRuntime.Helper.exe");
        Assert.True(File.Exists(executablePath), "runtime helper was not copied to the test output");
        var files = Directory.EnumerateFiles(helperDirectory)
            .Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.NoCompression);
                using var input = File.OpenRead(file);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }

        var bytes = archiveBytes.ToArray();
        var required = files.Select(file =>
            new LocalRuntimeFileManifest(
                Path.GetFileName(file),
                new FileInfo(file).Length,
                Sha256(File.ReadAllBytes(file)))).ToArray();
        return (new LocalRuntimeManifest(
            "whisper-test-runtime",
            "Whisper synthetic helper runtime",
            "runtime.zip",
            "https://example.invalid/runtime.zip",
            "MIT",
            "https://example.invalid/LICENSE",
            "runtime-revision",
            Sha256(bytes),
            bytes.Length,
            LocalRuntimeArtifactKind.Zip,
            "Pim.LocalRuntime.Helper.exe",
            required,
            new LocalRuntimeExecutionProfile(
                arguments ?? new[]
                {
                    "--mode", "whisper-json",
                    "--model", "{modelPath}",
                    "--input", "{inputPath}",
                    "--language", "{language}",
                },
                TimeSpan.FromSeconds(30),
                outputFormat)), bytes);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FakeDownloader : IArtifactDownloader
    {
        private readonly byte[] _bytes;

        public FakeDownloader(byte[] bytes)
        {
            _bytes = bytes;
        }

        public Task DownloadAsync(
            Uri source,
            string destinationPath,
            long maxBytes,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, _bytes);
            progress?.Report(_bytes.Length);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
    {
        public long GetAvailableFreeBytes(string directoryPath) => 1024L * 1024 * 1024;
    }

    private sealed class StubProcessRunner : IControlledProcessRunner
    {
        private readonly RuntimeLaunchResult _result;

        public StubProcessRunner(RuntimeLaunchResult result)
        {
            _result = result;
        }

        public Task<RuntimeLaunchResult> StartAsync(
            RuntimeLaunchSpec spec,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }
}
