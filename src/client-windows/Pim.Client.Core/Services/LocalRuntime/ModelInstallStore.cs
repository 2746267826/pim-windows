using System.Security.Cryptography;
using System.Text.Json;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

public interface IArtifactDownloader
{
    Task DownloadAsync(
        Uri source,
        string destinationPath,
        long maxBytes,
        IProgress<long>? progress,
        CancellationToken cancellationToken);
}

public interface IDiskSpaceProbe
{
    long GetAvailableFreeBytes(string directoryPath);
}

public sealed class ArtifactSizeLimitExceededException : Exception
{
    public ArtifactSizeLimitExceededException(long limit)
        : base($"artifact exceeded declared size limit of {limit} bytes")
    {
    }
}

public sealed record ModelRecoveryReport(
    int Reconstructed,
    int MarkedCorrupt,
    int StalePartialsDeleted,
    IReadOnlyList<string> Errors);

/// <summary>
/// Account-scoped model store. Every install is explicitly initiated, written to a
/// temporary file, verified against the manifest digest, then moved into place.
/// A crash between move and metadata write is repaired from a pending record.
/// </summary>
public sealed class ModelInstallStore
{
    private const string StateFileName = "state.json";
    private const string PendingFileName = "pending.json";
    private const long DefaultStagingMarginBytes = 64L * 1024 * 1024;

    private readonly string _root;
    private readonly IArtifactDownloader _downloader;
    private readonly IDiskSpaceProbe _disk;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _stalePartialAge;
    private readonly long _stagingMarginBytes;
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly object _gateLock = new();

    public ModelInstallStore(
        string root,
        IArtifactDownloader downloader,
        IDiskSpaceProbe disk,
        Func<DateTimeOffset>? now = null,
        TimeSpan? stalePartialAge = null,
        long stagingMarginBytes = DefaultStagingMarginBytes)
    {
        _root = Path.GetFullPath(root);
        _downloader = downloader;
        _disk = disk;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _stalePartialAge = stalePartialAge ?? TimeSpan.FromHours(24);
        _stagingMarginBytes = Math.Max(0, stagingMarginBytes);
    }

    public string Root => _root;

    public async Task<ModelInstallStatus> InstallAsync(
        LocalModelManifest manifest,
        string accountId,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var normalizedAccount = NormalizeAccount(accountId);
        if (!LocalModelManifest.IsValidModelId(manifest.ModelId))
        {
            return Status(ModelInstallState.Failed, null, "model-id-invalid", manifest.ModelId);
        }

        if (!LocalModelManifest.IsValidArtifactFileName(manifest.ArtifactFileName))
        {
            return Status(ModelInstallState.Failed, null, "model-artifact-invalid", manifest.ModelId);
        }

        var gate = GateFor(normalizedAccount, manifest.ModelId);
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Status(
                ModelInstallState.Cancelled,
                null,
                LocalRuntimeReasonCodes.Cancelled,
                "install cancelled before start");
        }

