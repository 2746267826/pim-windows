using System;
using System.Linq;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 工单 06 / AC-5（G3）：站点通道口径修复。
///
/// 根因已由维护者的三方同时刻观测确认（2026-10-16 12:26–12:28 CST，设备 `DESKTOP-ARJ75IN`）：
/// 状态窗口那一行的"未连接"**只**来自"最后一批**入站**是 159s 前"（超过硬编码 120s），
/// 而出站侧 112/112 全部成功、0 丢弃、无错误 —— 两件事被压进同一行文字，
/// 于是"事件在传"与"显示未连接"同时为真（候选① 语义错位）。
///
/// 修复口径：把一行拆成两条**各自独立**的事实，且都不使用"连接断开"这种会误导的说法：
/// <list type="number">
///   <item><description>「站点接收（扩展 → 本机）」= 入站新鲜度；超过判定窗口的语义是
///     <b>浏览器侧当前没有活动</b>（浏览器不在前台/没在跑时本来就不产生新批次），不是故障；</description></item>
///   <item><description>「站点上传（本机 → 服务端）」= 出站结果；<c>传 + 弃 == 收</c> 且无错误即"已同步"，
///     否则显示落后条数或错误原因。</description></item>
/// </list>
///
/// 证据等级：本文件是纯逻辑 + 源码形态（Linux 可跑）；状态窗口的实际渲染仍需 Windows 真机验收。
/// </summary>
public class SiteChannelStatusTests
{
    /// <summary>
    /// 方向①（**维护者的真实案例**）：入站静默 159s、收=112 传=112 弃=0、无错误。
    /// 修复后必须**同时**表达"接收静默"与"上传正常"，不得再出现旧的那种"未连接"式误导。
    /// </summary>
    [Fact]
    public void RealIncident_InboundSilentButUploadHealthy_StatesBothFacts()
    {
        var inbound = SiteChannelStatusFormatter.DescribeInbound(
            secondsSinceLastBatch: 159,
            browserConnected: true,
            browserHeartbeatAgeSeconds: 8);
        var upload = SiteChannelStatusFormatter.DescribeUpload(
            received: 112,
            uploaded: 112,
            dropped: 0,
            lastUploadError: null);

        // 入站：说"浏览器侧当前无活动"，不说"断开/未连接"。
        Assert.Contains("浏览器侧当前无活动", inbound.Text);
        Assert.Contains("159s", inbound.Text);
        Assert.DoesNotContain("连接断开", inbound.Text);
        Assert.DoesNotContain("未连接", inbound.Text);
        Assert.False(inbound.RecentBatch);

        // 出站：同一时刻必须是"已同步"（这正是旧显示漏掉的那一半事实）。
        Assert.True(upload.InSync);
        Assert.Equal(0, upload.PendingCount);
        Assert.Null(upload.Error);
        Assert.Contains("已同步", upload.Text);
        Assert.Contains("112", upload.Text);
    }

    /// <summary>方向②：入站活跃 + 出站持续失败 → 必须显示失败原因（而不是只显示"已连接"）。</summary>
    [Fact]
    public void ActiveInboundWithFailingUpload_ShowsTheFailureReason()
    {
        var inbound = SiteChannelStatusFormatter.DescribeInbound(12, browserConnected: true, browserHeartbeatAgeSeconds: 3);
        var upload = SiteChannelStatusFormatter.DescribeUpload(
            received: 200,
            uploaded: 112,
            dropped: 0,
            lastUploadError: "HTTP 500 Internal Server Error: upload rejected");

        Assert.True(inbound.RecentBatch);
        Assert.Contains("有活动", inbound.Text);

        Assert.False(upload.InSync);
        Assert.Contains("HTTP 500", upload.Text);
        Assert.Contains("待上传 88 条", upload.Text);
        Assert.Equal(88, upload.PendingCount);
    }

