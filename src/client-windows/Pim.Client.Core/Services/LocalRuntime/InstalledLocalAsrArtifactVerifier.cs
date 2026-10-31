using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Fresh installation proof for ASR registration and heartbeat. Catalog metadata
/// alone is never accepted: the installed runtime file set and model artifact are
/// re-hashed for the current account, then compared with the exact manifest.
/// </summary>
public sealed class InstalledLocalAsrArtifactVerifier
{
    private readonly Func<LocalAccountScope?> _currentScope;
    private readonly RuntimeInstallStore _runtimeStore;
    private readonly ModelInstallStore _modelStore;

    public InstalledLocalAsrArtifactVerifier(
        Func<LocalAccountScope?> currentScope,
        RuntimeInstallStore runtimeStore,
        ModelInstallStore modelStore)
    {
        _currentScope = currentScope ?? throw new ArgumentNullException(nameof(currentScope));
        _runtimeStore = runtimeStore ?? throw new ArgumentNullException(nameof(runtimeStore));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
    }

    public async Task<bool> VerifyAsync(
        LocalRuntimeManifest runtime,
        LocalModelManifest model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(model);
        var scope = _currentScope();
        if (scope is null
            || !runtime.HasVerifiedRuntime
            || !model.HasVerifiedArtifact
            || !string.Equals(runtime.RuntimeId, model.RuntimeId, StringComparison.Ordinal))
        {
            return false;
        }

        var runtimeStatus = await _runtimeStore
            .VerifyAsync(runtime, scope.OwnerIdentity, cancellationToken)
            .ConfigureAwait(false);
        if (!SameScope(scope, _currentScope())
            || !runtimeStatus.IsExecutable
            || runtimeStatus.Installation is not { } installedRuntime
            || !RuntimeMatchesManifest(installedRuntime, runtime))
        {
            return false;
        }

        var modelStatus = await _modelStore
            .VerifyAsync(scope.OwnerIdentity, model.ModelId, cancellationToken)
            .ConfigureAwait(false);
        return SameScope(scope, _currentScope())
               && modelStatus.IsExecutable
               && modelStatus.Installation is { } installedModel
               && string.Equals(installedModel.AccountId, scope.OwnerIdentity, StringComparison.Ordinal)
               && string.Equals(installedModel.ModelId, model.ModelId, StringComparison.Ordinal)
               && string.Equals(installedModel.Revision, model.Revision, StringComparison.Ordinal)
               && string.Equals(installedModel.Sha256, model.Sha256, StringComparison.OrdinalIgnoreCase)
               && installedModel.SizeBytes == model.SizeBytes
               && string.Equals(
                   installedModel.ArtifactFileName,
                   model.ArtifactFileName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool RuntimeMatchesManifest(
        RuntimeInstallationRecord installed,
        LocalRuntimeManifest manifest) =>
        string.Equals(installed.RuntimeId, manifest.RuntimeId, StringComparison.Ordinal)
        && string.Equals(installed.Revision, manifest.Revision, StringComparison.Ordinal)
        && string.Equals(installed.Sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase)
        && installed.SizeBytes == manifest.SizeBytes
        && string.Equals(
            installed.ArtifactFileName,
            manifest.ArtifactFileName,
            StringComparison.OrdinalIgnoreCase)
        && installed.ArtifactKind == manifest.ArtifactKind
        && string.Equals(
            installed.ExecutableRelativePath,
            manifest.ExecutableRelativePath,
            StringComparison.OrdinalIgnoreCase)
        && SameFileSet(installed.RequiredFiles, manifest.RequiredFiles);

    private static bool SameFileSet(
        IReadOnlyList<LocalRuntimeFileManifest> installed,
        IReadOnlyList<LocalRuntimeFileManifest> manifest) =>
        installed.Count == manifest.Count
        && installed.All(file => manifest.Any(expected =>
            string.Equals(expected.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase)
            && expected.SizeBytes == file.SizeBytes
            && string.Equals(expected.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)));

    private static bool SameScope(LocalAccountScope expected, LocalAccountScope? current) =>
        current is not null && expected.Matches(current);
}
