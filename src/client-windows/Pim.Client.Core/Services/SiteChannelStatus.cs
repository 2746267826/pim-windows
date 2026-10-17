namespace Pim.Client.Core.Services;

/// <summary>站点**接收**通道（浏览器扩展 → 本机桥）的当前状态。这是入站新鲜度，**不是**上传结果。</summary>
/// <param name="RecentBatch">最近一批入站是否落在判定窗口内。</param>
/// <param name="SecondsSinceLastBatch">距最近一批入站的秒数；从未收到过为 null。</param>
/// <param name="Text">给状态窗口/诊断用的整行文本（已含口径说明）。</param>
public sealed record SiteChannelInboundStatus(bool RecentBatch, double? SecondsSinceLastBatch, string Text);

/// <summary>站点**上传**通道（本机 → 服务端）的当前状态。这是出站事实，与入站新鲜度无关。</summary>
/// <param name="InSync">收 = 传 + 弃 且无错误（即没有还没了结的事件）。</param>
/// <param name="PendingCount">还没了结的事件数（收 − 传 − 弃，下界 0）。</param>
/// <param name="Error">最近一次上传错误；无错误为 null。</param>
/// <param name="Text">给状态窗口/诊断用的整行文本。</param>
public sealed record SiteChannelUploadStatus(bool InSync, long PendingCount, string? Error, string Text);

/// <summary>
/// 站点通道口径（工单 06 / AC-5 / G3）：
/// 把过去压在一行里的两件事拆成**两条独立事实** —— 「站点接收（扩展 → 本机）」与「站点上传（本机 → 服务端）」。
///
/// <para>
/// 为什么必须拆（根因，已由维护者三方同时刻观测确认，2026-10-16 12:26–12:28 CST，设备 `DESKTOP-ARJ75IN`）：
/// 旧显示 `站点通道: 未连接 收=112 传=112 弃=0 最近=159s前 错误=无` 里的"未连接"**只**来自
/// "最后一批**入站**是 159 秒前"（`BrowserBridgeService.IsSiteConnected`，硬编码 120 秒窗口），
/// 而出站侧那一刻是 112/112 全成功、0 丢弃、无错误。
/// 两件事被压进同一句，于是"事件在传"与"显示未连接"可以同时为真 —— 这就是候选① 语义错位。
/// </para>
///
/// <para>
/// 因此本类**不改** <c>SiteConnected</c> 的原语义（它是入站新鲜度），只是把它照实说成
/// "浏览器侧当前没有活动"，并把出站结果单独说清楚。修文案不是修法的一半 —— 拆开事实才是。
/// </para>
/// </summary>
public static class SiteChannelStatusFormatter
{
    /// <summary>
    /// 入站"还在活动"的判定窗口（秒）。
    ///
    /// <para>
    /// **依据（实测，不是拍的）**：维护者 2026-10-16 的真机日志里，站点级 fork 扩展的刷批节拍
    /// 实测为 **10–30 秒**（12:22:55 / 12:23:12 / 12:23:27 / 12:23:45 …），
    /// 120 秒 ≈ 该节拍的 **4–12 倍** 余量；而且浏览器不在前台/没在跑时**本来就不产生新批次**。
    /// 所以"超过窗口"的正确语义是**浏览器侧当前没有活动**，不是"通道断了"、更不是"上传失败"。
    /// </para>
    /// </summary>
    public const double InboundActivityWindowSeconds = 120.0;

    /// <summary>
    /// 描述站点**接收**通道。
    /// 文案刻意避开"连接断开/未连接"这类说法：入站静默是浏览器侧的活动状态，不是故障结论。
    /// </summary>
    public static SiteChannelInboundStatus DescribeInbound(
        double? secondsSinceLastBatch,
        bool? browserConnected = null,
        double? browserHeartbeatAgeSeconds = null)
    {
        var hint = DescribeBrowserHeartbeatHint(browserConnected, browserHeartbeatAgeSeconds);

        if (secondsSinceLastBatch is not { } age)
        {
            return new SiteChannelInboundStatus(
                RecentBatch: false,
                SecondsSinceLastBatch: null,
                Text: "站点接收: 尚未收到站点批次（浏览器侧可能没在跑，或没装站点级扩展）" + hint);
        }

        if (age <= InboundActivityWindowSeconds)
        {
            return new SiteChannelInboundStatus(
                RecentBatch: true,
                SecondsSinceLastBatch: age,
                Text: $"站点接收: 最近一批 {age:F0}s 前 · 浏览器侧有活动" + hint);
        }

        return new SiteChannelInboundStatus(
            RecentBatch: false,
            SecondsSinceLastBatch: age,
            Text: $"站点接收: 最近一批 {age:F0}s 前 · 已超过 {InboundActivityWindowSeconds:F0}s 判定窗口 "
                  + "→ 浏览器侧当前无活动（不是通道故障，上传状态看下一行）" + hint);
    }

    /// <summary>
    /// 描述站点**上传**通道。判据是账目而不是感觉：<c>收 = 传 + 弃</c> 且无错误才算"已同步"，
    /// 否则给出落后条数或错误原因。没有落库/没有上传时间戳之类的猜测在这里一律不出现。
    /// </summary>
    public static SiteChannelUploadStatus DescribeUpload(
        long received,
        long uploaded,
        long dropped,
        string? lastUploadError)
    {
        var pending = Math.Max(0, received - uploaded - dropped);
        var accounts = $"收={received} 传={uploaded} 弃={dropped}";

        if (!string.IsNullOrWhiteSpace(lastUploadError))
        {
            return new SiteChannelUploadStatus(
                InSync: false,
                PendingCount: pending,
                Error: lastUploadError,
                Text: $"站点上传: 失败 —— {lastUploadError} · 待上传 {pending} 条（{accounts}）");
        }

        if (pending > 0)
        {
            return new SiteChannelUploadStatus(
                InSync: false,
                PendingCount: pending,
                Error: null,
                Text: $"站点上传: 待上传 {pending} 条（{accounts}）");
        }

        return new SiteChannelUploadStatus(
            InSync: true,
            PendingCount: 0,
            Error: null,
            Text: $"站点上传: 已同步（{accounts}，无错误）");
    }

    /// <summary>可选上下文：桥的浏览器心跳 —— 用来区分"浏览器没在跑/不在前台"与"桥/通道出问题"。</summary>
    private static string DescribeBrowserHeartbeatHint(bool? browserConnected, double? browserHeartbeatAgeSeconds)
    {
        if (browserHeartbeatAgeSeconds is { } age)
        {
            return $" · 浏览器心跳 {age:F0}s 前";
        }

        return browserConnected switch
        {
            true => " · 浏览器心跳: 已连接",
            false => " · 浏览器心跳: 无（浏览器扩展可能没在运行）",
            _ => string.Empty,
        };
    }
}
