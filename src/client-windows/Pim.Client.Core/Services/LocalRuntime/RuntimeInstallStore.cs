using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

public sealed record RuntimeRecoveryReport(
    int Reconstructed,
    int MarkedCorrupt,
    int StalePartialsDeleted,
    IReadOnlyList<string> Errors);

/// <summary>
/// Account-scoped runtime installer. The downloaded archive, extracted file set and
/// every launchable file are pinned by the catalog. Archives are extracted into a
/// staging directory with a closed-world file list before the version is published.
/// </summary>
public sealed class RuntimeInstallStore
{
    private const string StateFileName = "state.json";
    private const string PendingFileName = "pending.json";
    private const long DefaultStagingMarginBytes = 64L * 1024 * 1024;
    private const int DefaultMaxArchiveEntries = 512;
    private const long DefaultMaxArchiveEntryBytes = 512L * 1024 * 1024;
    private const long DefaultMaxExtractedBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _root;
    private readonly IArtifactDownloader _downloader;
    private readonly IDiskSpaceProbe _disk;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _stalePartialAge;
    private readonly long _stagingMarginBytes;
    private readonly int _maxArchiveEntries;
    private readonly long _maxArchiveEntryBytes;
    private readonly long _maxExtractedBytes;
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly object _gateLock = new();

    public RuntimeInstallStore(
        string root,
        IArtifactDownloader downloader,
        IDiskSpaceProbe disk,
        Func<DateTimeOffset>? now = null,
        TimeSpan? stalePartialAge = null,
        long stagingMarginBytes = DefaultStagingMarginBytes,
        int maxArchiveEntries = DefaultMaxArchiveEntries,
        long maxArchiveEntryBytes = DefaultMaxArchiveEntryBytes,
        long maxExtractedBytes = DefaultMaxExtractedBytes)
    {
        _root = Path.GetFullPath(root);
        _downloader = downloader;
        _disk = disk;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _stalePartialAge = stalePartialAge ?? TimeSpan.FromHours(24);
        _stagingMarginBytes = Math.Max(0, stagingMarginBytes);
        _maxArchiveEntries = Math.Max(1, maxArchiveEntries);
        _maxArchiveEntryBytes = Math.Max(1, maxArchiveEntryBytes);
        _maxExtractedBytes = Math.Max(1, maxExtractedBytes);
    }

    public string Root => _root;

