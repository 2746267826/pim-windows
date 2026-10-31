using System.Text.Json;
using System.Text.RegularExpressions;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

public sealed record LocalSpeechExecutionRequest(
    string AccountId,
    string InputPath,
    string Language = "auto",
    LocalAccountScope? Scope = null)
{
    public LocalSpeechExecutionRequest(
        LocalAccountScope scope,
        string inputPath,
        string language = "auto")
        : this(scope.OwnerIdentity, inputPath, language, scope)
    {
    }
}

public sealed record LocalSpeechTranscriptSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text);

public sealed record LocalSpeechExecutionResult(
    bool Succeeded,
    string? ErrorCode,
    string? Detail,
    string Text,
    IReadOnlyList<LocalSpeechTranscriptSegment> Segments,
    RuntimeLaunchResult? Process)
{
    public static LocalSpeechExecutionResult Failed(
        string errorCode,
        string? detail = null,
        RuntimeLaunchResult? process = null) =>
        new(false, errorCode, detail, string.Empty, Array.Empty<LocalSpeechTranscriptSegment>(), process);
}

public interface ILocalSpeechRuntimeAdapter
{
    Task<LocalSpeechExecutionResult> ExecuteAsync(
        LocalRuntimeManifest runtimeManifest,
        LocalModelManifest modelManifest,
        LocalSpeechExecutionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes a catalog-configured local ASR runtime through the controlled process
/// runner. Paths and language are passed as individual argv entries, never through a
/// shell. Runtime and model digests are re-verified before every launch.
/// </summary>
public sealed class LocalRuntimeExecutionAdapter : ILocalSpeechRuntimeAdapter
{
    private const int MaxOutputBytes = 4 * 1024 * 1024;
    private static readonly Regex PlaceholderPattern =
        new("\\{(modelPath|inputPath|language)\\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LanguagePattern =
        new("^(auto|[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly RuntimeInstallStore _runtimeStore;
    private readonly ModelInstallStore _modelStore;
    private readonly IControlledProcessRunner _runner;
    private readonly LocalRuntimeAccountGuard _accountGuard;

    public LocalRuntimeExecutionAdapter(
        RuntimeInstallStore runtimeStore,
        ModelInstallStore modelStore,
        IControlledProcessRunner runner,
        LocalRuntimeAccountGuard accountGuard)
    {
        _runtimeStore = runtimeStore;
        _modelStore = modelStore;
        _runner = runner;
        _accountGuard = accountGuard;
    }

    public async Task<LocalSpeechExecutionResult> ExecuteAsync(
        LocalRuntimeManifest runtimeManifest,
        LocalModelManifest modelManifest,
        LocalSpeechExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeManifest);
        ArgumentNullException.ThrowIfNull(modelManifest);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AccountId))
        {
            return LocalSpeechExecutionResult.Failed(LocalRuntimeReasonCodes.AccountRequired);
        }

        if (!runtimeManifest.HasVerifiedRuntime
            || !modelManifest.HasVerifiedArtifact
            || !string.Equals(
                modelManifest.RuntimeId,
                runtimeManifest.RuntimeId,
                StringComparison.Ordinal))
        {
            return LocalSpeechExecutionResult.Failed(LocalRuntimeReasonCodes.ManifestUnverified);
        }

        if (!LanguagePattern.IsMatch(request.Language))
        {
            return LocalSpeechExecutionResult.Failed(
                LocalRuntimeReasonCodes.InvalidExecutionProfile,
                "language must be auto or a BCP-47-like tag");
        }

        if (string.IsNullOrWhiteSpace(request.InputPath)
            || !Path.IsPathFullyQualified(request.InputPath)
            || !File.Exists(request.InputPath))
        {
            return LocalSpeechExecutionResult.Failed(LocalRuntimeReasonCodes.InputMissing);
        }

        var scope = request.Scope;
        if (scope is null)
        {
            // A worker must carry the origin that issued the account identity. Falling
            // back to an owner-only run would let an old origin's lease escape a switch.
            return LocalSpeechExecutionResult.Failed(
                LocalRuntimeReasonCodes.AccountRequired,
                "an explicit server origin/owner scope is required");
        }

        if (!string.Equals(scope.OwnerIdentity, request.AccountId, StringComparison.Ordinal))
        {
            return LocalSpeechExecutionResult.Failed(
                LocalRuntimeReasonCodes.OwnershipMismatch,
                "request account does not match its server origin/owner scope");
        }

        CancellationToken accountToken;
        try
        {
            accountToken = _accountGuard.BeginRun(scope);
        }
        catch (InvalidOperationException ex)
        {
            return LocalSpeechExecutionResult.Failed(
                LocalRuntimeReasonCodes.OwnershipMismatch,
                ex.Message);
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                accountToken);
            var token = linked.Token;

            RuntimeInstallStatus runtimeStatus;
            ModelInstallStatus modelStatus;
            try
            {
                runtimeStatus = await _runtimeStore
                    .VerifyAsync(runtimeManifest, request.AccountId, token)
                    .ConfigureAwait(false);
                modelStatus = await _modelStore
                    .VerifyAsync(request.AccountId, modelManifest.ModelId, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return LocalSpeechExecutionResult.Failed(LocalRuntimeReasonCodes.Cancelled);
            }

            if (!runtimeStatus.IsExecutable || runtimeStatus.Installation is null)
            {
                return LocalSpeechExecutionResult.Failed(
                    LocalRuntimeReasonCodes.RuntimeNotInstalled,
                    runtimeStatus.ReasonCode);
            }

            if (!modelStatus.IsExecutable || modelStatus.Installation is null)
            {
                return LocalSpeechExecutionResult.Failed(
                    LocalRuntimeReasonCodes.ModelNotInstalled,
                    modelStatus.ReasonCode);
            }

            var runtime = runtimeStatus.Installation;
            var model = modelStatus.Installation;
            if (!string.Equals(runtime.Revision, runtimeManifest.Revision, StringComparison.Ordinal)
                || !string.Equals(runtime.Sha256, runtimeManifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return LocalSpeechExecutionResult.Failed(LocalRuntimeReasonCodes.RevisionMismatch);
            }

            if (!string.Equals(model.Revision, modelManifest.Revision, StringComparison.Ordinal)
                || !string.Equals(model.Sha256, modelManifest.Sha256, StringComparison.OrdinalIgnoreCase)
                || model.SizeBytes != modelManifest.SizeBytes
                || !string.Equals(
                    model.ArtifactFileName,
                    modelManifest.ArtifactFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return LocalSpeechExecutionResult.Failed(LocalRuntimeReasonCodes.RevisionMismatch);
            }

            var executablePath = _runtimeStore.ResolveInstalledFilePath(
                request.AccountId,
                runtime,
                runtime.ExecutableRelativePath);
            var modelPath = _modelStore.ResolveArtifactPath(request.AccountId, model);
            if (executablePath is null || !File.Exists(executablePath))
            {
                return LocalSpeechExecutionResult.Failed(LocalRuntimeReasonCodes.ExecutableMissing);
            }

            if (modelPath is null || !File.Exists(modelPath))
            {
                return LocalSpeechExecutionResult.Failed(LocalRuntimeReasonCodes.ArtifactMissing);
            }

            var workingDirectory = Path.GetDirectoryName(executablePath);
            if (workingDirectory is null)
            {
                return LocalSpeechExecutionResult.Failed(LocalRuntimeReasonCodes.ExecutableMissing);
            }

            var arguments = new List<string>(runtimeManifest.Execution.Arguments.Count);
            foreach (var template in runtimeManifest.Execution.Arguments)
            {
                var expanded = ExpandArgument(
                    template,
                    modelPath!,
                    Path.GetFullPath(request.InputPath),
                    request.Language);
                if (expanded is null)
                {
                    return LocalSpeechExecutionResult.Failed(
                        LocalRuntimeReasonCodes.InvalidExecutionProfile,
                        template);
                }

                arguments.Add(expanded);
            }

            var executableDigest = runtime.RequiredFiles
                .First(file => string.Equals(
                    file.RelativePath,
                    runtime.ExecutableRelativePath,
                    StringComparison.OrdinalIgnoreCase))
                .Sha256;
            var process = await _runner.StartAsync(
                    new RuntimeLaunchSpec(
                        executablePath,
                        arguments,
                        workingDirectory,
                        executableDigest,
                        runtimeManifest.Execution.Timeout,
                        MaxOutputBytes),
                    token)
                .ConfigureAwait(false);

            if (!process.Succeeded)
            {
                return LocalSpeechExecutionResult.Failed(
                    process.Cancelled
                        ? LocalRuntimeReasonCodes.ProcessCancelled
                        : process.TimedOut
                            ? LocalRuntimeReasonCodes.ProcessTimedOut
                            : process.ErrorCode ?? "process-exit-nonzero",
                    process.StandardError,
                    process);
            }

            if (process.OutputTruncated)
            {
                return LocalSpeechExecutionResult.Failed(
                    LocalRuntimeReasonCodes.OutputTruncated,
                    "runtime output exceeded the bounded capture budget",
                    process);
            }

            if (string.Equals(
                    runtimeManifest.Execution.OutputFormat,
                    LocalRuntimeExecutionProfile.PlainText,
                    StringComparison.Ordinal))
            {
                var text = process.StandardOutput.Trim();
                return text.Length == 0
                    ? LocalSpeechExecutionResult.Failed(
                        LocalRuntimeReasonCodes.OutputParseFailed,
                        "runtime produced empty output",
                        process)
                    : new LocalSpeechExecutionResult(
                        true,
                        null,
                        null,
                        text,
                        Array.Empty<LocalSpeechTranscriptSegment>(),
                        process);
            }

            return ParseWhisperCppJson(process);
        }
        finally
        {
            _accountGuard.EndRun(accountToken);
        }
    }

    private static string? ExpandArgument(
        string template,
        string modelPath,
        string inputPath,
        string language)
    {
        if (!LocalRuntimeManifest.IsSafeArgumentTemplate(template))
        {
            return null;
        }

        return PlaceholderPattern.Replace(
            template,
            match => match.Groups[1].Value switch
            {
                "modelPath" => modelPath,
                "inputPath" => inputPath,
                "language" => language,
                _ => throw new InvalidOperationException("unsupported execution placeholder"),
            });
    }

    private static LocalSpeechExecutionResult ParseWhisperCppJson(RuntimeLaunchResult process)
    {
        try
        {
            using var document = JsonDocument.Parse(process.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return LocalSpeechExecutionResult.Failed(
                    LocalRuntimeReasonCodes.OutputParseFailed,
                    "whisper.cpp output root must be an object",
                    process);
            }

            var segments = new List<LocalSpeechTranscriptSegment>();
            if (root.TryGetProperty("transcription", out var transcription))
            {
                if (transcription.ValueKind != JsonValueKind.Array)
                {
                    return LocalSpeechExecutionResult.Failed(
                        LocalRuntimeReasonCodes.OutputParseFailed,
                        "transcription must be an array",
                        process);
                }

                foreach (var item in transcription.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("text", out var textElement)
                        || textElement.ValueKind != JsonValueKind.String)
                    {
                        return LocalSpeechExecutionResult.Failed(
                            LocalRuntimeReasonCodes.OutputParseFailed,
                            "transcription item is missing text",
                            process);
                    }

                    if (!TryReadOffset(item, out var start, out var end))
                    {
                        return LocalSpeechExecutionResult.Failed(
                            LocalRuntimeReasonCodes.OutputParseFailed,
                            "transcription item has invalid offsets",
                            process);
                    }

                    if (start < TimeSpan.Zero || end < start)
                    {
                        return LocalSpeechExecutionResult.Failed(
                            LocalRuntimeReasonCodes.OutputParseFailed,
                            "transcription item has negative or reversed offsets",
                            process);
                    }

                    segments.Add(new LocalSpeechTranscriptSegment(
                        start,
                        end,
                        textElement.GetString()?.Trim() ?? string.Empty));
                }
            }

            string aggregate;
            if (root.TryGetProperty("text", out var topLevelText))
            {
                if (topLevelText.ValueKind != JsonValueKind.String)
                {
                    return LocalSpeechExecutionResult.Failed(
                        LocalRuntimeReasonCodes.OutputParseFailed,
                        "text must be a string",
                        process);
                }

                aggregate = topLevelText.GetString()?.Trim() ?? string.Empty;
                if (aggregate.Length == 0)
                {
                    aggregate = AggregateSegmentText(segments);
                }
            }
            else
            {
                aggregate = AggregateSegmentText(segments);
            }

            if (aggregate.Length == 0)
            {
                return LocalSpeechExecutionResult.Failed(
                    LocalRuntimeReasonCodes.OutputParseFailed,
                    "runtime produced no transcript",
                    process);
            }

            return new LocalSpeechExecutionResult(
                true,
                null,
                null,
                aggregate,
                segments,
                process);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException)
        {
            return LocalSpeechExecutionResult.Failed(
                LocalRuntimeReasonCodes.OutputParseFailed,
                ex.Message,
                process);
        }
    }

    private static string AggregateSegmentText(
        IReadOnlyList<LocalSpeechTranscriptSegment> segments) =>
        string.Join(
            " ",
            segments.Select(segment => segment.Text)
                .Where(text => text.Length > 0));

    private static bool TryReadOffset(
        JsonElement item,
        out TimeSpan start,
        out TimeSpan end)
    {
        start = default;
        end = default;
        if (item.TryGetProperty("offsets", out var offsets))
        {
            return offsets.ValueKind == JsonValueKind.Object
                   && offsets.TryGetProperty("from", out var from)
                   && offsets.TryGetProperty("to", out var to)
                   && TryReadMilliseconds(from, out start)
                   && TryReadMilliseconds(to, out end);
        }

        if (item.TryGetProperty("timestamps", out var timestamps))
        {
            return timestamps.ValueKind == JsonValueKind.Object
                   && timestamps.TryGetProperty("from", out var fromText)
                   && timestamps.TryGetProperty("to", out var toText)
                   && fromText.ValueKind == JsonValueKind.String
                   && toText.ValueKind == JsonValueKind.String
                   && TimeSpan.TryParse(fromText.GetString(), out start)
                   && TimeSpan.TryParse(toText.GetString(), out end);
        }

        start = default;
        end = default;
        return false;
    }

    private static bool TryReadMilliseconds(JsonElement element, out TimeSpan value)
    {
        value = default;
        if (!element.TryGetInt64(out var milliseconds)
            || milliseconds < 0
            || milliseconds > (long)TimeSpan.MaxValue.TotalMilliseconds)
        {
            return false;
        }

        try
        {
            value = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
