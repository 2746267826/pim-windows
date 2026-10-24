namespace Pim.Client.Core.Services;

/// <summary>
/// 凭据文件的字节级保护（Windows 生产实现：DPAPI CurrentUser）。
/// 注入点只服务两件事：测试模拟 DPAPI 故障、Linux 上保持原明文兼容 —— 不是新抽象平台。
/// </summary>
public interface ITokenProtector
{
    /// <summary>能否保护字节。false 时沿用历史明文格式（仅非 Windows）。</summary>
    bool CanProtect { get; }

    /// <summary>是否尝试把已加载的明文迁移为受保护格式。</summary>
    bool ShouldMigratePlaintext { get; }

    /// <summary>保护字节。实现失败时抛异常，调用方不得回落明文。</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>解除保护。输入不是本实现可解的格式时抛异常。</summary>
    byte[] Unprotect(byte[] payload);
}

/// <summary>生产实现：Windows 用 DPAPI CurrentUser；非 Windows 保持明文（原行为）。</summary>
public sealed class PlatformTokenProtector : ITokenProtector
{
    public static readonly PlatformTokenProtector Instance = new();

    private PlatformTokenProtector() { }

    public bool CanProtect => OperatingSystem.IsWindows();

    public bool ShouldMigratePlaintext => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] plaintext) =>
        OperatingSystem.IsWindows()
            ? System.Security.Cryptography.ProtectedData.Protect(plaintext, null, System.Security.Cryptography.DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("Token protection is only available on Windows.");

    public byte[] Unprotect(byte[] payload) =>
        OperatingSystem.IsWindows()
            ? System.Security.Cryptography.ProtectedData.Unprotect(payload, null, System.Security.Cryptography.DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("Token protection is only available on Windows.");
}
