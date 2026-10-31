using System;
using Pim.Client.App.Services;
using Pim.Client.Core.Models.LocalRuntime;
using Xunit;

namespace Pim.Client.App.Tests;

public class LocalModelSectionViewTests
{
    [Fact]
    public void FormatStatus_IncludesStateReasonSizeAndVerification()
    {
        var manifest = Manifest(verified: true);
        var status = new ModelInstallStatus(
            ModelInstallState.Corrupt,
            null,
            LocalRuntimeReasonCodes.HashMismatch,
            "digest differs",
            DateTimeOffset.UtcNow);

        var item = LocalModelSectionView.ToItem(manifest, status);

        Assert.False(item.Executable);
        Assert.True(item.Installable);
        Assert.Contains("已损坏", item.StatusText);
        Assert.Contains(LocalRuntimeReasonCodes.HashMismatch, item.StatusText);
        Assert.Contains("摘要已登记", item.StatusText);
    }

    [Fact]
    public void ToItem_UnverifiedManifest_IsNotInstallable()
    {
        var manifest = Manifest(verified: false);

        var item = LocalModelSectionView.ToItem(
            manifest,
            new ModelInstallStatus(ModelInstallState.NotInstalled, null, null, null, DateTimeOffset.UtcNow));

        Assert.False(item.Installable);
        Assert.Contains("未登记摘要", item.StatusText);
    }

    private static LocalModelManifest Manifest(bool verified) =>
        new(
            "model-a",
            "Model A",
            "runtime-a",
            "MIT",
            "https://example.invalid/LICENSE",
            "https://example.invalid/model.bin",
            "r1",
            verified ? new string('a', 64) : string.Empty,
            1024 * 1024,
            "model.bin",
            LocalResourceRequirements.None);
}
