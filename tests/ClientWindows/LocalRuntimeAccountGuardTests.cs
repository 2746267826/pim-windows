using System;
using System.Threading;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class LocalRuntimeAccountGuardTests
{
    [Fact]
    public void Run_IsPinnedToStartingAccount()
    {
        var guard = new LocalRuntimeAccountGuard();
        _ = guard.BeginRun("account-a");

        Assert.Throws<InvalidOperationException>(() => guard.BeginRun("account-b"));
    }

    [Fact]
    public async Task AccountSwitch_CancelsRunAndRevokesCapability()
    {
        var guard = new LocalRuntimeAccountGuard();
        var runToken = guard.BeginRun("account-a");
        guard.RegisterCapability("account-a", new[] { "model-a" });

        var result = await guard.SwitchAccountAsync("account-b");

        Assert.True(runToken.IsCancellationRequested);
        Assert.True(result.ActiveRunCancelled);
        Assert.True(result.CapabilityRevoked);
        Assert.Null(guard.Capability);
        Assert.Equal("account-b", guard.CurrentAccountId);
    }

    [Fact]
    public async Task SameAccountSwitch_DoesNotCancelRun()
    {
        var guard = new LocalRuntimeAccountGuard();
        var runToken = guard.BeginRun("account-a");
        guard.RegisterCapability("account-a", new[] { "model-a" });

        var result = await guard.SwitchAccountAsync("account-a");

        Assert.False(runToken.IsCancellationRequested);
        Assert.False(result.ActiveRunCancelled);
        Assert.False(result.CapabilityRevoked);
        Assert.NotNull(guard.Capability);
    }

    [Fact]
    public async Task Logout_RevokesCapabilityAndCancelsRun()
    {
        var guard = new LocalRuntimeAccountGuard();
        var runToken = guard.BeginRun("account-a");
        guard.RegisterCapability("account-a", new[] { "model-a" });

        await guard.SwitchAccountAsync(null);

        Assert.True(runToken.IsCancellationRequested);
        Assert.Null(guard.CurrentAccountId);
        Assert.Null(guard.Capability);
    }
}
