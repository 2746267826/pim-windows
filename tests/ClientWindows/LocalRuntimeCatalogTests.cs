using System;
using System.IO;
using System.Linq;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class LocalRuntimeCatalogTests
{
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Parse_ValidCatalog_ReturnsModelsAndRuntimes()
    {
        var json = $$"""
        {
          "schemaVersion": 1,
          "runtimes": [
            {
              "runtimeId": "whisper-cpp-cpu",
              "displayName": "whisper.cpp CPU",
              "artifactFileName": "whisper-bin-x64.zip",
              "sourceUrl": "https://example.invalid/whisper-bin-x64.zip",
              "licenseId": "MIT",
              "licenseUrl": "https://example.invalid/LICENSE",
              "revision": "v1.0.0",
              "sha256": "{{ShaB}}",
              "sizeBytes": 4096,
              "archiveKind": "zip",
              "executablePath": "whisper-cli.exe",
              "files": [
                { "path": "whisper-cli.exe", "sizeBytes": 1024, "sha256": "{{ShaB}}" },
                { "path": "whisper.dll", "sizeBytes": 2048, "sha256": "{{ShaA}}" }
              ],
              "execution": {
                "arguments": [
                  "--model", "{modelPath}",
                  "--file", "{inputPath}",
                  "--language", "{language}",
                  "--output-json"
                ],
                "timeoutSeconds": 300,
                "outputFormat": "whisper.cpp-json"
              }
            }
          ],
          "models": [
            {
              "modelId": "whisper-base-cpu",
              "displayName": "Whisper base (CPU)",
              "runtimeId": "whisper-cpp-cpu",
              "licenseId": "MIT",
              "licenseUrl": "https://example.invalid/LICENSE",
              "sourceUrl": "https://example.invalid/ggml-base.bin",
              "revision": "r1",
              "sha256": "{{ShaA}}",
              "sizeBytes": 1024,
              "artifactFileName": "ggml-base.bin",
              "resources": { "cpuCores": 2, "memoryBytes": 1048576, "gpuRequired": false }
            }
          ]
        }
        """;

        var result = LocalRuntimeCatalogParser.Parse(json);

        Assert.Empty(result.Errors);
        Assert.Single(result.Catalog.Models);
        Assert.Single(result.Catalog.Runtimes);
        Assert.True(result.Catalog.Models[0].HasVerifiedArtifact);
        Assert.True(result.Catalog.Runtimes[0].HasVerifiedRuntime);
        Assert.Equal(LocalRuntimeArtifactKind.Zip, result.Catalog.Runtimes[0].ArtifactKind);
        Assert.Equal(2, result.Catalog.Runtimes[0].RequiredFiles.Count);
        Assert.Equal("whisper-cli.exe", result.Catalog.Runtimes[0].ExecutableRelativePath);
        Assert.Equal(2, result.Catalog.Models[0].Resources.CpuCores);
    }

    [Fact]
    public void Parse_EmptyDigest_IsListedButNotInstallable()
    {
        var json = """
        {
          "schemaVersion": 1,
          "models": [
            {
              "modelId": "candidate-model",
              "displayName": "Candidate",
              "runtimeId": "unknown",
              "licenseId": "MIT",
              "sourceUrl": "https://example.invalid/model.bin",
              "revision": "r1",
              "sha256": "",
              "sizeBytes": 1024,
              "artifactFileName": "model.bin"
            }
          ]
        }
        """;

        var result = LocalRuntimeCatalogParser.Parse(json);

        Assert.Empty(result.Errors);
        var model = Assert.Single(result.Catalog.Models);
        Assert.False(model.HasVerifiedArtifact);
    }

    [Fact]
    public void Parse_RejectsUnsafeAndInconsistentEntries()
    {
        var json = """
        {
          "schemaVersion": 1,
          "models": [
            { "modelId": "Bad Id", "sourceUrl": "https://example.invalid/a", "sizeBytes": 1, "artifactFileName": "a.bin" },
            { "modelId": "http-source", "sourceUrl": "http://example.invalid/a", "sizeBytes": 1, "artifactFileName": "a.bin" },
            { "modelId": "path-escape", "sourceUrl": "https://example.invalid/a", "sizeBytes": 1, "artifactFileName": "../evil.bin" },
            { "modelId": "bad-sha", "sourceUrl": "https://example.invalid/a", "sizeBytes": 1, "artifactFileName": "a.bin", "sha256": "xyz" },
            { "modelId": "vram-no-gpu", "sourceUrl": "https://example.invalid/a", "sizeBytes": 1, "artifactFileName": "a.bin", "resources": { "gpuRequired": false, "vramBytes": 1024 } },
            { "modelId": "duplicate", "sourceUrl": "https://example.invalid/a", "sizeBytes": 1, "artifactFileName": "a.bin" },
            { "modelId": "duplicate", "sourceUrl": "https://example.invalid/b", "sizeBytes": 1, "artifactFileName": "b.bin" }
          ]
        }
        """;

        var result = LocalRuntimeCatalogParser.Parse(json);

        Assert.Single(result.Catalog.Models);
        Assert.Equal("duplicate", result.Catalog.Models[0].ModelId);
        Assert.Equal(6, result.Errors.Count);
        Assert.Contains(result.Errors, error => error.StartsWith("model-id-invalid", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("source must be an absolute https URL", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.StartsWith("model-artifact-invalid", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.StartsWith("model-sha-invalid", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("vram declared without gpuRequired", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.StartsWith("model-duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsUnknownSchema()
    {
        var result = LocalRuntimeCatalogParser.Parse("""{ "schemaVersion": 99 }""");

        Assert.Empty(result.Catalog.Models);
        Assert.Contains(result.Errors, error => error.StartsWith("catalog-schema-unsupported", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsRuntimeWithUnsafeOrIncompleteExecution()
    {
        var json = $$"""
        {
          "schemaVersion": 1,
          "runtimes": [
            {
              "runtimeId": "traversal",
              "artifactFileName": "runtime.zip",
              "sourceUrl": "https://example.invalid/runtime.zip",
              "revision": "r1",
              "sha256": "{{ShaB}}",
              "sizeBytes": 4096,
              "archiveKind": "zip",
              "executablePath": "../evil.exe",
              "files": [
                { "path": "../evil.exe", "sizeBytes": 1, "sha256": "{{ShaA}}" }
              ],
              "execution": { "arguments": [ "{inputPath}" ], "timeoutSeconds": 30 }
            },
            {
              "runtimeId": "missing-dll",
              "artifactFileName": "runtime.zip",
              "sourceUrl": "https://example.invalid/runtime.zip",
              "revision": "r1",
              "sha256": "{{ShaB}}",
              "sizeBytes": 4096,
              "archiveKind": "zip",
              "executablePath": "runner.exe",
              "files": [
                { "path": "runner.exe", "sizeBytes": 1, "sha256": "{{ShaA}}" }
              ],
              "execution": { "arguments": [ "{unknown}" ], "timeoutSeconds": 30 }
            }
          ]
        }
        """;

        var result = LocalRuntimeCatalogParser.Parse(json);

        Assert.Empty(result.Catalog.Runtimes);
        Assert.Contains(result.Errors, error => error.StartsWith("runtime-executable-invalid", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.StartsWith("runtime-arguments-invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveContained_RejectsEscapesAndAcceptsChildren()
    {
        var root = Path.Combine(Path.GetTempPath(), "pim-path-root");

        Assert.NotNull(PathSafety.ResolveContained(root, "models/a.bin"));
        Assert.Null(PathSafety.ResolveContained(root, "../a.bin"));
        Assert.Null(PathSafety.ResolveContained(root, "models/../../a.bin"));
        Assert.Null(PathSafety.ResolveContained(root, Path.Combine(Path.GetTempPath(), "a.bin")));
    }

    [Fact]
    public void Parse_BundledWhisperCppPrereleaseCatalog_IsVerifiedButExplicitlyPrerelease()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "client-windows",
            "Pim.Client.App",
            "LocalRuntime",
            "whisper-cpp-b5454-cpu-x64.json");
        var json = File.ReadAllText(path);

        var result = LocalRuntimeCatalogParser.Parse(json);

        Assert.Empty(result.Errors);
        Assert.Equal("whisper-cpp-b5454-cpu-x64-prerelease", result.CatalogId);
        Assert.Contains(
            "prerelease",
            result.CandidateStatus,
            StringComparison.Ordinal);
        Assert.Contains(
            "not-verified",
            result.CandidateStatus,
            StringComparison.Ordinal);

        var runtime = Assert.Single(result.Catalog.Runtimes);
        Assert.Equal("whisper-cpp-b5454-cpu-x64", runtime.RuntimeId);
        Assert.True(runtime.HasVerifiedRuntime);
        Assert.Contains(
            "b5454",
            runtime.Revision,
            StringComparison.Ordinal);
        Assert.Equal("MIT", runtime.LicenseId);
        Assert.StartsWith("https://github.com/ggml-org/whisper.cpp/", runtime.SourceUrl, StringComparison.Ordinal);

        var executable = Assert.Single(
            runtime.RequiredFiles,
            file => string.Equals(
                file.RelativePath,
                "Release/whisper-cli.exe",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(executable.SizeBytes > 0);
        Assert.Matches("^[0-9a-f]{64}$", executable.Sha256);
        Assert.Equal(41, runtime.RequiredFiles.Count);

        Assert.Equal(
            new[]
            {
                "--model", "{modelPath}",
                "--file", "{inputPath}",
                "--language", "{language}",
                "--no-timestamps",
            },
            runtime.Execution.Arguments);
        Assert.Equal(LocalRuntimeExecutionProfile.PlainText, runtime.Execution.OutputFormat);

        Assert.Empty(result.Catalog.Models);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "client-windows", "Pim.Client.App", "LocalRuntime", "whisper-cpp-b5454-cpu-x64.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
