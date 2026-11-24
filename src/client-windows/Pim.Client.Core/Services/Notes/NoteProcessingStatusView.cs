using System.Text.RegularExpressions;
using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services.Notes;

/// <summary>Immutable server material identity used for one high-level processing status read.</summary>
public sealed record NoteProcessingStatusTarget(
    Guid NoteId,
    Guid VersionId,
    bool IsServerSource)
{
    public static NoteProcessingStatusTarget? FromServerSource(LocalNoteServerSource source)
        => source.SourceNoteId != Guid.Empty && source.SourceVersionId != Guid.Empty
            ? new NoteProcessingStatusTarget(
                source.SourceNoteId,
                source.SourceVersionId,
                IsServerSource: true)
            : null;

    public static NoteProcessingStatusTarget? ForUploadedVersion(
        Guid currentNoteId,
        Guid noteId,
        Guid versionId)
        => currentNoteId != Guid.Empty
           && noteId == currentNoteId
           && versionId != Guid.Empty
            ? new NoteProcessingStatusTarget(noteId, versionId, IsServerSource: false)
            : null;

    public string Label => IsServerSource
        ? "服务器恢复的只读版本"
        : "已上传的服务器固定版本";
}

/// <summary>
/// Captures the account, target and UI generation for one status request so a late
/// response can never repaint another owner, note or version.
/// </summary>
public sealed record NoteProcessingStatusRequest(
    long Generation,
    Guid NoteId,
    Guid VersionId,
    CloudNoteAccountScope Account,
    long AccountGeneration)
{
    public bool IsCurrent(
        long generation,
        Guid noteId,
        Guid versionId,
        CloudNoteAccountScope? account,
        long accountGeneration)
        => Generation == generation
           && NoteId == noteId
           && VersionId == versionId
           && Account == account
           && AccountGeneration == accountGeneration;
}

