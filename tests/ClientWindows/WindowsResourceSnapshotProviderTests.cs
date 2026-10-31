using System;
using System.Threading.Tasks;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.Client.App.Tests;

public class WindowsResourceSnapshotProviderTests
{
    [Fact]
    public async Task SecondSampleHasMeasuredCpuLoad_AndVramStaysExplicitlyUnknown()
    {
        var provider = new WindowsResourceSnapshotProvider();

        var first = await provider.GetSnapshotAsync();
        await Task.Delay(250);
        var second = await provider.GetSnapshotAsync();

        Assert.True(first.LogicalProcessorCount > 0);
        Assert.True(first.AvailableMemoryBytes > 0);
        Assert.NotNull(second.CpuLoadPercent);
        Assert.InRange(second.CpuLoadPercent!.Value, 0, 100);
        // GPU-only data is intentionally unavailable; a GPU-required model must
        // fail closed rather than assume VRAM.
        Assert.Null(second.VramAvailableBytes);
    }
}
