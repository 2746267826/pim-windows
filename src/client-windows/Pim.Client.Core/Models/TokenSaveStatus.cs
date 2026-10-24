namespace Pim.Client.Core.Models;

/// <summary>登录/续期/明文迁移共用的 token 持久化结果（AuthService 内部状态机输出）。</summary>
public enum TokenSaveStatus
{
    NotAttempted,
    SavedEncrypted,
    SavedPlaintext,
    EncryptionFailed,
    Failed
}