/// <summary>User-facing projection of the server's material/ASR/derived status.</summary>
public sealed record NoteProcessingStatusView(
    string OverallText,
    string AsrText,
    string DerivedText,
    string ArtifactsText,
    string HintText,
    string IssuesText,
    DateTimeOffset UpdatedAt)
{
    private static readonly Regex GuidPattern = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex LongHexPattern = new(
        @"\b[0-9a-fA-F]{16,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex EngineeringFieldPattern = new(
        @"\b(?:taskId|changeKey|graphEventId|processingId|revision|stepKey|stepKeys|errorCode|callId)\b\s*[:=]\s*\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex TechnicalOnlyPattern = new(
        @"^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static NoteProcessingStatusView From(NoteProcessingStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new(
            $"总体：{OverallStateLabel(status.State)} · "
            + $"自动处理{(status.AutoProcessingEnabled ? "开启" : "关闭")}",
            ChainText(status.Asr),
            ChainText(status.Derived),
            BuildArtifactsText(status.Artifacts),
            BuildHintText(status),
            BuildIssuesText(status),
            status.UpdatedAt);
    }

    public static string OverallStateLabel(NoteProcessingState state) => state switch
    {
        NoteProcessingState.WaitingForMaterial => "等待材料",
        NoteProcessingState.PendingProcessing => "待发起处理",
        NoteProcessingState.Processing => "处理中",
        NoteProcessingState.PartiallyCompleted => "部分完成",
        NoteProcessingState.Completed => "已完成",
        NoteProcessingState.NeedsAttention => "需核对",
        _ => state.ToString(),
    };

    public static string ChainStateLabel(NoteProcessingChainState state) => state switch
    {
        NoteProcessingChainState.NotApplicable => "不适用",
        NoteProcessingChainState.WaitingForMaterial => "等待材料",
        NoteProcessingChainState.WaitingForAsr => "等待录音转写",
        NoteProcessingChainState.PendingStart => "待发起",
        NoteProcessingChainState.Queued => "排队中",
        NoteProcessingChainState.Running => "处理中",
        NoteProcessingChainState.Succeeded => "已完成",
        NoteProcessingChainState.NeedsAttention => "需核对",
        NoteProcessingChainState.Stopped => "已停止",
        _ => state.ToString(),
    };

    private static string ChainText(NoteProcessingChainDto chain)
    {
        var task = TaskLabel(chain.Kind);
        if (chain.State == NoteProcessingChainState.NotApplicable)
        {
            return $"{task}：不适用";
        }

        var state = ChainStateLabel(chain.State);
        var parts = new List<string> { $"{task}：{state}" };
        if (chain.TotalItems > 0)
        {
            parts.Add($"{chain.CompletedItems}/{chain.TotalItems}");
        }
        if (chain.FailedItems > 0)
        {
            parts.Add($"失败 {chain.FailedItems}");
        }
        if (chain.HasResult)
        {
            parts.Add("已生成结果");
        }
        var waitingReason = UserVisibleText(chain.WaitingReason);
        if (!string.IsNullOrWhiteSpace(waitingReason)
            && !string.Equals(waitingReason, state, StringComparison.Ordinal))
        {
            parts.Add(waitingReason);
        }

        return string.Join(" · ", parts);
    }

    private static string TaskLabel(NoteProcessingTaskKind kind) => kind switch
    {
        NoteProcessingTaskKind.Asr => "录音转写",
        NoteProcessingTaskKind.Derived => "笔记整理",
        _ => kind.ToString(),
    };

    private static string BuildArtifactsText(IReadOnlyList<NoteProcessingArtifactDto> artifacts)
    {
        var labels = (artifacts ?? [])
            .Select(artifact => ArtifactLabel(artifact.Kind))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return labels.Length == 0 ? "结果：尚未生成" : "结果：" + string.Join("、", labels);
    }

    private static string ArtifactLabel(NoteDerivedArtifactKind kind) => kind switch
    {
        NoteDerivedArtifactKind.CompleteRecord => "完整记录",
        NoteDerivedArtifactKind.IndependentAnalysis => "独立分析",
        _ => kind.ToString(),
    };

    private static string BuildHintText(NoteProcessingStatusDto status)
    {
        var pendingReason = UserVisibleText(status.PendingReason);
        if (!string.IsNullOrWhiteSpace(pendingReason))
        {
            return "说明：" + pendingReason;
        }

        return status.State switch
        {
            NoteProcessingState.WaitingForMaterial =>
                "说明：材料尚未完整，上传完成后会继续。",
            NoteProcessingState.PendingProcessing =>
                "说明：材料已完整，处理任务尚未发起。",
            NoteProcessingState.Processing =>
                "说明：服务端仍在处理，稍后刷新可查看最新状态。",
            NoteProcessingState.PartiallyCompleted =>
                "说明：录音转写已完成，笔记整理仍在进行。",
            NoteProcessingState.Completed =>
                "说明：两条处理链路均已完成。",
            NoteProcessingState.NeedsAttention =>
                BuildAttentionHint(status),
            _ => "说明：状态未知，请刷新核对。",
        };
    }

    private static string BuildAttentionHint(NoteProcessingStatusDto status)
    {
        var asrBlocked = status.Asr.State is
            NoteProcessingChainState.NeedsAttention or NoteProcessingChainState.Stopped;
        if (asrBlocked && status.Derived.State == NoteProcessingChainState.WaitingForAsr)
        {
            return "说明：录音转写失败或已停止，笔记整理已被阻断；可在其他客户端重试处理后刷新状态。";
        }
        if (asrBlocked)
        {
            return "说明：录音转写失败或已停止，笔记整理可能被阻断；可在其他客户端重试处理后刷新状态。";
        }
        if (status.Derived.State is
            NoteProcessingChainState.NeedsAttention or NoteProcessingChainState.Stopped)
        {
            return "说明：笔记整理失败或已停止，已生成结果仍可查看；可在其他客户端重试处理后刷新状态。";
        }

        return "说明：有处理步骤需要人工核对，可刷新查看最新状态。";
    }

    private static string BuildIssuesText(NoteProcessingStatusDto status)
    {
        var messages = (status.Issues ?? [])
            .Select(issue => issue.Message)
            .Concat(status.Asr.Errors ?? [])
            .Concat(status.Derived.Errors ?? [])
            .Select(UserVisibleText)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Where(message => !string.Equals(
                message,
                UserVisibleText(status.PendingReason),
                StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (messages.Length == 0)
        {
            return string.Empty;
        }

        var shown = string.Join("；", messages.Take(3));
        return messages.Length <= 3
            ? "需核对：" + shown
            : $"需核对：{shown}（另有 {messages.Length - 3} 条）";
    }

    private static string UserVisibleText(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var redacted = EngineeringFieldPattern.Replace(message, " ");
        redacted = GuidPattern.Replace(redacted, "[标识已隐藏]");
        redacted = LongHexPattern.Replace(redacted, "[标识已隐藏]");
        redacted = string.Join(
            ' ',
            redacted.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return TechnicalOnlyPattern.IsMatch(redacted)
            ? "服务端返回了处理错误，请在处理页查看详情。"
            : redacted;
    }
}
