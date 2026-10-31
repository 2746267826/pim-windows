using System;
using System.IO;
using System.Threading.Tasks;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class LocalRuntimeResourceBackoffTests
{
    [Fact]
    public void CpuOnlyModel_DoesNotRequireGpu()
    {
        var requirements = new LocalResourceRequirements(CpuCores: 4, MemoryBytes: 1024);
        var snapshot = new ResourceSnapshot(
            DateTimeOffset.UtcNow,
            LogicalProcessorCount: 8,
            CpuLoadPercent: 20,
            AvailableMemoryBytes: 2048,
            VramAvailableBytes: null,
            IsOnAcPower: null,
            BatteryPercent: null);

        var decision = ResourceRequirementEvaluator.Evaluate(requirements, snapshot);

        Assert.True(decision.Satisfied);
    }

    [Fact]
    public void RequiredButUnknownResources_FailClosed()
    {
        var requirements = new LocalResourceRequirements(
            CpuCores: 4,
            MemoryBytes: 1024,
            GpuRequired: true,
            VramBytes: 1024,
            RequireAcPower: true);
        var snapshot = ResourceSnapshot.Unknown(DateTimeOffset.UtcNow);

        var decision = ResourceRequirementEvaluator.Evaluate(requirements, snapshot);

        Assert.False(decision.Satisfied);
        Assert.Contains(LocalRuntimeReasonCodes.ResourceCpuUnknown, decision.Reasons);
        Assert.Contains(LocalRuntimeReasonCodes.ResourceMemoryUnknown, decision.Reasons);
        Assert.Contains(LocalRuntimeReasonCodes.ResourceVramUnknown, decision.Reasons);
        Assert.Contains(LocalRuntimeReasonCodes.ResourcePowerUnknown, decision.Reasons);
    }

    [Fact]
    public void InsufficientMeasuredResources_AreReported()
    {
        var requirements = new LocalResourceRequirements(
            CpuCores: 16,
            MemoryBytes: 8L * 1024,
            GpuRequired: true,
            VramBytes: 8L * 1024,
            RequireAcPower: true);
        var snapshot = new ResourceSnapshot(
            DateTimeOffset.UtcNow,
            LogicalProcessorCount: 8,
            CpuLoadPercent: 50,
            AvailableMemoryBytes: 1024,
            VramAvailableBytes: 1024,
            IsOnAcPower: false,
            BatteryPercent: 40);

        var decision = ResourceRequirementEvaluator.Evaluate(requirements, snapshot);

        Assert.False(decision.Satisfied);
        Assert.Contains(LocalRuntimeReasonCodes.ResourceCpuInsufficient, decision.Reasons);
        Assert.Contains(LocalRuntimeReasonCodes.ResourceMemoryInsufficient, decision.Reasons);
        Assert.Contains(LocalRuntimeReasonCodes.ResourceVramInsufficient, decision.Reasons);
        Assert.Contains(LocalRuntimeReasonCodes.ResourceOnBattery, decision.Reasons);
    }

    [Fact]
    public async Task Backoff_StopsAfterMaximumAutoAttempts_AndResetsOnSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "pim-backoff-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-10-31T08:00:00Z");
        var store = new RuntimeBackoffStore(
            root,
            new RuntimeBackoffPolicy(MaxAutoAttempts: 3, BaseDelaySeconds: 10, MaxDelaySeconds: 60),
            () => now);
        try
        {
            var first = await store.RecordResourceFailureAsync(
                "account-a",
                "model-a",
                new[] { LocalRuntimeReasonCodes.ResourceMemoryInsufficient });
            Assert.False(first.ManualRequired);
            Assert.Equal(now.AddSeconds(10), first.NextAttemptAtUtc);

            await store.RecordResourceFailureAsync("account-a", "model-a", new[] { "r1" });
            var third = await store.RecordResourceFailureAsync("account-a", "model-a", new[] { "r2" });
            Assert.True(third.ManualRequired);
            Assert.Null(third.NextAttemptAtUtc);

            var decision = await store.EvaluateAsync("account-a", "model-a", now.AddHours(1));
            Assert.False(decision.ShouldAutoAttempt);
            Assert.True(decision.ManualRequired);

            var reset = await store.RecordSuccessAsync("account-a", "model-a");
            Assert.Equal(0, reset.AutoAttempts);
            Assert.False(reset.ManualRequired);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Backoff_RespectsNextAttemptTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "pim-backoff-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-10-31T08:00:00Z");
        var store = new RuntimeBackoffStore(
            root,
            new RuntimeBackoffPolicy(MaxAutoAttempts: 5, BaseDelaySeconds: 30, MaxDelaySeconds: 60),
            () => now);
        try
        {
            await store.RecordResourceFailureAsync("account-a", "model-a", new[] { "r" });

            Assert.False((await store.EvaluateAsync("account-a", "model-a", now.AddSeconds(10))).ShouldAutoAttempt);
            Assert.True((await store.EvaluateAsync("account-a", "model-a", now.AddSeconds(31))).ShouldAutoAttempt);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