        try
        {
            return await InstallCoreAsync(manifest, normalizedAccount, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ModelInstallStatus> InstallCoreAsync(
        LocalModelManifest manifest,
        string accountId,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var modelDir = ModelDirectory(accountId, manifest.ModelId);
        var existing = await ReadStatusAsync(accountId, manifest.ModelId, verifyArtifactPresence: true)
            .ConfigureAwait(false);
        if (existing.IsExecutable
            && string.Equals(existing.Installation!.Sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            var existingArtifact = Path.Combine(modelDir, existing.Installation.ArtifactFileName);
            try
            {
                var existingHash = await ComputeSha256Async(existingArtifact, cancellationToken).ConfigureAwait(false);
                if (string.Equals(existingHash, existing.Installation.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through to a fresh download; the old file is replaced atomically.
            }
            catch (OperationCanceledException)
            {
                return Status(
                    ModelInstallState.Cancelled,
                    existing.Installation,
                    LocalRuntimeReasonCodes.Cancelled,
                    "install cancelled before download");
            }
        }

        if (!manifest.HasVerifiedArtifact)
        {
            return await PersistFailureAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.ManifestUnverified,
                    "manifest has no verified digest, size, licence and revision")
                .ConfigureAwait(false);
        }

        if (!Uri.TryCreate(manifest.SourceUrl, UriKind.Absolute, out var source)
            || !string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return await PersistFailureAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.SourceNotHttps,
                    manifest.SourceUrl)
                .ConfigureAwait(false);
        }

        try
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(StagingDirectory);
            var available = _disk.GetAvailableFreeBytes(_root);
            if (available < manifest.SizeBytes + _stagingMarginBytes)
            {
                return await PersistFailureAsync(
                        accountId,
                        manifest,
                        LocalRuntimeReasonCodes.DiskInsufficient,
                        $"required={manifest.SizeBytes + _stagingMarginBytes};available={available}")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await PersistFailureAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.DiskInsufficient,
                    ex.GetType().Name)
                .ConfigureAwait(false);
        }

        var tempPath = Path.Combine(StagingDirectory, $"{manifest.ModelId}-{Guid.NewGuid():N}.part");
        try
        {
            progress?.Report(new ModelInstallProgress(0, manifest.SizeBytes, ModelInstallState.Downloading));
            await _downloader.DownloadAsync(
                    source,
                    tempPath,
                    manifest.SizeBytes,
                    new Progress<long>(received =>
                        progress?.Report(new ModelInstallProgress(
                            received,
                            manifest.SizeBytes,
                            ModelInstallState.Downloading))),
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            var size = new FileInfo(tempPath).Length;
            if (size != manifest.SizeBytes)
            {
                TryDelete(tempPath);
                return await PersistFailureAsync(
                        accountId,
                        manifest,
                        LocalRuntimeReasonCodes.SizeMismatch,
                        $"expected={manifest.SizeBytes};actual={size}")
                    .ConfigureAwait(false);
            }

            progress?.Report(new ModelInstallProgress(size, manifest.SizeBytes, ModelInstallState.Verifying));
            var actualSha = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualSha, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tempPath);
                return await PersistFailureAsync(
                        accountId,
                        manifest,
                        LocalRuntimeReasonCodes.HashMismatch,
                        $"expected={manifest.Sha256};actual={actualSha}")
                    .ConfigureAwait(false);
            }

            Directory.CreateDirectory(modelDir);
            var installedAt = _now();
            var record = new ModelInstallationRecord(
                manifest.ModelId,
                accountId,
                manifest.Revision,
                manifest.Sha256,
                manifest.SizeBytes,
                manifest.ArtifactFileName,
                installedAt);

            // Pending record closes the crash window between the artifact move and state write.
            await WriteJsonAtomicAsync(Path.Combine(modelDir, PendingFileName), record, cancellationToken)
                .ConfigureAwait(false);

            var destination = Path.Combine(modelDir, manifest.ArtifactFileName);
            RemoveOtherArtifacts(modelDir, manifest.ArtifactFileName);
            File.Move(tempPath, destination, overwrite: true);

            var installed = Status(ModelInstallState.Installed, record, null, null);
            await WriteJsonAtomicAsync(Path.Combine(modelDir, StateFileName), installed, cancellationToken)
                .ConfigureAwait(false);
            TryDelete(Path.Combine(modelDir, PendingFileName));
            progress?.Report(new ModelInstallProgress(size, manifest.SizeBytes, ModelInstallState.Installed));
            return installed;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            return await PersistFailureAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.Cancelled,
                    "install cancelled")
                .ConfigureAwait(false);
        }
        catch (ArtifactSizeLimitExceededException ex)
        {
            TryDelete(tempPath);
            return await PersistFailureAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.SizeMismatch,
                    ex.Message)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            TryDelete(tempPath);
            return await PersistFailureAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.DownloadFailed,
                    ex.GetType().Name)
                .ConfigureAwait(false);
        }
    }

    public Task<ModelInstallStatus> GetStatusAsync(string accountId, string modelId)
    {
        var normalizedAccount = NormalizeAccount(accountId);
        return ReadStatusAsync(normalizedAccount, modelId, verifyArtifactPresence: true);
    }

    public async Task<IReadOnlyList<ModelInstallStatus>> ListAsync(string accountId)
    {
        var normalizedAccount = NormalizeAccount(accountId);
        var accountDir = Path.Combine(_root, "accounts", PathSafety.HashAccountKey(normalizedAccount));
        if (!Directory.Exists(accountDir))
        {
            return Array.Empty<ModelInstallStatus>();
        }

        var result = new List<ModelInstallStatus>();
        foreach (var modelDir in Directory.EnumerateDirectories(accountDir))
        {
            var modelId = Path.GetFileName(modelDir);
            if (!LocalModelManifest.IsValidModelId(modelId))
            {
                continue;
            }

            result.Add(await ReadStatusAsync(normalizedAccount, modelId, verifyArtifactPresence: true)
                .ConfigureAwait(false));
        }

        return result
            .OrderBy(status => status.Installation?.ModelId ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ModelInstallStatus> VerifyAsync(
        string accountId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccount = NormalizeAccount(accountId);
        var gate = GateFor(normalizedAccount, modelId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await ReadStatusAsync(normalizedAccount, modelId, verifyArtifactPresence: true)
                .ConfigureAwait(false);
            if (status.Installation is null)
            {
                return status;
            }

            var artifactPath = Path.Combine(ModelDirectory(normalizedAccount, modelId), status.Installation.ArtifactFileName);
            if (!File.Exists(artifactPath))
            {
                return await PersistCorruptAsync(normalizedAccount, status.Installation, LocalRuntimeReasonCodes.ArtifactMissing, artifactPath)
                    .ConfigureAwait(false);
            }

            var actual = await ComputeSha256Async(artifactPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, status.Installation.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return await PersistCorruptAsync(
                        normalizedAccount,
                        status.Installation,
                        LocalRuntimeReasonCodes.HashMismatch,
                        $"expected={status.Installation.Sha256};actual={actual}")
                    .ConfigureAwait(false);
            }

            return status;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> UninstallAsync(string accountId, string modelId)
    {
        var normalizedAccount = NormalizeAccount(accountId);
        if (!LocalModelManifest.IsValidModelId(modelId))
        {
            return false;
        }

        var gate = GateFor(normalizedAccount, modelId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var modelDir = ModelDirectory(normalizedAccount, modelId);
            if (!Directory.Exists(modelDir))
            {
                return false;
            }

            Directory.Delete(modelDir, recursive: true);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Repairs interrupted installs and, optionally, re-verifies installed digests.
    /// Startup should pass false (presence-only) to avoid hashing multi-GB files;
    /// the explicit "校验摘要" action passes true.
    /// </summary>
    public async Task<ModelRecoveryReport> RecoverAsync(
        bool verifyInstalledHashes = false,
        CancellationToken cancellationToken = default)
    {
        var reconstructed = 0;
        var corrupt = 0;
        var stalePartials = 0;
        var errors = new List<string>();

        Directory.CreateDirectory(_root);
        if (Directory.Exists(StagingDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(StagingDirectory, "*.part"))
            {
                try
                {
                    if (_now() - File.GetLastWriteTimeUtc(file) >= _stalePartialAge)
                    {
                        File.Delete(file);
                        stalePartials++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"staging-cleanup-failed: {Path.GetFileName(file)}: {ex.GetType().Name}");
                }
            }
        }

        var accountsDir = Path.Combine(_root, "accounts");
        if (!Directory.Exists(accountsDir))
        {
            return new ModelRecoveryReport(0, 0, stalePartials, errors);
        }

        foreach (var accountDir in Directory.EnumerateDirectories(accountsDir))
        {
            foreach (var modelDir in Directory.EnumerateDirectories(accountDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modelId = Path.GetFileName(modelDir);
                if (!LocalModelManifest.IsValidModelId(modelId))
                {
                    continue;
                }

                foreach (var temp in Directory.EnumerateFiles(modelDir, "*.tmp"))
                {
                    TryDelete(temp);
                }

                var pendingPath = Path.Combine(modelDir, PendingFileName);
                if (File.Exists(pendingPath))
                {
                    var pending = await TryReadJsonAsync<ModelInstallationRecord>(pendingPath).ConfigureAwait(false);
                    if (pending is null)
                    {
                        TryDelete(pendingPath);
                        continue;
                    }

                    var artifact = Path.Combine(modelDir, pending.ArtifactFileName);
                    var repair = await RepairPendingAsync(accountDir, modelDir, pending, artifact, verifyInstalledHashes, cancellationToken)
                        .ConfigureAwait(false);
                    if (repair == ModelInstallState.Installed)
                    {
                        reconstructed++;
                    }
                    else
                    {
                        corrupt++;
                    }

                    TryDelete(pendingPath);
                    continue;
                }

                var statePath = Path.Combine(modelDir, StateFileName);
                var status = await TryReadJsonAsync<ModelInstallStatus>(statePath).ConfigureAwait(false);
                if (status?.Installation is null)
                {
                    continue;
                }

                var artifactPath = Path.Combine(modelDir, status.Installation.ArtifactFileName);
                if (!File.Exists(artifactPath))
                {
                    await PersistCorruptAsync(
                            status.Installation.AccountId,
                            status.Installation,
                            LocalRuntimeReasonCodes.ArtifactMissing,
                            artifactPath)
                        .ConfigureAwait(false);
                    corrupt++;
                    continue;
                }

                if (verifyInstalledHashes)
                {
                    var actual = await ComputeSha256Async(artifactPath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(actual, status.Installation.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        await PersistCorruptAsync(
                                status.Installation.AccountId,
                                status.Installation,
                                LocalRuntimeReasonCodes.HashMismatch,
                                $"expected={status.Installation.Sha256};actual={actual}")
                            .ConfigureAwait(false);
                        corrupt++;
                    }
                }
            }
        }

        return new ModelRecoveryReport(reconstructed, corrupt, stalePartials, errors);
    }

    private async Task<ModelInstallState> RepairPendingAsync(
        string accountDir,
        string modelDir,
        ModelInstallationRecord pending,
        string artifactPath,
        bool verifyHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(artifactPath))
        {
            await PersistCorruptAsync(pending.AccountId, pending, LocalRuntimeReasonCodes.ArtifactMissing, artifactPath)
                .ConfigureAwait(false);
            return ModelInstallState.Corrupt;
        }

        if (verifyHash)
        {
            var actual = await ComputeSha256Async(artifactPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, pending.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await PersistCorruptAsync(
                        pending.AccountId,
                        pending,
                        LocalRuntimeReasonCodes.HashMismatch,
                        $"expected={pending.Sha256};actual={actual}")
                    .ConfigureAwait(false);
                return ModelInstallState.Corrupt;
            }
        }

        var installed = Status(ModelInstallState.Installed, pending, null, null);
        await WriteJsonAtomicAsync(Path.Combine(modelDir, StateFileName), installed, CancellationToken.None)
            .ConfigureAwait(false);
        return ModelInstallState.Installed;
    }

    private async Task<ModelInstallStatus> ReadStatusAsync(
        string accountId,
        string modelId,
        bool verifyArtifactPresence)
    {
        if (!LocalModelManifest.IsValidModelId(modelId))
        {
            return Status(ModelInstallState.Failed, null, "model-id-invalid", modelId);
        }

        var modelDir = ModelDirectory(accountId, modelId);
        var state = await TryReadJsonAsync<ModelInstallStatus>(Path.Combine(modelDir, StateFileName))
            .ConfigureAwait(false);
        if (state is null)
        {
            return new ModelInstallStatus(
                ModelInstallState.NotInstalled,
                null,
                null,
                null,
                _now());
        }

        if (state.Installation is not null
            && !string.Equals(state.Installation.AccountId, accountId, StringComparison.Ordinal))
        {
            return new ModelInstallStatus(
                ModelInstallState.Corrupt,
                state.Installation,
                LocalRuntimeReasonCodes.OwnershipMismatch,
                "installation belongs to another account",
                _now());
        }

        if (verifyArtifactPresence
            && state.State == ModelInstallState.Installed
            && state.Installation is not null)
        {
            var artifact = Path.Combine(modelDir, state.Installation.ArtifactFileName);
            if (!File.Exists(artifact))
            {
                return new ModelInstallStatus(
                    ModelInstallState.Corrupt,
                    state.Installation,
                    LocalRuntimeReasonCodes.ArtifactMissing,
                    artifact,
                    _now());
            }
        }

        return state;
    }

    private async Task<ModelInstallStatus> PersistFailureAsync(
        string accountId,
        LocalModelManifest manifest,
        string reason,
        string detail)
    {
        var existing = await ReadStatusAsync(accountId, manifest.ModelId, verifyArtifactPresence: true)
            .ConfigureAwait(false);
        if (existing.IsExecutable)
        {
            // Keep the last healthy installation usable when a reinstall fails.
            return new ModelInstallStatus(ModelInstallState.Failed, existing.Installation, reason, detail, _now());
        }

        var state = reason == LocalRuntimeReasonCodes.Cancelled
            ? ModelInstallState.Cancelled
            : ModelInstallState.Failed;
        var status = new ModelInstallStatus(state, null, reason, detail, _now());
        var modelDir = ModelDirectory(accountId, manifest.ModelId);
        Directory.CreateDirectory(modelDir);
        await WriteJsonAtomicAsync(Path.Combine(modelDir, StateFileName), status, CancellationToken.None)
            .ConfigureAwait(false);
        return status;
    }

    private async Task<ModelInstallStatus> PersistCorruptAsync(
        string accountId,
        ModelInstallationRecord record,
        string reason,
        string detail)
    {
        var status = new ModelInstallStatus(ModelInstallState.Corrupt, record, reason, detail, _now());
        var modelDir = ModelDirectory(accountId, record.ModelId);
        Directory.CreateDirectory(modelDir);
        await WriteJsonAtomicAsync(Path.Combine(modelDir, StateFileName), status, CancellationToken.None)
            .ConfigureAwait(false);
        return status;
    }

    private ModelInstallStatus Status(
        ModelInstallState state,
        ModelInstallationRecord? record,
        string? reason,
        string? detail) =>
        new(state, record, reason, detail, _now());

    private string StagingDirectory => Path.Combine(_root, ".staging");

    private string ModelDirectory(string accountId, string modelId) =>
        Path.Combine(_root, "accounts", PathSafety.HashAccountKey(accountId), modelId);

    private SemaphoreSlim GateFor(string accountId, string modelId)
    {
        var key = PathSafety.HashAccountKey(accountId) + "/" + modelId;
        lock (_gateLock)
        {
            if (!_gates.TryGetValue(key, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _gates[key] = gate;
            }

            return gate;
        }
    }

    private static string NormalizeAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 256)
        {
            throw new ArgumentException("account id is required", nameof(accountId));
        }

        return accountId.Trim();
    }

    private static void RemoveOtherArtifacts(string modelDir, string keepFileName)
    {
        foreach (var file in Directory.EnumerateFiles(modelDir))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, StateFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, PendingFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only remove files that look like previous model artifacts.
            if (LocalModelManifest.IsValidArtifactFileName(name))
            {
                TryDelete(file);
            }
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private static async Task<T?> TryReadJsonAsync<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
