using System.Text.Json;
using System.Text.Json.Serialization;
using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services.Notes;

public enum NativeNotesSidebarPane
{
    Notes,
    Materials,
}

public enum NativeNotesWorkspaceKey
{
    Left,
    Right,
    D0,
    B,
    M,
}

public enum NativeNotesWorkspaceAction
{
    DecreaseSidebarWidth,
    IncreaseSidebarWidth,
    ResetSidebarWidth,
    ToggleSidebar,
    ShowNotes,
    ShowMaterials,
}

/// <summary>
/// Pure layout state for the native notes workspace. One shared sidebar switches between
/// the note list and the selected note's materials; compact windows float it over the editor.
/// </summary>
public sealed class NativeNotesWorkspaceLayout
{
    public const double CompactWidth = 1_000;
    public const double DefaultSidebarWidth = 360;
    public const double MinSidebarWidth = 260;
    public const double MaxSidebarWidth = 560;
    public const double WidthStep = 24;
    public const double OverlayEdgeGap = 32;

    private double _sidebarWidth = DefaultSidebarWidth;

    public double SidebarWidth
    {
        get => _sidebarWidth;
        private set => _sidebarWidth = ValidateWidth(value);
    }

    public NativeNotesSidebarPane SidebarPane { get; private set; } = NativeNotesSidebarPane.Notes;

    public bool IsSidebarCollapsed { get; private set; }

    public Guid? LastNoteId { get; private set; }

    public bool IsCompact(double windowWidth) => windowWidth < CompactWidth;

    public bool IsSidebarVisible(double windowWidth)
        => !IsSidebarCollapsed;

    public bool IsSidebarInline(double windowWidth)
        => IsSidebarVisible(windowWidth) && !IsCompact(windowWidth);

    public bool IsSidebarOverlay(double windowWidth)
        => IsSidebarVisible(windowWidth) && IsCompact(windowWidth);

    public double EffectiveSidebarWidth(double windowWidth)
    {
        if (!double.IsFinite(windowWidth) || windowWidth <= 0)
        {
            return DefaultSidebarWidth;
        }

        if (!IsCompact(windowWidth))
        {
            return SidebarWidth;
        }

        var max = Math.Max(
            MinSidebarWidth,
            Math.Min(SidebarWidth, windowWidth - OverlayEdgeGap));
        return max;
    }

    public void SetSidebarWidth(double width) => SidebarWidth = width;

    public void AdjustSidebarWidth(int steps)
        => SidebarWidth = SidebarWidth + (steps * WidthStep);

    public void ResetSidebarWidth() => SidebarWidth = DefaultSidebarWidth;

    public void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    public void ShowPane(NativeNotesSidebarPane pane)
    {
        SidebarPane = pane;
        IsSidebarCollapsed = false;
    }

    public void RememberNote(Guid? noteId) => LastNoteId = noteId;

    public bool Apply(NativeNotesWorkspaceAction action)
    {
        switch (action)
        {
            case NativeNotesWorkspaceAction.DecreaseSidebarWidth:
                AdjustSidebarWidth(-1);
                return true;
            case NativeNotesWorkspaceAction.IncreaseSidebarWidth:
                AdjustSidebarWidth(1);
                return true;
            case NativeNotesWorkspaceAction.ResetSidebarWidth:
                ResetSidebarWidth();
                return true;
            case NativeNotesWorkspaceAction.ToggleSidebar:
                ToggleSidebar();
                return true;
            case NativeNotesWorkspaceAction.ShowNotes:
                ShowPane(NativeNotesSidebarPane.Notes);
                return true;
            case NativeNotesWorkspaceAction.ShowMaterials:
                ShowPane(NativeNotesSidebarPane.Materials);
                return true;
            default:
                return false;
        }
    }

    public NativeNotesWorkspacePreferences ToPreferences() => new()
    {
        SidebarWidth = SidebarWidth,
        SidebarPane = SidebarPane,
        SidebarCollapsed = IsSidebarCollapsed,
        LastNoteId = LastNoteId,
    };

    public static NativeNotesWorkspaceLayout FromPreferences(NativeNotesWorkspacePreferences? preferences)
    {
        var layout = new NativeNotesWorkspaceLayout();
        if (preferences is null)
        {
            return layout;
        }

        layout.SidebarWidth = preferences.SidebarWidth ?? DefaultSidebarWidth;
        layout.SidebarPane = Enum.IsDefined(preferences.SidebarPane)
            ? preferences.SidebarPane
            : NativeNotesSidebarPane.Notes;
        layout.IsSidebarCollapsed = preferences.SidebarCollapsed;
        layout.LastNoteId = preferences.LastNoteId is { } noteId && noteId != Guid.Empty
            ? noteId
            : null;
        return layout;
    }

