using System.Text.Json;
using System.Text.Json.Serialization;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Parses the operator-supplied local runtime catalog. Invalid entries are
/// excluded from the usable catalog and reported, never silently promoted to
/// executable.
/// </summary>
public static class LocalRuntimeCatalogParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static LocalRuntimeCatalogParseResult Parse(string json)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            errors.Add("catalog-empty");
            return new LocalRuntimeCatalogParseResult(LocalRuntimeCatalog.Empty, errors);
        }

        CatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            errors.Add($"catalog-json-invalid: {ex.Message}");
            return new LocalRuntimeCatalogParseResult(LocalRuntimeCatalog.Empty, errors);
        }

        if (document is null)
        {
            errors.Add("catalog-json-invalid: document is null");
            return new LocalRuntimeCatalogParseResult(LocalRuntimeCatalog.Empty, errors);
        }

        if (document.SchemaVersion != 1)
        {
            errors.Add($"catalog-schema-unsupported: {document.SchemaVersion}");
            return new LocalRuntimeCatalogParseResult(LocalRuntimeCatalog.Empty, errors);
        }

        if (document.CandidateStatus is { Length: > 128 })
        {
            errors.Add("catalog-candidate-status-invalid");
            return new LocalRuntimeCatalogParseResult(LocalRuntimeCatalog.Empty, errors);
        }

        if (document.CatalogueId is { Length: > 128 }
            || (document.CatalogueId is not null
                && !LocalModelManifest.IsValidModelId(document.CatalogueId.Trim())))
        {
            errors.Add("catalog-catalogue-id-invalid");
            return new LocalRuntimeCatalogParseResult(LocalRuntimeCatalog.Empty, errors);
        }

        var runtimes = new List<LocalRuntimeManifest>();
        var runtimeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Runtimes ?? Array.Empty<RuntimeEntry>())
        {
            var validation = ValidateRuntime(entry);
            if (validation.Error is not null)
            {
                errors.Add(validation.Error);
                continue;
            }

            if (!runtimeIds.Add(validation.Runtime!.RuntimeId))
            {
                errors.Add($"runtime-duplicate: {validation.Runtime.RuntimeId}");
                continue;
            }

            runtimes.Add(validation.Runtime);
        }

        var models = new List<LocalModelManifest>();
        var modelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Models ?? Array.Empty<ModelEntry>())
        {
            var validation = ValidateModel(entry);
            if (validation.Error is not null)
            {
                errors.Add(validation.Error);
                continue;
            }

            if (!modelIds.Add(validation.Model!.ModelId))
            {
                errors.Add($"model-duplicate: {validation.Model.ModelId}");
                continue;
            }

            models.Add(validation.Model);
        }

        return new LocalRuntimeCatalogParseResult(
            new LocalRuntimeCatalog(models, runtimes),
            errors,
            NormalizeOptionalText(document.CatalogueId),
            NormalizeOptionalText(document.CandidateStatus));
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (LocalRuntimeManifest? Runtime, string? Error) ValidateRuntime(RuntimeEntry entry)
    {
        var id = entry.RuntimeId?.Trim() ?? string.Empty;
        if (!LocalModelManifest.IsValidModelId(id))
        {
            return (null, $"runtime-id-invalid: {entry.RuntimeId}");
        }

        if (!LocalModelManifest.IsValidArtifactFileName(entry.ArtifactFileName))
        {
            return (null, $"runtime-artifact-invalid: {id}");
        }

        if (!TryValidateSource(entry.SourceUrl, out var sourceError))
        {
            return (null, $"runtime-source-invalid: {id}: {sourceError}");
        }

        if (!TryValidateOptionalHttps(entry.LicenseUrl, out var licenseError))
        {
            return (null, $"runtime-license-url-invalid: {id}: {licenseError}");
        }

        var sha = (entry.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
        if (sha.Length > 0 && !LocalModelManifest.IsValidSha256(sha))
        {
            return (null, $"runtime-sha-invalid: {id}");
        }

        if (entry.SizeBytes <= 0)
        {
            return (null, $"runtime-size-invalid: {id}");
        }

        if (!TryParseArtifactKind(entry.ArchiveKind, out var artifactKind))
        {
            return (null, $"runtime-archive-kind-invalid: {id}");
        }

        var executablePath = entry.ExecutablePath?.Trim() ?? string.Empty;
        if (!LocalRuntimeManifest.IsValidRelativePath(executablePath)
            || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"runtime-executable-invalid: {id}");
        }

        var requiredFiles = new List<LocalRuntimeFileManifest>();
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in entry.Files ?? Array.Empty<RuntimeFileEntry>())
        {
            var path = file.Path?.Trim() ?? string.Empty;
            var fileSha = (file.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
            if (!LocalRuntimeManifest.IsValidRelativePath(path)
                || file.SizeBytes <= 0
                || !LocalModelManifest.IsValidSha256(fileSha))
            {
                return (null, $"runtime-file-invalid: {id}: {file.Path}");
            }

            if (!filePaths.Add(path))
            {
                return (null, $"runtime-file-duplicate: {id}: {path}");
            }

            requiredFiles.Add(new LocalRuntimeFileManifest(path, file.SizeBytes, fileSha));
        }

        if (requiredFiles.Count == 0
            || !filePaths.Contains(executablePath))
        {
            return (null, $"runtime-required-files-invalid: {id}");
        }

        if (artifactKind == LocalRuntimeArtifactKind.File
            && (requiredFiles.Count != 1
                || !string.Equals(requiredFiles[0].RelativePath, executablePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(requiredFiles[0].RelativePath, entry.ArtifactFileName, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, $"runtime-file-artifact-invalid: {id}");
        }

        var arguments = entry.Execution?.Arguments ?? Array.Empty<string>();
        if (arguments.Length == 0
            || arguments.Any(argument => !LocalRuntimeManifest.IsSafeArgumentTemplate(argument)))
        {
            return (null, $"runtime-arguments-invalid: {id}");
        }

        var timeoutSeconds = entry.Execution?.TimeoutSeconds ?? 0;
        if (timeoutSeconds is < 1 or > 3600)
        {
            return (null, $"runtime-timeout-invalid: {id}");
        }

        var outputFormat = entry.Execution?.OutputFormat?.Trim()
                           ?? LocalRuntimeExecutionProfile.WhisperCppJson;
        if (!string.Equals(outputFormat, LocalRuntimeExecutionProfile.WhisperCppJson, StringComparison.Ordinal)
            && !string.Equals(outputFormat, LocalRuntimeExecutionProfile.PlainText, StringComparison.Ordinal))
        {
            return (null, $"runtime-output-format-invalid: {id}");
        }

        return (new LocalRuntimeManifest(
            id,
            string.IsNullOrWhiteSpace(entry.DisplayName) ? id : entry.DisplayName.Trim(),
            entry.ArtifactFileName!.Trim(),
            entry.SourceUrl!.Trim(),
            entry.LicenseId?.Trim() ?? string.Empty,
            entry.LicenseUrl?.Trim() ?? string.Empty,
            entry.Revision?.Trim() ?? string.Empty,
            sha,
            entry.SizeBytes,
            artifactKind,
            executablePath,
            requiredFiles,
            new LocalRuntimeExecutionProfile(
                arguments.ToArray(),
                TimeSpan.FromSeconds(timeoutSeconds),
                outputFormat)), null);
    }

    private static (LocalModelManifest? Model, string? Error) ValidateModel(ModelEntry entry)
    {
        var id = entry.ModelId?.Trim() ?? string.Empty;
        if (!LocalModelManifest.IsValidModelId(id))
        {
            return (null, $"model-id-invalid: {entry.ModelId}");
        }

        if (!LocalModelManifest.IsValidArtifactFileName(entry.ArtifactFileName))
        {
            return (null, $"model-artifact-invalid: {id}");
        }

        if (!TryValidateSource(entry.SourceUrl, out var sourceError))
        {
            return (null, $"model-source-invalid: {id}: {sourceError}");
        }

        if (!TryValidateOptionalHttps(entry.LicenseUrl, out var licenseError))
        {
            return (null, $"model-license-url-invalid: {id}: {licenseError}");
        }

        if (entry.SizeBytes <= 0)
        {
            return (null, $"model-size-invalid: {id}");
        }

        var sha = (entry.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
        if (sha.Length > 0 && !LocalModelManifest.IsValidSha256(sha))
        {
            return (null, $"model-sha-invalid: {id}");
        }

        var resources = entry.Resources ?? new ResourceEntry();
        if (resources.CpuCores is <= 0
            || resources.MemoryBytes is <= 0
            || resources.VramBytes is <= 0)
        {
            return (null, $"model-resources-invalid: {id}");
        }

        if (!resources.GpuRequired && resources.VramBytes is not null)
        {
            return (null, $"model-resources-invalid: {id}: vram declared without gpuRequired");
        }

        return (new LocalModelManifest(
            id,
            string.IsNullOrWhiteSpace(entry.DisplayName) ? id : entry.DisplayName.Trim(),
            entry.RuntimeId?.Trim() ?? string.Empty,
            entry.LicenseId?.Trim() ?? string.Empty,
            entry.LicenseUrl?.Trim() ?? string.Empty,
            entry.SourceUrl!.Trim(),
            entry.Revision?.Trim() ?? string.Empty,
            sha,
            entry.SizeBytes,
            entry.ArtifactFileName!.Trim(),
            new LocalResourceRequirements(
                resources.CpuCores,
                resources.MemoryBytes,
                resources.GpuRequired,
                resources.VramBytes,
                resources.RequireAcPower)), null);
    }

    private static bool TryValidateSource(string? value, out string error)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "source must be an absolute https URL";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateOptionalHttps(string? value, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = string.Empty;
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "licence URL must be an absolute https URL";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseArtifactKind(
        string? value,
        out LocalRuntimeArtifactKind kind)
    {
        if (string.Equals(value, "zip", StringComparison.OrdinalIgnoreCase))
        {
            kind = LocalRuntimeArtifactKind.Zip;
            return true;
        }

        if (string.Equals(value, "file", StringComparison.OrdinalIgnoreCase))
        {
            kind = LocalRuntimeArtifactKind.File;
            return true;
        }

        kind = default;
        return false;
    }

    private sealed class CatalogDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("catalogueId")]
        public string? CatalogueId { get; set; }

        [JsonPropertyName("candidateStatus")]
        public string? CandidateStatus { get; set; }

        [JsonPropertyName("models")]
        public ModelEntry[]? Models { get; set; }

        [JsonPropertyName("runtimes")]
        public RuntimeEntry[]? Runtimes { get; set; }
    }

    private sealed class ModelEntry
    {
        public string? ModelId { get; set; }
        public string? DisplayName { get; set; }
        public string? RuntimeId { get; set; }
        public string? LicenseId { get; set; }
        public string? LicenseUrl { get; set; }
        public string? SourceUrl { get; set; }
        public string? Revision { get; set; }
        public string? Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public string? ArtifactFileName { get; set; }
        public ResourceEntry? Resources { get; set; }
    }

    private sealed class RuntimeEntry
    {
        public string? RuntimeId { get; set; }
        public string? DisplayName { get; set; }
        public string? ArtifactFileName { get; set; }
        public string? SourceUrl { get; set; }
        public string? LicenseId { get; set; }
        public string? LicenseUrl { get; set; }
        public string? Revision { get; set; }
        public string? Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public string? ArchiveKind { get; set; }
        public string? ExecutablePath { get; set; }
        public RuntimeFileEntry[]? Files { get; set; }
        public RuntimeExecutionEntry? Execution { get; set; }
    }

    private sealed class RuntimeFileEntry
    {
        public string? Path { get; set; }
        public long SizeBytes { get; set; }
        public string? Sha256 { get; set; }
    }

    private sealed class RuntimeExecutionEntry
    {
        public string[]? Arguments { get; set; }
        public int TimeoutSeconds { get; set; }
        public string? OutputFormat { get; set; }
    }

    private sealed class ResourceEntry
    {
        public int? CpuCores { get; set; }
        public long? MemoryBytes { get; set; }
        public bool GpuRequired { get; set; }
        public long? VramBytes { get; set; }
        public bool RequireAcPower { get; set; }
    }
}
