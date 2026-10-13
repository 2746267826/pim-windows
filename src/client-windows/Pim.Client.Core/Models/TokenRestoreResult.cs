namespace Pim.Client.Core.Models;

/// <summary>
/// 令牌恢复结果（AuthService 的内部状态机输出，不是接口 DTO）。
///
/// 注意：原先与本类型同文件的 <c>ApiResponse&lt;T&gt;</c> / <c>AuthResponse</c> / <c>UserInfo</c>
/// 等**接口 DTO 已由契约生成**（<c>Pim.Client.Core.Contract</c>，见
/// <c>scripts/contract/generate-csharp.py</c>）；<c>CalendarResponse</c> / <c>EventResponse</c> /
/// <c>TaskResponse</c> / <c>SearchResult</c> / <c>PagedResult</c> 是**没有任何调用点的死声明**，
/// 已随本次接口边界契约化删除 —— 本仓没有任何 calendar/search operation 调用点。
/// </summary>
public enum TokenRestoreResult
{
    NoSavedToken,
    Success,
    PendingNetwork,
    InvalidCredentials
}
