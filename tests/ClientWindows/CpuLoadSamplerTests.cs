using System;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class CpuLoadSamplerTests
{
    [Fact]
    public void FirstSampleIsUnknown_SecondSampleIsRealRatio()
    {
        var readings = new[]
        {
            new CpuTimes(Idle: 1000, Kernel: 2000, User: 3000),
            new CpuTimes(Idle: 1100, Kernel: 2200, User: 3300),
        };
        var index = 0;
        var sampler = new CpuLoadSampler(() => readings[index++]);

        Assert.Null(sampler.Sample());
        var load = sampler.Sample();

        Assert.NotNull(load);
        Assert.Equal(80, load!.Value, precision: 6);
    }

    [Fact]
    public void ZeroDeltaStaysUnknown_AndKeepsBaseline()
    {
        var readings = new[]
        {
            new CpuTimes(1000, 2000, 3000),
            new CpuTimes(1000, 2000, 3000),
            new CpuTimes(1100, 2100, 3100),
        };
        var index = 0;
        var sampler = new CpuLoadSampler(() => readings[index++]);

        Assert.Null(sampler.Sample());
        Assert.Null(sampler.Sample());
        var load = sampler.Sample();

        Assert.NotNull(load);
        Assert.Equal(50, load!.Value, precision: 6);
    }

    [Fact]
    public void ClampsIdleDeltaThatExceedsTotal()
    {
        var readings = new[]
        {
            new CpuTimes(1000, 1000, 1000),
            new CpuTimes(2000, 1100, 1100),
        };
        var index = 0;
        var sampler = new CpuLoadSampler(() => readings[index++]);

        sampler.Sample();

        Assert.Equal(0, sampler.Sample());
    }
}
