using System;
using System.IO;
using System.Threading.Tasks;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class ControlledProcessRunnerCoreTests
{
    [Fact]
    public async Task MissingExecutable_FailsClosed()
    {
        var runner = new ControlledProcessRunner();
        var result = await runner.StartAsync(new RuntimeLaunchSpec(
            Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".exe"),
            Array.Empty<string>(),
            Path.GetTempPath(),
            new string('a', 64),
            TimeSpan.FromSeconds(1)));

        Assert.False(result.Succeeded);
        Assert.Equal(LocalRuntimeReasonCodes.ExecutableMissing, result.ErrorCode);
    }

    [Fact]
    public async Task InvalidDigest_NeverStarts()
    {
        var path = Path.Combine(Path.GetTempPath(), "pim-runtime-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllTextAsync(path, "binary");
        try
        {
            var runner = new ControlledProcessRunner();
            var result = await runner.StartAsync(new RuntimeLaunchSpec(
                path,
                Array.Empty<string>(),
                Path.GetTempPath(),
                "not-a-sha",
                TimeSpan.FromSeconds(1)));

            Assert.False(result.Succeeded);
            Assert.Equal(LocalRuntimeReasonCodes.RuntimeUnverified, result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DigestMismatch_NeverStarts()
    {
        var path = Path.Combine(Path.GetTempPath(), "pim-runtime-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllTextAsync(path, "binary");
        try
        {
            var runner = new ControlledProcessRunner();
            var result = await runner.StartAsync(new RuntimeLaunchSpec(
                path,
                Array.Empty<string>(),
                Path.GetTempPath(),
                new string('0', 64),
                TimeSpan.FromSeconds(1)));

            Assert.False(result.Succeeded);
            Assert.Equal(LocalRuntimeReasonCodes.ExecutableHashMismatch, result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OwnedIdentity_RequiresExactStartTimeAndPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "pim-owned");
        var identity = new OwnedProcessIdentity(
            42,
            123456789,
            1,
            Path.Combine(root, "helper.exe"));

        Assert.True(OwnedProcessIdentity.Matches(
            identity,
            42,
            123456789,
            1,
            Path.Combine(root, "HELPER.EXE")));
        Assert.False(OwnedProcessIdentity.Matches(
            identity,
            42,
            123456790,
            1,
            Path.Combine(root, "helper.exe")));
        Assert.False(OwnedProcessIdentity.Matches(
            identity,
            43,
            123456789,
            1,
            Path.Combine(root, "helper.exe")));
        Assert.False(OwnedProcessIdentity.Matches(
            identity,
            42,
            123456789,
            1,
            Path.Combine(root, "other.exe")));
    }
}
