using System.IO;
using System.Text;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;

namespace Pim.Client.App.Services;

internal sealed record LocalRuntimeCatalogLoadResult(
    LocalRuntimeCatalogParseResult Parse,
    string CatalogueLabel);

/// <summary>
/// Loads the operator catalog first, then bundled candidate catalogs. Duplicate
/// model/runtime ids are rejected instead of silently selecting the first entry.
/// </summary>
internal sealed class LocalRuntimeCatalogProvider
{
    private readonly string _operatorPath;
    private readonly string _shippedDirectory;

    public LocalRuntimeCatalogProvider(
        string? operatorPath = null,
        string? shippedDirectory = null)
    {
        _operatorPath = operatorPath ?? LocalRuntimePaths.CatalogFile();
        _shippedDirectory = shippedDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "LocalRuntime");
    }

    public async Task<LocalRuntimeCatalogLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(_operatorPath))
        {
            var text = await File
                .ReadAllTextAsync(_operatorPath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            return new LocalRuntimeCatalogLoadResult(
                LocalRuntimeCatalogParser.Parse(text),
                $"目录来源：{_operatorPath}（管理员目录优先）。");
        }

        var candidates = Directory.Exists(_shippedDirectory)
            ? Directory.GetFiles(_shippedDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();
        var parsed = new List<LocalRuntimeCatalogParseResult>();
        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            var result = LocalRuntimeCatalogParser.Parse(
                await File.ReadAllTextAsync(candidate, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false));
            errors.AddRange(result.Errors.Select(error =>
                $"{Path.GetFileName(candidate)}: {error}"));
            if (result.Catalog.Models.Count > 0 || result.Catalog.Runtimes.Count > 0)
            {
                parsed.Add(result);
            }
        }

        if (parsed.Count == 0)
        {
            return new LocalRuntimeCatalogLoadResult(
                new LocalRuntimeCatalogParseResult(
                    LocalRuntimeCatalog.Empty,
                    errors,
                    null,
                    null),
                errors.Count == 0
                    ? "本机没有可用模型目录，应用也不会自动下载任何模型。"
                    : "内置候选目录未通过完整性校验；未提供可安装条目。");
        }

        var allModels = parsed.SelectMany(result => result.Catalog.Models).ToArray();
        var allRuntimes = parsed.SelectMany(result => result.Catalog.Runtimes).ToArray();
        var conflictingModels = allModels
            .GroupBy(model => model.ModelId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        var conflictingRuntimes = allRuntimes
            .GroupBy(runtime => runtime.RuntimeId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        errors.AddRange(conflictingModels.Select(id => $"catalog-model-id-conflict:{id}"));
        errors.AddRange(conflictingRuntimes.Select(id => $"catalog-runtime-id-conflict:{id}"));

        var mergedModels = allModels
            .Where(model => !conflictingModels.Contains(model.ModelId, StringComparer.Ordinal))
            .ToArray();
        var mergedRuntimes = allRuntimes
            .Where(runtime => !conflictingRuntimes.Contains(runtime.RuntimeId, StringComparer.Ordinal))
            .ToArray();
        var labels = parsed
            .Where(result => result.Catalog.Runtimes.Count > 0)
            .Select(result =>
                string.IsNullOrWhiteSpace(result.CandidateStatus)
                    ? result.CatalogId ?? "未命名候选"
                    : $"{result.CatalogId ?? "未命名候选"}（{result.CandidateStatus}）")
            .ToArray();
        return new LocalRuntimeCatalogLoadResult(
            new LocalRuntimeCatalogParseResult(
                new LocalRuntimeCatalog(mergedModels, mergedRuntimes),
                errors,
                string.Join(", ", labels),
                parsed.Select(result => result.CandidateStatus)
                    .FirstOrDefault(status => status is not null)),
            "目录来源：应用内置候选"
            + (labels.Length == 0 ? "。" : $"（{string.Join("；", labels)}）。"));
    }
}
