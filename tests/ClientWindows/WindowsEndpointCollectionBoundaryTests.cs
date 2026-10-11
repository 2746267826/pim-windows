using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 离线排队边界（<see cref="EndpointCollectionBoundaryService"/>）。
///
/// 审计 F-21：本文件原名 <c>WindowsNotificationActionRouterTests.cs</c>，其中两条用例
/// 只镜像 <c>Core.NotificationActionRouter</c> —— 该类在生产代码里已经没有任何消费者
/// （App 侧的包装类与 DI 注册已在 round3 删除，剩下的 DI 注册在这一轮删除），
/// 属于「只镜像死类的测试」，随死类一并删除。这里保留的边界用例测试的是**另一个**
/// 类，不随 F-21 删除。
/// </summary>
public class WindowsEndpointCollectionBoundaryTests
{
    [Fact]
    public void OfflineBoundaryOnlyAllowsCollectionUploads()
    {
        var boundary = new EndpointCollectionBoundaryService();

        Assert.True(boundary.CanQueueOffline("collection-upload"));
        Assert.True(boundary.CanQueueOffline("pc-activity"));
        Assert.True(boundary.CanQueueOffline("window-context"));
        Assert.True(boundary.CanQueueOffline("upload-retry"));
        Assert.False(boundary.CanQueueOffline("task-fact-change"));
        Assert.False(boundary.CanQueueOffline("event-fact-change"));
        Assert.False(boundary.CanQueueOffline("habit-rule-change"));
        Assert.False(boundary.CanQueueOffline("confirmation-decision"));
        Assert.False(boundary.CanQueueOffline("report-edit"));
        Assert.False(boundary.CanQueueOffline("outlook-writeback"));
        Assert.False(boundary.CanQueueOffline("restore-delete-operation"));
    }
}
