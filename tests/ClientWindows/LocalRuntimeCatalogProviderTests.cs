using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Pim.Client.App.Services;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class LocalRuntimeCatalogProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-catalog-provider-" + Guid.NewGuid().ToString("N"));

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
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task DuplicateBundledIds_FailClosedAndRemoveAmbiguousEntries()
    {
        var shipped = Path.Combine(_root, "shipped");
        Directory.CreateDirectory(shipped);
        File.WriteAllText(
            Path.Combine(shipped, "a.json"),
            CatalogJson("catalog-a", "runtime-shared", "model-shared"));
        File.WriteAllText(
            Path.Combine(shipped, "b.json"),
            CatalogJson("catalog-b", "runtime-shared", "model-shared"));

        var provider = new LocalRuntimeCatalogProvider(
            Path.Combine(_root, "missing-operator.json"),
            shipped);

        var loaded = await provider.LoadAsync();

        Assert.Empty(loaded.Parse.Catalog.Runtimes);
        Assert.Empty(loaded.Parse.Catalog.Models);
        Assert.Contains(
            "catalog-runtime-id-conflict:runtime-shared",
            loaded.Parse.Errors);
        Assert.Contains(
            "catalog-model-id-conflict:model-shared",
            loaded.Parse.Errors);
    }

    private static string CatalogJson(
        string catalogueId,
        string runtimeId,
        string modelId)
    {
        var digest = new string('a', 64);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            catalogueId,
            candidateStatus = "synthetic-test-only",
            runtimes = new[]
            {
                new
                {
                    runtimeId,
                    displayName = runtimeId,
                    artifactFileName = "runtime.zip",
                    sourceUrl = "https://example.test/runtime.zip",
                    licenseId = "test",
                    licenseUrl = "https://example.test/license",
                    revision = "test-revision",
                    sha256 = digest,
                    sizeBytes = 1024,
                    archiveKind = "zip",
                    executablePath = "bin/runner.exe",
                    files = new[]
                    {
                        new
                        {
                            path = "bin/runner.exe",
                            sizeBytes = 512,
                            sha256 = digest,
                        },
                    },
                    execution = new
                    {
                        arguments = new[] { "--input", "{inputPath}" },
                        timeoutSeconds = 30,
                        outputFormat = "plain-text",
                    },
                },
            },
            models = new[]
            {
                new
                {
                    modelId,
                    displayName = modelId,
                    runtimeId,
                    licenseId = "test",
                    licenseUrl = "https://example.test/license",
                    sourceUrl = "https://example.test/model.bin",
                    revision = "test-revision",
                    sha256 = digest,
                    sizeBytes = 1024,
                    artifactFileName = "model.bin",
                    resources = new
                    {
                        cpuCores = 1,
                        memoryBytes = 1024,
                        gpuRequired = false,
                    },
                },
            },
        });
    }
}
