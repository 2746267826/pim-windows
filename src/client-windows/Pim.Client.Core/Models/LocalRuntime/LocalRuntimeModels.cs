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

public sealed record LocalRuntimeManifest(
    string RuntimeId,
    string DisplayName,
    string ExecutableFileName,
    string SourceUrl,
    string LicenseId,
    string LicenseUrl,
    string Revision,
    string Sha256)
{
    public bool HasVerifiedRuntime =>
        LocalModelManifest.IsValidSha256(Sha256)
        && !string.IsNullOrWhiteSpace(LicenseId)
        && !string.IsNullOrWhiteSpace(Revision)
        && LocalModelManifest.IsValidArtifactFileName(ExecutableFileName);
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
