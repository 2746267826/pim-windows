using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pim.Client.Core.Models.LocalRuntime;

/// <summary>
/// Resource requirements declared by a local runtime manifest. A null value means
/// "not declared"; it is not a licence to invent a requirement at runtime.
/// </summary>
public sealed record LocalResourceRequirements(
    int? CpuCores = null,
    long? MemoryBytes = null,
    bool GpuRequired = false,
    long? VramBytes = null,
    bool RequireAcPower = false)
{
    public static LocalResourceRequirements None { get; } = new();
}

public sealed record LocalModelManifest(
    string ModelId,
    string DisplayName,
    string RuntimeId,
    string LicenseId,
    string LicenseUrl,
    string SourceUrl,
    string Revision,
    string Sha256,
    long SizeBytes,
    string ArtifactFileName,
    LocalResourceRequirements Resources)
{
    private static readonly Regex ModelIdPattern =
        new("^[a-z0-9][a-z0-9._-]{1,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ArtifactNamePattern =
        new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Sha256Pattern =
        new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValidModelId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ModelIdPattern.IsMatch(value);

    public static bool IsValidArtifactFileName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && ArtifactNamePattern.IsMatch(value)
        && !value.EndsWith(".", StringComparison.Ordinal)
        && !value.EndsWith(" ", StringComparison.Ordinal);

    public static bool IsValidSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Sha256Pattern.IsMatch(value);

    /// <summary>
    /// A model may only be offered for installation/execution when its artifact has a
    /// real 64-hex digest, a declared size, a licence and a pinned revision.
    /// </summary>
    public bool HasVerifiedArtifact =>
        IsValidSha256(Sha256)
        && SizeBytes > 0
        && !string.IsNullOrWhiteSpace(LicenseId)
        && !string.IsNullOrWhiteSpace(Revision);
}

public enum LocalRuntimeArtifactKind
{
    File,
    Zip,
}

public sealed record LocalRuntimeFileManifest(
    string RelativePath,
    long SizeBytes,
    string Sha256)
{
    public bool IsValid =>
        LocalRuntimeManifest.IsValidRelativePath(RelativePath)
        && SizeBytes > 0
        && LocalModelManifest.IsValidSha256(Sha256);
}

public sealed record LocalRuntimeExecutionProfile(
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    string OutputFormat)
{
    public const string WhisperCppJson = "whisper.cpp-json";
    public const string PlainText = "plain-text";

    public static IReadOnlySet<string> SupportedPlaceholders { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "modelPath",
            "inputPath",
            "language",
        };

    public bool IsValid =>
        Arguments is { Count: > 0 }
        && Timeout >= TimeSpan.FromSeconds(1)
        && Timeout <= TimeSpan.FromHours(1)
        && (string.Equals(OutputFormat, WhisperCppJson, StringComparison.Ordinal)
            || string.Equals(OutputFormat, PlainText, StringComparison.Ordinal));
}

public sealed record LocalRuntimeManifest(
    string RuntimeId,
    string DisplayName,
    string ArtifactFileName,
    string SourceUrl,
    string LicenseId,
    string LicenseUrl,
    string Revision,
    string Sha256,
    long SizeBytes,
    LocalRuntimeArtifactKind ArtifactKind,
    string ExecutableRelativePath,
    IReadOnlyList<LocalRuntimeFileManifest> RequiredFiles,
    LocalRuntimeExecutionProfile Execution)
{
    private static readonly Regex RelativePathSegmentPattern =
        new("^[A-Za-z0-9_][A-Za-z0-9._+ -]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PlaceholderPattern =
        new("\\{([^{}]+)\\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> WindowsReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

    public bool HasVerifiedRuntime =>
        LocalModelManifest.IsValidSha256(Sha256)
        && SizeBytes > 0
        && !string.IsNullOrWhiteSpace(LicenseId)
        && !string.IsNullOrWhiteSpace(Revision)
        && LocalModelManifest.IsValidArtifactFileName(ArtifactFileName)
        && IsValidRelativePath(ExecutableRelativePath)
        && ExecutableRelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && RequiredFiles is { Count: > 0 }
        && RequiredFiles.All(file => file.IsValid)
        && RequiredFiles
            .Select(file => file.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == RequiredFiles.Count
        && RequiredFiles.Any(file => string.Equals(
            file.RelativePath,
            ExecutableRelativePath,
            StringComparison.OrdinalIgnoreCase))
        && (ArtifactKind != LocalRuntimeArtifactKind.File
            || (RequiredFiles.Count == 1
                && string.Equals(
                    RequiredFiles[0].RelativePath,
                    ArtifactFileName,
                    StringComparison.OrdinalIgnoreCase)))
        && Execution.IsValid
        && Execution.Arguments.All(IsSafeArgumentTemplate);

    public static bool IsValidRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 240
            || Path.IsPathRooted(value)
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("\\", StringComparison.Ordinal)
            || value.Contains(':', StringComparison.Ordinal)
            || value.Contains('\0', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = value.Split('/');
        return segments.Length > 0
               && segments.All(segment =>
                   segment.Length > 0
                   && segment is not "." and not ".."
                   && RelativePathSegmentPattern.IsMatch(segment)
                   && !segment.EndsWith(".", StringComparison.Ordinal)
                   && !segment.EndsWith(" ", StringComparison.Ordinal)
                   && !IsWindowsReservedName(segment));
    }

    public static string NormalizeRelativePath(string value) =>
        value.Replace('/', Path.DirectorySeparatorChar);

    public static bool IsSafeArgumentTemplate(string? argument)
    {
        if (argument is null || argument.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (Match match in PlaceholderPattern.Matches(argument))
        {
            if (!LocalRuntimeExecutionProfile.SupportedPlaceholders.Contains(match.Groups[1].Value))
            {
                return false;
            }
        }

        var withoutPlaceholders = PlaceholderPattern.Replace(argument, string.Empty);
        return !withoutPlaceholders.Contains('{', StringComparison.Ordinal)
               && !withoutPlaceholders.Contains('}', StringComparison.Ordinal);
    }

    private static bool IsWindowsReservedName(string segment)
    {
        var stem = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        return WindowsReservedNames.Contains(stem);
    }
}

public sealed record LocalRuntimeCatalog(
    IReadOnlyList<LocalModelManifest> Models,
    IReadOnlyList<LocalRuntimeManifest> Runtimes)
{
    public static LocalRuntimeCatalog Empty { get; } =
        new(Array.Empty<LocalModelManifest>(), Array.Empty<LocalRuntimeManifest>());
}

public sealed record LocalRuntimeCatalogParseResult(
    LocalRuntimeCatalog Catalog,
    IReadOnlyList<string> Errors);

public enum ModelInstallState
{
    NotInstalled,
    Downloading,
    Verifying,
    Installed,
    Corrupt,
    Cancelled,
    Failed,
}

public sealed record ModelInstallationRecord(
    string ModelId,
    string AccountId,
    string Revision,
    string Sha256,
    long SizeBytes,
    string ArtifactFileName,
    DateTimeOffset InstalledAtUtc);

public sealed record ModelInstallStatus(
    ModelInstallState State,
    ModelInstallationRecord? Installation,
    string? ReasonCode,
    string? Detail,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsExecutable => State == ModelInstallState.Installed && Installation is not null;

    public static ModelInstallStatus NotInstalled(string accountId) =>
        new(ModelInstallState.NotInstalled, null, null, null, DateTimeOffset.UtcNow);
}

public sealed record ModelInstallProgress(long BytesReceived, long ExpectedBytes, ModelInstallState State);

public enum RuntimeInstallState
{
    NotInstalled,
    Downloading,
    Extracting,
    Verifying,
    Installed,
    Corrupt,
    Cancelled,
    Failed,
}

public sealed record RuntimeInstallationRecord(
    string RuntimeId,
    string AccountId,
    string Revision,
    string Sha256,
    long SizeBytes,
    string ArtifactFileName,
    LocalRuntimeArtifactKind ArtifactKind,
    string VersionDirectoryName,
    string ExecutableRelativePath,
    IReadOnlyList<LocalRuntimeFileManifest> RequiredFiles,
    DateTimeOffset InstalledAtUtc);

public sealed record RuntimeInstallStatus(
    RuntimeInstallState State,
    RuntimeInstallationRecord? Installation,
    string? ReasonCode,
    string? Detail,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsExecutable => State == RuntimeInstallState.Installed && Installation is not null;
}

public sealed record RuntimeInstallProgress(
    long BytesReceived,
    long ExpectedBytes,
    RuntimeInstallState State);

public sealed record ResourceSnapshot(
    DateTimeOffset MeasuredAtUtc,
    int? LogicalProcessorCount,
    double? CpuLoadPercent,
    long? AvailableMemoryBytes,
    long? VramAvailableBytes,
    bool? IsOnAcPower,
    int? BatteryPercent)
{
    public static ResourceSnapshot Unknown(DateTimeOffset measuredAtUtc) =>
        new(measuredAtUtc, null, null, null, null, null, null);
}

public sealed record ResourceDecision(bool Satisfied, IReadOnlyList<string> Reasons)
{
    public static ResourceDecision Ok { get; } =
        new(true, Array.Empty<string>());
}

public sealed record RuntimeLaunchSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string ExpectedSha256,
    TimeSpan Timeout,
    int MaxOutputBytes = 64 * 1024);

public sealed record RuntimeLaunchResult(
    bool Succeeded,
    int? ExitCode,
    bool Cancelled,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    string? ErrorCode,
    TimeSpan Duration);

public sealed record OwnedProcessIdentity(
    int ProcessId,
    long StartTimeUtcTicks,
    int SessionId,
    string ExecutablePath)
{
    public static bool Matches(
        OwnedProcessIdentity owned,
        int processId,
        long startTimeUtcTicks,
        int sessionId,
        string executablePath)
    {
        return owned.ProcessId == processId
               && owned.StartTimeUtcTicks == startTimeUtcTicks
               && owned.SessionId == sessionId
               && string.Equals(
                   Path.GetFullPath(owned.ExecutablePath),
                   Path.GetFullPath(executablePath),
                   StringComparison.OrdinalIgnoreCase);
    }
}

public static class LocalRuntimeReasonCodes
{
    public const string AccountRequired = "account-required";
    public const string ManifestUnverified = "manifest-unverified";
    public const string RuntimeUnverified = "runtime-unverified";
    public const string RuntimeManifestUnverified = "runtime-manifest-unverified";
    public const string SourceNotHttps = "source-not-https";
    public const string DiskInsufficient = "disk-insufficient";
    public const string DownloadFailed = "download-failed";
    public const string SizeMismatch = "size-mismatch";
    public const string HashMismatch = "hash-mismatch";
    public const string ArtifactMissing = "artifact-missing";
    public const string OwnershipMismatch = "ownership-mismatch";
    public const string Cancelled = "cancelled";
    public const string ExecutableHashMismatch = "executable-hash-mismatch";
    public const string ExecutableMissing = "executable-missing";
    public const string ArchiveInvalid = "archive-invalid";
    public const string ArchiveEntryUnsafe = "archive-entry-unsafe";
    public const string ArchiveLimitExceeded = "archive-limit-exceeded";
    public const string RuntimeFileMissing = "runtime-file-missing";
    public const string RuntimeFileHashMismatch = "runtime-file-hash-mismatch";
    public const string RuntimeOwnershipMismatch = "runtime-ownership-mismatch";
    public const string RuntimeInstallCorrupt = "runtime-install-corrupt";
    public const string ModelNotInstalled = "model-not-installed";
    public const string RuntimeNotInstalled = "runtime-not-installed";
    public const string RevisionMismatch = "revision-mismatch";
    public const string InputMissing = "input-missing";
    public const string InvalidExecutionProfile = "invalid-execution-profile";
    public const string OutputParseFailed = "output-parse-failed";
    public const string ProcessStartFailed = "process-start-failed";
    public const string JobCreateFailed = "job-create-failed";
    public const string JobAssignFailed = "job-assign-failed";
    public const string ProcessTimedOut = "process-timed-out";
    public const string ProcessCancelled = "process-cancelled";
    public const string ResourceCpuUnknown = "resource-cpu-unknown";
    public const string ResourceCpuInsufficient = "resource-cpu-insufficient";
    public const string ResourceMemoryUnknown = "resource-memory-unknown";
    public const string ResourceMemoryInsufficient = "resource-memory-insufficient";
    public const string ResourceVramUnknown = "resource-vram-unknown";
    public const string ResourceVramInsufficient = "resource-vram-insufficient";
    public const string ResourcePowerUnknown = "resource-power-unknown";
    public const string ResourceOnBattery = "resource-on-battery";
}

public static class PathSafety
{
    /// <summary>
    /// Resolves a relative path and verifies it stays under the supplied root.
    /// Returns null when the path escapes or is rooted.
    /// </summary>
    public static string? ResolveContained(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        if (Path.IsPathRooted(relative))
        {
            return null;
        }

        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relative));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    public static string HashAccountKey(string accountId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(accountId));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }
}
