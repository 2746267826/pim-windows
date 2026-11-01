using Pim.Client.App;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class NativePlannerUiStateTests
{
    private static readonly PlannerAccountIdentity AccountA =
        new("account-a", "https://one.example.test/api/v1");
    private static readonly PlannerAccountIdentity AccountB =
        new("account-b", "https://two.example.test/api/v1");

    [Fact]
    public void LoadedSelectionCannotWriteAfterAccountSwitch()
    {
        var state = new NativePlannerUiState();
        var generation = state.BeginLoad();
        Assert.True(state.CommitLoad(generation, AccountA));
        var fence = new PlannerDataFence(generation, AccountA);

        Assert.Equal(PlannerFenceFailure.None, state.CheckWrite(fence, AccountA));
        Assert.Equal(PlannerFenceFailure.AccountChanged, state.CheckWrite(fence, AccountB));
    }

    [Fact]
    public void StartingAnotherLoadInvalidatesOldCalendarAndTaskChoices()
    {
        var state = new NativePlannerUiState();
        var first = state.BeginLoad();
        Assert.True(state.CommitLoad(first, AccountA));
        var oldFence = new PlannerDataFence(first, AccountA);

        var second = state.BeginLoad();

        Assert.Equal(PlannerFenceFailure.StaleData, state.CheckWrite(oldFence, AccountA));
        Assert.True(state.CommitLoad(second, AccountA));
        Assert.Equal(
            PlannerFenceFailure.None,
            state.CheckWrite(new PlannerDataFence(second, AccountA), AccountA));
    }

    [Fact]
    public void LateOlderLoadCannotReplaceNewerLoad()
    {
        var state = new NativePlannerUiState();
        var older = state.BeginLoad();
        var newer = state.BeginLoad();

        Assert.False(state.CommitLoad(older, AccountA));
        Assert.True(state.CommitLoad(newer, AccountA));
        Assert.Equal(
            PlannerFenceFailure.StaleData,
            state.CheckWrite(new PlannerDataFence(older, AccountA), AccountA));
    }

    [Fact]
    public void ScopeInvalidationBlocksInFlightAndLoadedWrites()
    {
        var state = new NativePlannerUiState();
        var generation = state.BeginLoad();
        Assert.True(state.CommitLoad(generation, AccountA));
        var fence = new PlannerDataFence(generation, AccountA);

        state.Invalidate();

        Assert.False(state.IsLoadCurrent(generation, AccountA));
        Assert.Equal(PlannerFenceFailure.StaleData, state.CheckWrite(fence, AccountA));
        Assert.Equal(PlannerFenceFailure.NoAccount, state.CheckWrite(fence, null));
    }
}
