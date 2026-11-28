using Pim.Client.Core.Services;
using Xunit;

namespace Pim.Client.Tests;

public sealed class CalendarEventDraftKeyTests
{
    private static readonly Guid CalendarId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Start =
        new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnchangedDraftReusesTheSameKey()
    {
        var draft = new CalendarEventDraftKey();

        var first = draft.Resolve(
            CalendarId, "设计评审", Start, Start.AddHours(1), false, " 会议室 A ", "说明");
        var retry = draft.Resolve(
            CalendarId, " 设计评审 ", Start, Start.AddHours(1), false, "会议室 A", " 说明 ");

        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, retry);
    }

    [Fact]
    public void ChangedDraftStartsANewKey()
    {
        var draft = new CalendarEventDraftKey();
        var first = draft.Resolve(
            CalendarId, "设计评审", Start, Start.AddHours(1), false, null, null);

        var renamed = draft.Resolve(
            CalendarId, "设计评审（改）", Start, Start.AddHours(1), false, null, null);
        var moved = draft.Resolve(
            CalendarId, "设计评审（改）", Start.AddHours(2), Start.AddHours(3), false, null, null);

        Assert.NotEqual(first, renamed);
        Assert.NotEqual(renamed, moved);
    }

    [Fact]
    public void ResetStartsANewKeyForTheSameDraft()
    {
        var draft = new CalendarEventDraftKey();
        var first = draft.Resolve(
            CalendarId, "设计评审", Start, Start.AddHours(1), false, null, null);

        draft.Reset();
        var afterReset = draft.Resolve(
            CalendarId, "设计评审", Start, Start.AddHours(1), false, null, null);

        Assert.NotEqual(first, afterReset);
    }
}