    public async Task<RuntimeInstallStatus> InstallAsync(
        LocalRuntimeManifest manifest,
        string accountId,
        IProgress<RuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var normalizedAccount = NormalizeAccount(accountId);
        if (!LocalModelManifest.IsValidModelId(manifest.RuntimeId))
        {
            return Status(RuntimeInstallState.Failed, null, "runtime-id-invalid", manifest.RuntimeId);
        }

        var gate = GateFor(normalizedAccount, manifest.RuntimeId);
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Status(
                RuntimeInstallState.Cancelled,
                null,
                LocalRuntimeReasonCodes.Cancelled,
                "runtime install cancelled before start");
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

    public Task<RuntimeInstallStatus> GetStatusAsync(string accountId, string runtimeId)
    {
        var normalizedAccount = NormalizeAccount(accountId);
        return ReadStatusAsync(normalizedAccount, runtimeId, verifyPresence: true);
    }

    public string? ResolveInstalledFilePath(
        string accountId,
        RuntimeInstallationRecord installation,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var normalizedAccount = NormalizeAccount(accountId);
        if (!string.Equals(
                installation.AccountId,
                normalizedAccount,
                StringComparison.Ordinal)
            || !string.Equals(
                installation.RuntimeId,
                installation.RuntimeId.Trim(),
                StringComparison.Ordinal)
            || !LocalModelManifest.IsValidModelId(installation.RuntimeId)
            || !IsSafeVersionName(installation.VersionDirectoryName)
            || !LocalRuntimeManifest.IsValidRelativePath(relativePath)
            || !installation.RequiredFiles.Any(file =>
                string.Equals(
                    file.RelativePath,
                    relativePath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var versionsRoot = Path.Combine(
            RuntimeDirectory(normalizedAccount, installation.RuntimeId),
            "versions");
        var versionRoot = PathSafety.ResolveContained(
            versionsRoot,
            installation.VersionDirectoryName);
        return versionRoot is null
            ? null
            : PathSafety.ResolveContained(
                versionRoot,
                LocalRuntimeManifest.NormalizeRelativePath(relativePath));
    }

    public async Task<RuntimeInstallStatus> VerifyAsync(
        LocalRuntimeManifest manifest,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var normalizedAccount = NormalizeAccount(accountId);
        var gate = GateFor(normalizedAccount, manifest.RuntimeId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await ReadStatusAsync(
                    normalizedAccount,
                    manifest.RuntimeId,
                    verifyPresence: true)
                .ConfigureAwait(false);
            if (!status.IsExecutable)
            {
                return status;
            }

            var verification = await VerifyInstalledFilesAsync(
                    normalizedAccount,
                    status.Installation!,
                    manifest,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!verification.Ok)
            {
                return await PersistCorruptAsync(
                        normalizedAccount,
                        status.Installation!,
                        verification.Reason!,
                        verification.Detail!)
                    .ConfigureAwait(false);
            }

            return status;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> UninstallAsync(string accountId, string runtimeId)
    {
        var normalizedAccount = NormalizeAccount(accountId);
        if (!LocalModelManifest.IsValidModelId(runtimeId))
        {
            return false;
        }

        var gate = GateFor(normalizedAccount, runtimeId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = ResolveOwnedRuntimeDirectory(normalizedAccount, runtimeId);
            if (directory is null)
            {
                return false;
            }

            if (!Directory.Exists(directory))
            {
                return false;
            }

            if (!IsOwnedPathSafeForRecursiveDelete(directory))
            {
                return false;
            }

            Directory.Delete(directory, recursive: true);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RuntimeRecoveryReport> RecoverAsync(
        bool verifyInstalledHashes = false,
        CancellationToken cancellationToken = default)
    {
        var reconstructed = 0;
        var corrupt = 0;
        var stale = 0;
        var errors = new List<string>();
        Directory.CreateDirectory(_root);

        var accounts = RuntimeAccountsRoot;
        if (!Directory.Exists(accounts))
        {
            return new RuntimeRecoveryReport(0, 0, stale, errors);
        }

        foreach (var accountDir in Directory.EnumerateDirectories(accounts))
        {
            stale += CleanStaleStaging(Path.Combine(accountDir, ".staging"), errors);
            foreach (var runtimeDir in Directory.EnumerateDirectories(accountDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var runtimeId = Path.GetFileName(runtimeDir);
                if (!LocalModelManifest.IsValidModelId(runtimeId))
                {
                    continue;
                }

                var pendingPath = Path.Combine(runtimeDir, PendingFileName);
                if (File.Exists(pendingPath))
                {
                    var pending = await TryReadJsonAsync<RuntimeInstallationRecord>(pendingPath)
                        .ConfigureAwait(false);
                    if (pending is null)
                    {
                        TryDelete(pendingPath);
                        continue;
                    }

                    var pendingAccount = NormalizeAccount(
                        pending.AccountId,
                        throwOnInvalid: false);
                    var expectedAccountKey = Path.GetFileName(accountDir);
                    if (!string.Equals(
                            PathSafety.HashAccountKey(pendingAccount),
                            expectedAccountKey,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            pending.RuntimeId,
                            runtimeId,
                            StringComparison.Ordinal))
                    {
                        await PersistCorruptAtAsync(
                                runtimeDir,
                                pending,
                                LocalRuntimeReasonCodes.RuntimeOwnershipMismatch,
                                "pending runtime does not belong to this account/runtime directory")
                            .ConfigureAwait(false);
                        corrupt++;
                        TryDelete(pendingPath);
                        continue;
                    }

                    var pendingVerification = await VerifyRecordFilesAsync(
                            pending,
                            verifyInstalledHashes,
                            manifest: null,
                            pendingAccount,
                            runtimeId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (pendingVerification.Ok)
                    {
                        await WriteJsonAtomicAsync(
                                Path.Combine(runtimeDir, StateFileName),
                                new RuntimeInstallStatus(
                                    RuntimeInstallState.Installed,
                                    pending,
                                    null,
                                    null,
                                    _now()),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        reconstructed++;
                    }
                    else
                    {
                        await PersistCorruptAtAsync(
                                runtimeDir,
                                pending,
                                pendingVerification.Reason!,
                                pendingVerification.Detail!)
                            .ConfigureAwait(false);
                        corrupt++;
                    }

                    TryDelete(pendingPath);
                    continue;
                }

                var state = await TryReadJsonAsync<RuntimeInstallStatus>(
                        Path.Combine(runtimeDir, StateFileName))
                    .ConfigureAwait(false);
                if (state?.Installation is null)
                {
                    continue;
                }

                var actual = NormalizeAccount(state.Installation.AccountId, throwOnInvalid: false);
                var expected = Path.GetFileName(accountDir);
                if (!string.Equals(
                        PathSafety.HashAccountKey(actual),
                        expected,
                        StringComparison.Ordinal))
                {
                    await PersistCorruptAtAsync(
                            runtimeDir,
                            state.Installation,
                            LocalRuntimeReasonCodes.RuntimeOwnershipMismatch,
                            "runtime installation belongs to another account")
                        .ConfigureAwait(false);
                    corrupt++;
                    continue;
                }

                var verification = await VerifyRecordFilesAsync(
                        state.Installation,
                        verifyInstalledHashes,
                        manifest: null,
                        actual,
                        runtimeId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!verification.Ok)
                {
                    await PersistCorruptAtAsync(
                            runtimeDir,
                            state.Installation,
                            verification.Reason!,
                            verification.Detail!)
                        .ConfigureAwait(false);
                    corrupt++;
                }
            }
        }

        return new RuntimeRecoveryReport(reconstructed, corrupt, stale, errors);
    }

    private async Task<RuntimeInstallStatus> InstallCoreAsync(
        LocalRuntimeManifest manifest,
        string accountId,
        IProgress<RuntimeInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existing = await ReadStatusAsync(accountId, manifest.RuntimeId, verifyPresence: true)
            .ConfigureAwait(false);
        if (existing.IsExecutable
            && string.Equals(existing.Installation!.Sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            var verification = await VerifyInstalledFilesAsync(
                    accountId,
                    existing.Installation,
                    manifest,
                    cancellationToken)
                .ConfigureAwait(false);
            if (verification.Ok)
            {
                return existing;
            }
        }

        if (!manifest.HasVerifiedRuntime)
        {
            return await PersistFailureAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.RuntimeManifestUnverified,
                    "runtime archive, file set, licence and revision must all be verified")
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

        long extractedBytes;
        try
        {
            extractedBytes = manifest.RequiredFiles.Aggregate(
                0L,
                (total, file) => checked(total + file.SizeBytes));
        }
        catch (OverflowException)
        {
            return await PersistFailureAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.ArchiveLimitExceeded,
                    "declared extracted size overflow")
                .ConfigureAwait(false);
        }

        if (extractedBytes > _maxExtractedBytes)
        {
            return await PersistFailureAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.ArchiveLimitExceeded,
                    $"declared={extractedBytes};limit={_maxExtractedBytes}")
                .ConfigureAwait(false);
        }

        try
        {
            Directory.CreateDirectory(_root);
            var stagingDirectory = StagingDirectoryFor(accountId);
            Directory.CreateDirectory(stagingDirectory);
            var requiredFree = checked(manifest.SizeBytes + extractedBytes + _stagingMarginBytes);
            var available = _disk.GetAvailableFreeBytes(_root);
            if (available < requiredFree)
            {
                return await PersistFailureAsync(
                        accountId,
                        manifest,
                        LocalRuntimeReasonCodes.DiskInsufficient,
                        $"required={requiredFree};available={available}")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return await PersistFailureAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.DiskInsufficient,
                    ex.GetType().Name)
                .ConfigureAwait(false);
        }

        var archivePath = Path.Combine(
            StagingDirectoryFor(accountId),
            $"{manifest.RuntimeId}-{Guid.NewGuid():N}.part");
        var extractDirectory = Path.Combine(
            StagingDirectoryFor(accountId),
            $"{manifest.RuntimeId}-{Guid.NewGuid():N}.extract");
        try
        {
            progress?.Report(new RuntimeInstallProgress(
                0,
                manifest.SizeBytes,
                RuntimeInstallState.Downloading));
            await _downloader.DownloadAsync(
                    source,
                    archivePath,
                    manifest.SizeBytes,
                    new Progress<long>(received =>
                        progress?.Report(new RuntimeInstallProgress(
                            received,
                            manifest.SizeBytes,
                            RuntimeInstallState.Downloading))),
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            var actualSize = new FileInfo(archivePath).Length;
            if (actualSize != manifest.SizeBytes)
            {
                return await FailAndCleanAsync(
                        accountId,
                        manifest,
                        LocalRuntimeReasonCodes.SizeMismatch,
                        $"expected={manifest.SizeBytes};actual={actualSize}",
                        archivePath,
                        extractDirectory)
                    .ConfigureAwait(false);
            }

            progress?.Report(new RuntimeInstallProgress(
                actualSize,
                manifest.SizeBytes,
                RuntimeInstallState.Verifying));
            var archiveHash = await ModelInstallStore
                .ComputeSha256Async(archivePath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(archiveHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return await FailAndCleanAsync(
                        accountId,
                        manifest,
                        LocalRuntimeReasonCodes.HashMismatch,
                        $"expected={manifest.Sha256};actual={archiveHash}",
                        archivePath,
                        extractDirectory)
                    .ConfigureAwait(false);
            }

            Directory.CreateDirectory(extractDirectory);
            progress?.Report(new RuntimeInstallProgress(
                actualSize,
                manifest.SizeBytes,
                RuntimeInstallState.Extracting));
            if (manifest.ArtifactKind == LocalRuntimeArtifactKind.Zip)
            {
                ExtractClosedWorldZip(manifest, archivePath, extractDirectory, cancellationToken);
            }
            else
            {
                StageSingleFileRuntime(manifest, archivePath, extractDirectory, cancellationToken);
            }

            progress?.Report(new RuntimeInstallProgress(
                actualSize,
                manifest.SizeBytes,
                RuntimeInstallState.Verifying));
            var verified = await VerifyDirectoryFilesAsync(
                    extractDirectory,
                    manifest.RequiredFiles,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!verified.Ok)
            {
                return await FailAndCleanAsync(
                        accountId,
                        manifest,
                        verified.Reason!,
                        verified.Detail!,
                        archivePath,
                        extractDirectory)
                    .ConfigureAwait(false);
            }

            var versionName = manifest.Sha256.ToLowerInvariant();
            var versionsDirectory = Path.Combine(
                RuntimeDirectory(accountId, manifest.RuntimeId),
                "versions");
            Directory.CreateDirectory(versionsDirectory);
            var versionDirectory = Path.Combine(versionsDirectory, versionName);
            var record = new RuntimeInstallationRecord(
                manifest.RuntimeId,
                accountId,
                manifest.Revision,
                manifest.Sha256,
                manifest.SizeBytes,
                manifest.ArtifactFileName,
                manifest.ArtifactKind,
                versionName,
                manifest.ExecutableRelativePath,
                manifest.RequiredFiles.ToArray(),
                _now());
            var runtimeDirectory = RuntimeDirectory(accountId, manifest.RuntimeId);
            await WriteJsonAtomicAsync(
                    Path.Combine(runtimeDirectory, PendingFileName),
                    record,
                    cancellationToken)
                .ConfigureAwait(false);

            if (Directory.Exists(versionDirectory))
            {
                var existingVersion = await VerifyDirectoryFilesAsync(
                        versionDirectory,
                        manifest.RequiredFiles,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (existingVersion.Ok)
                {
                    Directory.Delete(extractDirectory, recursive: true);
                }
                else
                {
                    Directory.Delete(versionDirectory, recursive: true);
                    Directory.Move(extractDirectory, versionDirectory);
                }
            }
            else
            {
                Directory.Move(extractDirectory, versionDirectory);
            }

            var installed = Status(RuntimeInstallState.Installed, record, null, null);
            await WriteJsonAtomicAsync(
                    Path.Combine(runtimeDirectory, StateFileName),
                    installed,
                    cancellationToken)
                .ConfigureAwait(false);
            TryDelete(Path.Combine(runtimeDirectory, PendingFileName));
            RemoveUnusedVersions(versionsDirectory, versionName);
            TryDelete(archivePath);
            progress?.Report(new RuntimeInstallProgress(
                actualSize,
                manifest.SizeBytes,
                RuntimeInstallState.Installed));
            return installed;
        }
        catch (OperationCanceledException)
        {
            return await FailAndCleanAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.Cancelled,
                    "runtime install cancelled",
                    archivePath,
                    extractDirectory)
                .ConfigureAwait(false);
        }
        catch (ArtifactSizeLimitExceededException ex)
        {
            return await FailAndCleanAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.SizeMismatch,
                    ex.Message,
                    archivePath,
                    extractDirectory)
                .ConfigureAwait(false);
        }
        catch (UnsafeArchiveException ex)
        {
            return await FailAndCleanAsync(
                    accountId,
                    manifest,
                    ex.ReasonCode,
                    ex.Message,
                    archivePath,
                    extractDirectory)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            return await FailAndCleanAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.ArchiveInvalid,
                    ex.Message,
                    archivePath,
                    extractDirectory)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            return await FailAndCleanAsync(
                    accountId,
                    manifest,
                    LocalRuntimeReasonCodes.DownloadFailed,
                    ex.GetType().Name,
                    archivePath,
                    extractDirectory)
                .ConfigureAwait(false);
        }
    }

    private async Task<RuntimeInstallStatus> FailAndCleanAsync(
        string accountId,
        LocalRuntimeManifest manifest,
        string reason,
        string detail,
        string archivePath,
        string extractDirectory)
    {
        TryDelete(archivePath);
        TryDeleteDirectory(extractDirectory);
        return await PersistFailureAsync(accountId, manifest, reason, detail).ConfigureAwait(false);
    }

    private void ExtractClosedWorldZip(
        LocalRuntimeManifest manifest,
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        var expectedPaths = manifest.RequiredFiles
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = 0;
        long extracted = 0;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries++;
            if (entries > _maxArchiveEntries)
            {
                throw new UnsafeArchiveException(
                    LocalRuntimeReasonCodes.ArchiveLimitExceeded,
                    $"archive entry count exceeds {_maxArchiveEntries}");
            }

            if (IsLinkOrReparsePoint(entry))
            {
                throw new UnsafeArchiveException(
                    LocalRuntimeReasonCodes.ArchiveEntryUnsafe,
                    $"link or reparse entry rejected: {entry.FullName}");
            }

            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal)
                              || entry.FullName.EndsWith("\\", StringComparison.Ordinal);
            if (isDirectory)
            {
                continue;
            }

            if (!LocalRuntimeManifest.IsValidRelativePath(entry.FullName))
            {
                throw new UnsafeArchiveException(
                    LocalRuntimeReasonCodes.ArchiveEntryUnsafe,
                    $"unsafe archive path: {entry.FullName}");
            }

            if (!expectedPaths.Contains(entry.FullName))
            {
                throw new UnsafeArchiveException(
                    LocalRuntimeReasonCodes.ArchiveEntryUnsafe,
                    $"archive contains an undeclared file: {entry.FullName}");
            }

            if (!seen.Add(entry.FullName))
            {
                throw new UnsafeArchiveException(
                    LocalRuntimeReasonCodes.ArchiveEntryUnsafe,
                    $"duplicate archive path: {entry.FullName}");
            }

            if (entry.Length <= 0 || entry.Length > _maxArchiveEntryBytes)
            {
                throw new UnsafeArchiveException(
                    LocalRuntimeReasonCodes.ArchiveLimitExceeded,
                    $"entry size rejected: {entry.FullName}:{entry.Length}");
            }

            try
            {
                extracted = checked(extracted + entry.Length);
            }
            catch (OverflowException)
            {
                throw new UnsafeArchiveException(
                    LocalRuntimeReasonCodes.ArchiveLimitExceeded,
                    "extracted size overflow");
            }

            if (extracted > _maxExtractedBytes)
            {
                throw new UnsafeArchiveException(
                    LocalRuntimeReasonCodes.ArchiveLimitExceeded,
                    $"extracted bytes exceeds {_maxExtractedBytes}");
            }

            var outputPath = PathSafety.ResolveContained(destination, entry.FullName);
            if (outputPath is null)
            {
                throw new UnsafeArchiveException(
                    LocalRuntimeReasonCodes.ArchiveEntryUnsafe,
                    $"archive path escapes destination: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var input = entry.Open();
            using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: false);
            var buffer = new byte[1024 * 1024];
            var written = 0L;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                written += read;
                if (written > entry.Length || written > _maxArchiveEntryBytes)
                {
                    throw new UnsafeArchiveException(
                        LocalRuntimeReasonCodes.ArchiveLimitExceeded,
                        $"entry stream exceeded declared size: {entry.FullName}");
                }

                output.Write(buffer, 0, read);
            }

            if (written != entry.Length)
            {
                throw new UnsafeArchiveException(
                    LocalRuntimeReasonCodes.ArchiveInvalid,
                    $"entry stream length mismatch: {entry.FullName}");
            }

            if (File.GetAttributes(outputPath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnsafeArchiveException(
                    LocalRuntimeReasonCodes.ArchiveEntryUnsafe,
                    $"extracted reparse point rejected: {entry.FullName}");
            }
        }

        var missing = expectedPaths.Except(seen, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
        {
            throw new UnsafeArchiveException(
                LocalRuntimeReasonCodes.RuntimeFileMissing,
                "archive omitted required files: " + string.Join(",", missing));
        }
    }

    private static bool IsLinkOrReparsePoint(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        return (unixMode & 0xF000) == 0xA000
               || windowsAttributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static void StageSingleFileRuntime(
        LocalRuntimeManifest manifest,
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outputPath = PathSafety.ResolveContained(
            destination,
            manifest.ExecutableRelativePath);
        if (outputPath is null)
        {
            throw new UnsafeArchiveException(
                LocalRuntimeReasonCodes.ArchiveEntryUnsafe,
                "runtime executable path escapes destination");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.Copy(archivePath, outputPath, overwrite: false);
    }

    private async Task<RuntimeInstallStatus> PersistFailureAsync(
        string accountId,
        LocalRuntimeManifest manifest,
        string reason,
        string detail)
    {
        var existing = await ReadStatusAsync(
                accountId,
                manifest.RuntimeId,
                verifyPresence: true)
            .ConfigureAwait(false);
        if (existing.IsExecutable)
        {
            return new RuntimeInstallStatus(
                RuntimeInstallState.Failed,
                existing.Installation,
                reason,
                detail,
                _now());
        }

        var state = reason == LocalRuntimeReasonCodes.Cancelled
            ? RuntimeInstallState.Cancelled
            : RuntimeInstallState.Failed;
        var status = new RuntimeInstallStatus(state, null, reason, detail, _now());
        var directory = RuntimeDirectory(accountId, manifest.RuntimeId);
        Directory.CreateDirectory(directory);
        await WriteJsonAtomicAsync(
                Path.Combine(directory, StateFileName),
                status,
                CancellationToken.None)
            .ConfigureAwait(false);
        return status;
    }

    private async Task<RuntimeInstallStatus> PersistCorruptAsync(
        string accountId,
        RuntimeInstallationRecord record,
        string reason,
        string detail)
    {
        var status = new RuntimeInstallStatus(
            RuntimeInstallState.Corrupt,
            record,
            reason,
            detail,
            _now());
        var directory = RuntimeDirectory(accountId, record.RuntimeId);
        Directory.CreateDirectory(directory);
        await WriteJsonAtomicAsync(
                Path.Combine(directory, StateFileName),
                status,
                CancellationToken.None)
            .ConfigureAwait(false);
        return status;
    }

    private async Task<RuntimeInstallStatus> PersistCorruptAtAsync(
        string runtimeDirectory,
        RuntimeInstallationRecord record,
        string reason,
        string detail)
    {
        var status = new RuntimeInstallStatus(
            RuntimeInstallState.Corrupt,
            record,
            reason,
            detail,
            _now());
        Directory.CreateDirectory(runtimeDirectory);
        await WriteJsonAtomicAsync(
                Path.Combine(runtimeDirectory, StateFileName),
                status,
                CancellationToken.None)
            .ConfigureAwait(false);
        return status;
    }

    private async Task<RuntimeInstallStatus> ReadStatusAsync(
        string accountId,
        string runtimeId,
        bool verifyPresence)
    {
        if (!LocalModelManifest.IsValidModelId(runtimeId))
        {
            return Status(RuntimeInstallState.Failed, null, "runtime-id-invalid", runtimeId);
        }

        var directory = RuntimeDirectory(accountId, runtimeId);
        var status = await TryReadJsonAsync<RuntimeInstallStatus>(
                Path.Combine(directory, StateFileName))
            .ConfigureAwait(false);
        if (status is null)
        {
            return new RuntimeInstallStatus(
                RuntimeInstallState.NotInstalled,
                null,
                null,
                null,
                _now());
        }

        if (status.Installation is not null
            && !string.Equals(
                status.Installation.AccountId,
                accountId,
                StringComparison.Ordinal))
        {
            return new RuntimeInstallStatus(
                RuntimeInstallState.Corrupt,
                status.Installation,
                LocalRuntimeReasonCodes.RuntimeOwnershipMismatch,
                "runtime installation belongs to another account",
                _now());
        }

        if (verifyPresence
            && status.State == RuntimeInstallState.Installed
            && status.Installation is not null)
        {
            var verification = await VerifyRecordFilesAsync(
                    status.Installation,
                    verifyHashes: false,
                    manifest: null,
                    accountId,
                    runtimeId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!verification.Ok)
            {
                return new RuntimeInstallStatus(
                    RuntimeInstallState.Corrupt,
                    status.Installation,
                    verification.Reason,
                    verification.Detail,
                    _now());
            }
        }

        return status;
    }

    private async Task<(bool Ok, string? Reason, string? Detail)> VerifyInstalledFilesAsync(
        string accountId,
        RuntimeInstallationRecord record,
        LocalRuntimeManifest manifest,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(record.AccountId, accountId, StringComparison.Ordinal))
        {
            return (false, LocalRuntimeReasonCodes.RuntimeOwnershipMismatch, "account does not own runtime");
        }

        if (!string.Equals(record.Sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(record.Revision, manifest.Revision, StringComparison.Ordinal))
        {
            return (false, LocalRuntimeReasonCodes.RevisionMismatch, "installed runtime does not match manifest");
        }

        if (!SameFileSet(record.RequiredFiles, manifest.RequiredFiles)
            || !string.Equals(
                record.ExecutableRelativePath,
                manifest.ExecutableRelativePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return (false, LocalRuntimeReasonCodes.RuntimeInstallCorrupt, "installed file set differs from manifest");
        }

        return await VerifyRecordFilesAsync(
                record,
                verifyHashes: true,
                manifest,
                accountId,
                manifest.RuntimeId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(bool Ok, string? Reason, string? Detail)> VerifyRecordFilesAsync(
        RuntimeInstallationRecord record,
        bool verifyHashes,
        LocalRuntimeManifest? manifest,
        string expectedAccountId,
        string expectedRuntimeId,
        CancellationToken cancellationToken)
    {
        var normalizedAccount = NormalizeAccount(record.AccountId, throwOnInvalid: false);
        if (!string.Equals(
                normalizedAccount,
                expectedAccountId,
                StringComparison.Ordinal)
            || !LocalModelManifest.IsValidModelId(expectedRuntimeId)
            || !string.Equals(
                record.RuntimeId,
                expectedRuntimeId,
                StringComparison.Ordinal))
        {
            return (
                false,
                LocalRuntimeReasonCodes.RuntimeOwnershipMismatch,
                "runtime record belongs to another account or runtime directory");
        }

        if (!IsSafeVersionName(record.VersionDirectoryName))
        {
            return (false, LocalRuntimeReasonCodes.RuntimeInstallCorrupt, "unsafe version directory");
        }

        if (!LocalModelManifest.IsValidArtifactFileName(record.ArtifactFileName)
            || !LocalModelManifest.IsValidSha256(record.Sha256)
            || record.SizeBytes <= 0
            || string.IsNullOrWhiteSpace(record.Revision)
            || !Enum.IsDefined(record.ArtifactKind)
            || !LocalRuntimeManifest.IsValidRelativePath(record.ExecutableRelativePath)
            || record.RequiredFiles is not { Count: > 0 }
            || record.RequiredFiles.Any(file => !file.IsValid)
            || record.RequiredFiles
                .Select(file => file.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != record.RequiredFiles.Count
            || !record.RequiredFiles.Any(file => string.Equals(
                file.RelativePath,
                record.ExecutableRelativePath,
                StringComparison.OrdinalIgnoreCase))
            || (record.ArtifactKind == LocalRuntimeArtifactKind.File
                && (record.RequiredFiles.Count != 1
                    || !string.Equals(
                        record.ExecutableRelativePath,
                        record.ArtifactFileName,
                        StringComparison.OrdinalIgnoreCase))))
        {
            return (
                false,
                LocalRuntimeReasonCodes.RuntimeInstallCorrupt,
                "runtime record is structurally invalid");
        }

        var versionDirectory = PathSafety.ResolveContained(
            Path.Combine(RuntimeDirectory(normalizedAccount, expectedRuntimeId), "versions"),
            record.VersionDirectoryName);
        if (versionDirectory is null || !Directory.Exists(versionDirectory))
        {
            return (false, LocalRuntimeReasonCodes.RuntimeInstallCorrupt, "runtime version directory missing");
        }

        var expectedFiles = manifest?.RequiredFiles ?? record.RequiredFiles;
        return await VerifyDirectoryFilesAsync(
                versionDirectory,
                expectedFiles,
                cancellationToken,
                verifyHashes)
            .ConfigureAwait(false);
    }

    private static async Task<(bool Ok, string? Reason, string? Detail)> VerifyDirectoryFilesAsync(
        string root,
        IReadOnlyList<LocalRuntimeFileManifest> files,
        CancellationToken cancellationToken,
        bool verifyHashes = true)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PathSafety.ResolveContained(
                root,
                LocalRuntimeManifest.NormalizeRelativePath(file.RelativePath));
            if (path is null || !File.Exists(path))
            {
                return (false, LocalRuntimeReasonCodes.RuntimeFileMissing, file.RelativePath);
            }

            var info = new FileInfo(path);
            if (info.Length != file.SizeBytes)
            {
                return (
                    false,
                    LocalRuntimeReasonCodes.SizeMismatch,
                    $"{file.RelativePath}: expected={file.SizeBytes};actual={info.Length}");
            }

            if (!verifyHashes)
            {
                continue;
            }

            var actual = await ModelInstallStore.ComputeSha256Async(path, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return (
                    false,
                    LocalRuntimeReasonCodes.RuntimeFileHashMismatch,
                    $"{file.RelativePath}: expected={file.Sha256};actual={actual}");
            }
        }

        return (true, null, null);
    }

    private static bool SameFileSet(
        IReadOnlyList<LocalRuntimeFileManifest> left,
        IReadOnlyList<LocalRuntimeFileManifest> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var byPath = left.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        return right.All(file =>
            byPath.TryGetValue(file.RelativePath, out var installed)
            && installed.SizeBytes == file.SizeBytes
            && string.Equals(installed.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeVersionName(string value) =>
        value.Length == 64
        && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private RuntimeInstallStatus Status(
        RuntimeInstallState state,
        RuntimeInstallationRecord? record,
        string? reason,
        string? detail) =>
        new(state, record, reason, detail, _now());

    private string RuntimeAccountsRoot => Path.Combine(_root, "runtime-accounts");

    private string StagingDirectoryFor(string accountId) =>
        Path.Combine(
            RuntimeAccountsRoot,
            PathSafety.HashAccountKey(accountId),
            ".staging");

    private string? ResolveOwnedRuntimeDirectory(string accountId, string runtimeId) =>
        LocalModelManifest.IsValidModelId(runtimeId)
            ? PathSafety.ResolveContained(
                RuntimeAccountsRoot,
                Path.Combine(PathSafety.HashAccountKey(accountId), runtimeId))
            : null;

    private string RuntimeDirectory(string accountId, string runtimeId) =>
        ResolveOwnedRuntimeDirectory(accountId, runtimeId)
        ?? throw new InvalidOperationException("runtime directory is not owned by this store");

    private int CleanStaleStaging(string stagingDirectory, List<string> errors)
    {
        if (!Directory.Exists(stagingDirectory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(stagingDirectory))
        {
            try
            {
                var isDirectory = Directory.Exists(path);
                var modified = isDirectory
                    ? Directory.GetLastWriteTimeUtc(path)
                    : File.GetLastWriteTimeUtc(path);
                if (_now() - modified < _stalePartialAge)
                {
                    continue;
                }

                if (isDirectory)
                {
                    if (TryDeleteDirectory(path))
                    {
                        removed++;
                    }
                    else
                    {
                        errors.Add(
                            $"runtime-staging-cleanup-refused: {Path.GetFileName(path)}");
                    }
                }
                else
                {
                    if (new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        errors.Add(
                            $"runtime-staging-cleanup-refused: {Path.GetFileName(path)}");
                        continue;
                    }

                    File.Delete(path);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"runtime-staging-cleanup-failed: {Path.GetFileName(path)}: {ex.GetType().Name}");
            }
        }

        return removed;
    }

    private static bool IsTreeFreeOfReparsePoints(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return !File.Exists(path)
                       || !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
            }

            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        return false;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Push(entry);
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsOwnedPathSafeForRecursiveDelete(string path)
    {
        var root = Path.GetFullPath(RuntimeAccountsRoot);
        var candidate = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return false;
        }

        var current = root;
        if (Directory.Exists(current)
            && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }
        }

        return IsTreeFreeOfReparsePoints(candidate);
    }

    private SemaphoreSlim GateFor(string accountId, string runtimeId)
    {
        var key = "runtime:" + PathSafety.HashAccountKey(accountId) + "/" + runtimeId;
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

    private static string NormalizeAccount(string accountId, bool throwOnInvalid = true)
    {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 256)
        {
            if (throwOnInvalid)
            {
                throw new ArgumentException("account id is required", nameof(accountId));
            }

            return string.Empty;
        }

        return accountId.Trim();
    }

    private static void RemoveUnusedVersions(string versionsDirectory, string keepVersion)
    {
        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
        {
            if (!string.Equals(
                    Path.GetFileName(directory),
                    keepVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteDirectory(directory);
            }
        }
    }

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
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

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (!IsTreeFreeOfReparsePoints(path))
                {
                    return false;
                }

                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class UnsafeArchiveException : Exception
    {
        public UnsafeArchiveException(string reasonCode, string message)
            : base(message)
        {
            ReasonCode = reasonCode;
        }

        public string ReasonCode { get; }
    }
}