    /// <summary>方向③：出站队列不排空（没有错误，但落后）→ 必须显示落后条数。</summary>
    [Fact]
    public void NotYetDrainedBacklog_ShowsPendingCount()
    {
        var upload = SiteChannelStatusFormatter.DescribeUpload(received: 120, uploaded: 100, dropped: 5, lastUploadError: null);

        Assert.False(upload.InSync);
        Assert.Equal(15, upload.PendingCount);
        Assert.Contains("待上传 15 条", upload.Text);
        Assert.Null(upload.Error);
    }

    /// <summary>被丢弃的事件算"已了结"：收 = 传 + 弃 就是已同步，不该永远显示落后。</summary>
    [Fact]
    public void DroppedEventsCountAsSettled()
    {
        var upload = SiteChannelStatusFormatter.DescribeUpload(received: 112, uploaded: 100, dropped: 12, lastUploadError: null);

        Assert.True(upload.InSync);
        Assert.Equal(0, upload.PendingCount);
        Assert.Contains("已同步", upload.Text);
    }

    /// <summary>从来没收到过批次：如实说"尚未收到"，不编一个"已连接"或"未连接"。</summary>
    [Fact]
    public void NeverReceivedAnything_IsHonest()
    {
        var inbound = SiteChannelStatusFormatter.DescribeInbound(null, browserConnected: false, browserHeartbeatAgeSeconds: null);

        Assert.Null(inbound.SecondsSinceLastBatch);
        Assert.Contains("尚未收到", inbound.Text);
        Assert.DoesNotContain("未连接", inbound.Text);
    }

    /// <summary>
    /// 阈值依据必须写进代码注释（维护者要求）：判定窗口 120s 不是拍的 ——
    /// 实测 fork 扩展刷批节拍是 10–30 秒，120s ≈ 4–12 倍余量；超过它的语义是"浏览器侧无活动"。
    /// </summary>
    [Fact]
    public void InboundWindowIsDocumentedWithItsMeasuredBasis()
    {
        var source = RepoSource("src", "client-windows", "Pim.Client.Core", "Services", "SiteChannelStatus.cs");

        Assert.Equal(120.0, SiteChannelStatusFormatter.InboundActivityWindowSeconds);
        Assert.Contains("实测", source);
        Assert.Contains("30 秒", source);
        Assert.Contains("4–12", source);
    }

    /// <summary>
    /// 状态窗口必须渲染成两条独立事实（并且不再出现旧的那种"站点通道: 已连接/未连接"混合口径）。
    /// </summary>
    [Fact]
    public void StatusWindow_RendersReceiveAndUploadAsTwoFacts()
    {
        var code = RepoSource("src", "client-windows", "Pim.Client.App", "StatusWindow.xaml.cs");

        // 两条事实都要**真的渲染出来**（不是只算出来）：
        // 窗口正文里必须出现 formatter 产出的两行，而不是把它们丢掉。
        Assert.Contains("{siteChannel.Inbound.Text}", code);
        Assert.Contains("{siteChannel.Upload.Text}", code);
        // 窗口只依赖这一个来源（两处都走 BuildSiteChannelStatus，不各写一份口径）。
        Assert.Contains("BuildSiteChannelStatus", code);
        Assert.Contains("SiteChannelStatusFormatter.DescribeInbound", code);
        Assert.Contains("SiteChannelStatusFormatter.DescribeUpload", code);
        // 诊断报告里也要带上判定窗口的依据（维护者要求把阈值依据写进代码与描述）。
        Assert.Contains("InboundActivityWindowSeconds", code);
        // 旧口径：把"入站新鲜度"和上传计数压进同一句「站点通道: 已连接/未连接」。
        Assert.DoesNotContain("站点通道:", code);
    }

    private static string RepoSource(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = System.IO.Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (System.IO.File.Exists(candidate))
            {
                return System.IO.File.ReadAllText(candidate);
            }

            current = System.IO.Directory.GetParent(current)?.FullName;
        }

        throw new System.IO.FileNotFoundException(System.IO.Path.Combine(parts));
    }
}