    private static double ValidateWidth(double width)
        => double.IsFinite(width)
            ? Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth)
            : DefaultSidebarWidth;
}

public sealed record NativeNotesWorkspacePreferences
{
    public int SchemaVersion { get; init; } = 1;
    public double? SidebarWidth { get; init; }
    public NativeNotesSidebarPane SidebarPane { get; init; }
    public bool SidebarCollapsed { get; init; }
    public Guid? LastNoteId { get; init; }
}

public static class NativeNotesWorkspaceShortcuts
{
    public static bool TryResolve(
        bool control,
        bool shift,
        bool alt,
        NativeNotesWorkspaceKey key,
        out NativeNotesWorkspaceAction action)
    {
        action = default;
        if (!control || shift || !alt)
        {
            return false;
        }

        return key switch
        {
            NativeNotesWorkspaceKey.Left => Set(NativeNotesWorkspaceAction.DecreaseSidebarWidth, out action),
            NativeNotesWorkspaceKey.Right => Set(NativeNotesWorkspaceAction.IncreaseSidebarWidth, out action),
            NativeNotesWorkspaceKey.D0 => Set(NativeNotesWorkspaceAction.ResetSidebarWidth, out action),
            NativeNotesWorkspaceKey.B => Set(NativeNotesWorkspaceAction.ToggleSidebar, out action),
            NativeNotesWorkspaceKey.M => Set(NativeNotesWorkspaceAction.ShowMaterials, out action),
            _ => false,
        };
    }

    private static bool Set(NativeNotesWorkspaceAction value, out NativeNotesWorkspaceAction action)
    {
        action = value;
        return true;
    }
}

/// <summary>
/// Reads and writes one small preferences file with a bounded wait and atomic replace.
/// Missing or corrupt preferences deliberately fall back to in-memory defaults.
/// </summary>
public sealed class NativeNotesWorkspaceStore
{
    public const string FileName = "native-notes-workspace.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NativeNotesWorkspaceStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("笔记工作区路径不能为空。", nameof(root));
        }

        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    public async Task<NativeNotesWorkspacePreferences?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(_root, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > 64 * 1024)
            {
                return null;
            }

            return await JsonSerializer.DeserializeAsync<NativeNotesWorkspacePreferences>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        NativeNotesWorkspacePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, FileName);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                    FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

public enum NativeNotesSyncTone
{
    Neutral,
    Active,
    Success,
    Warning,
    Error,
}

public sealed record NativeNotesSyncStatus(string Text, NativeNotesSyncTone Tone);

/// <summary>
/// The window header only reflects real local/cloud state. It never invents a synced
/// or processed state when the server has not answered.
/// </summary>
public static class NativeNotesSyncStatusView
{
    public static NativeNotesSyncStatus NoNote()
        => new("同步：未选择笔记", NativeNotesSyncTone.Neutral);

    public static NativeNotesSyncStatus SignedOut()
        => new("同步：未登录", NativeNotesSyncTone.Neutral);

    public static NativeNotesSyncStatus LocalOnly()
        => new("同步：未上传", NativeNotesSyncTone.Warning);

    public static NativeNotesSyncStatus Uploading()
        => new("同步：正在上传", NativeNotesSyncTone.Active);

    public static NativeNotesSyncStatus Checking()
        => new("同步：正在核对", NativeNotesSyncTone.Active);

    public static NativeNotesSyncStatus Saving()
        => new("同步：本机保存中", NativeNotesSyncTone.Active);

    public static NativeNotesSyncStatus Saved()
        => new("同步：本机已保存", NativeNotesSyncTone.Neutral);

    public static NativeNotesSyncStatus LocalSaveFailed()
        => new("同步：本机保存失败", NativeNotesSyncTone.Error);

    public static NativeNotesSyncStatus Unavailable()
        => new("同步：服务器状态不可用", NativeNotesSyncTone.Error);

    public static NativeNotesSyncStatus FromProcessing(NoteProcessingStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var (text, tone) = status.State switch
        {
            NoteProcessingState.WaitingForMaterial => ("等待材料", NativeNotesSyncTone.Warning),
            NoteProcessingState.PendingProcessing => ("待处理", NativeNotesSyncTone.Active),
            NoteProcessingState.Processing => ("处理中", NativeNotesSyncTone.Active),
            NoteProcessingState.PartiallyCompleted => ("部分完成", NativeNotesSyncTone.Active),
            NoteProcessingState.Completed => ("已完成", NativeNotesSyncTone.Success),
            NoteProcessingState.NeedsAttention => ("需核对", NativeNotesSyncTone.Warning),
            _ => ("状态未知", NativeNotesSyncTone.Warning),
        };
        return new NativeNotesSyncStatus("同步：" + text, tone);
    }
}
