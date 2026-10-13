using System;
using Pim.Client.Core.Utils;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计整改 round5 · REQ-COM-02 / REQ-COM-06（F-10）：从**不被编译的孤儿文件**
/// <c>BusinessDayUtilsTests.cs</c> 迁移出来的 3 条**只依赖客户端类型**的用例。
///
/// 迁移口径（round4 独立 review I-1 确认的分母：16 = 可移植 10 / 后端依赖 6）：
///   - 本文件承接 3 条可移植用例（营业日边界、04:00 上海边界、下一营业日开始）；
///   - 同文件里的第 4 条 <c>PcTrackerService_GetBusinessDayForTimestamp_MatchesBusinessDayUtils</c>
///     需要后端程序集 <c>Pim.Module.PcTracker</c> 做对照，**不可移植**，归属与真实验证条件
///     记录在 <c>tests/ClientWindows/BACKEND-OWNED-CASES.md</c>（保持 NOT-VERIFIED）。
///
/// 用例名与孤儿文件逐字相同，便于对照审计分母；断言未做任何弱化。
/// </summary>
public sealed class BusinessDayUtilsClientTests
{
    [Fact]
    public void GetBusinessDate_BeforeFourAmShanghai_BelongsToPreviousCalendarDay()
    {
        // 2026-03-24 03:59:59 CST (+08:00) is 2026-03-23 19:59:59 UTC
        var ts = new DateTimeOffset(2026, 3, 24, 3, 59, 59, TimeSpan.FromHours(8));
        var businessDate = BusinessDayUtils.GetBusinessDate(ts);

        Assert.Equal(new DateOnly(2026, 3, 23), businessDate);
        Assert.Equal("2026-03-23", BusinessDayUtils.GetBusinessDateString(ts));
    }

    [Fact]
    public void GetBusinessDate_AtOrAfterFourAmShanghai_BelongsToCurrentCalendarDay()
    {
        // 2026-03-24 04:00:00 CST (+08:00) is 2026-03-23 20:00:00 UTC
        var tsAtFour = new DateTimeOffset(2026, 3, 24, 4, 0, 0, TimeSpan.FromHours(8));
        var businessDateAtFour = BusinessDayUtils.GetBusinessDate(tsAtFour);

        Assert.Equal(new DateOnly(2026, 3, 24), businessDateAtFour);
        Assert.Equal("2026-03-24", BusinessDayUtils.GetBusinessDateString(tsAtFour));

        // 2026-03-24 23:59:59 CST
        var tsLate = new DateTimeOffset(2026, 3, 24, 23, 59, 59, TimeSpan.FromHours(8));
        Assert.Equal(new DateOnly(2026, 3, 24), BusinessDayUtils.GetBusinessDate(tsLate));
    }

    [Fact]
    public void GetNextBusinessDayStart_ReturnsNextFourAmShanghaiInUtc()
    {
        // 2026-03-24 02:00:00 CST (+08:00) -> Next business day start is 2026-03-24 04:00:00 CST (2026-03-23 20:00:00 UTC)
        var ts1 = new DateTimeOffset(2026, 3, 24, 2, 0, 0, TimeSpan.FromHours(8));
        var next1 = BusinessDayUtils.GetNextBusinessDayStart(ts1);
        var expected1 = new DateTimeOffset(2026, 3, 24, 4, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        Assert.Equal(expected1, next1);

        // 2026-03-24 05:00:00 CST (+08:00) -> Next business day start is 2026-03-25 04:00:00 CST (2026-03-24 20:00:00 UTC)
        var ts2 = new DateTimeOffset(2026, 3, 24, 5, 0, 0, TimeSpan.FromHours(8));
        var next2 = BusinessDayUtils.GetNextBusinessDayStart(ts2);
        var expected2 = new DateTimeOffset(2026, 3, 25, 4, 0, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        Assert.Equal(expected2, next2);
    }
}
