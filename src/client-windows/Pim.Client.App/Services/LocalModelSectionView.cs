using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.App.Services;

public sealed record LocalModelSectionItem(
    string ModelId,
    string DisplayName,
    bool Installable,
    bool Executable,
    string StatusText);

/// <summary>Pure formatting for the 本地模型 section; unit-tested without WPF.</summary>
public static class LocalModelSectionView
{
    public static LocalModelSectionItem ToItem(
        LocalModelManifest manifest,
        ModelInstallStatus status)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(status);

        return new LocalModelSectionItem(
            manifest.ModelId,
            manifest.DisplayName,
            manifest.HasVerifiedArtifact,
            status.IsExecutable,
            FormatStatus(manifest, status));
    }

    public static string FormatStatus(LocalModelManifest manifest, ModelInstallStatus status)
    {
        var stateText = status.State switch
        {
            ModelInstallState.NotInstalled => "未安装",
            ModelInstallState.Downloading => "下载中",
            ModelInstallState.Verifying => "校验中",
            ModelInstallState.Installed => "已安装",
            ModelInstallState.Corrupt => "已损坏",
            ModelInstallState.Cancelled => "已取消",
            ModelInstallState.Failed => "失败",
            _ => status.State.ToString(),
        };

        var detail = status.ReasonCode is null
            ? string.Empty
            : $"（原因 {status.ReasonCode}）";
        var sizeText = $"{manifest.SizeBytes / (1024.0 * 1024):F1} MB";
        var verified = manifest.HasVerifiedArtifact ? "摘要已登记" : "未登记摘要";
        return $"{stateText}{detail} · {sizeText} · {verified} · 修订 {manifest.Revision}";
    }

    public static string FormatCatalogProblem(IReadOnlyList<string> errors) =>
        errors.Count == 0
            ? string.Empty
            : "目录条目问题：" + string.Join("；", errors);
}
