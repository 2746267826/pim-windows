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

    public string Label =>
        $"{(IsServerSource ? "服务器恢复" : "已上传固定版本")} · "
        + $"笔记 {NoteId.ToString("N")[..8]} · 版本 {VersionId.ToString("N")[..8]}";
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
        if (!string.IsNullOrWhiteSpace(chain.WaitingReason)
            && !string.Equals(chain.WaitingReason, state, StringComparison.Ordinal))
        {
            parts.Add(chain.WaitingReason);
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
        var labels = artifacts
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
        if (!string.IsNullOrWhiteSpace(status.PendingReason))
        {
            return "说明：" + status.PendingReason;
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
                "说明：有处理步骤需要人工核对，可刷新查看最新状态。",
            _ => "说明：状态未知，请刷新核对。",
        };
    }

    private static string BuildIssuesText(NoteProcessingStatusDto status)
    {
        var messages = status.Issues
            .Select(issue => issue.Message)
            .Concat(status.Asr.Errors)
            .Concat(status.Derived.Errors)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Where(message => !string.Equals(
                message,
                status.PendingReason,
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
}
