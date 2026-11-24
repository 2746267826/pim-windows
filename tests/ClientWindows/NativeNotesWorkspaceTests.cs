using System.Text.RegularExpressions;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class NativeNotesWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-native-notes-workspace-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WideWindowUsesInlineSharedSidebarAndCompactWindowFloatsIt()
    {
        var layout = new NativeNotesWorkspaceLayout();

        Assert.True(layout.IsCompact(900));
        Assert.True(layout.IsSidebarInline(1280));
        Assert.False(layout.IsSidebarOverlay(1280));
        Assert.False(layout.IsSidebarInline(900));
        Assert.True(layout.IsSidebarOverlay(900));
        Assert.True(layout.IsSidebarVisible(0));
        Assert.Equal(
            NativeNotesWorkspaceLayout.DefaultSidebarWidth,
            layout.EffectiveSidebarWidth(0));

        layout.ShowPane(NativeNotesSidebarPane.Materials);

        Assert.Equal(NativeNotesSidebarPane.Materials, layout.SidebarPane);
        Assert.True(layout.IsSidebarOverlay(900));
        Assert.InRange(
            layout.EffectiveSidebarWidth(900),
            NativeNotesWorkspaceLayout.MinSidebarWidth,
            900 - NativeNotesWorkspaceLayout.OverlayEdgeGap);
        Assert.Equal(layout.SidebarWidth, layout.EffectiveSidebarWidth(1280));
    }

    [Fact]
    public void SidebarCanCollapseAndReopenWithoutLosingTheAdjustedBoundary()
    {
        var layout = new NativeNotesWorkspaceLayout();
        layout.SetSidebarWidth(430);
        layout.ShowPane(NativeNotesSidebarPane.Materials);

        layout.ToggleSidebar();

        Assert.True(layout.IsSidebarCollapsed);
        Assert.False(layout.IsSidebarVisible(1280));
        Assert.False(layout.IsSidebarInline(1280));
        Assert.Equal(430, layout.SidebarWidth);

        layout.ShowPane(NativeNotesSidebarPane.Notes);

        Assert.False(layout.IsSidebarCollapsed);
        Assert.Equal(NativeNotesSidebarPane.Notes, layout.SidebarPane);
        Assert.True(layout.IsSidebarInline(1280));
        Assert.Equal(430, layout.SidebarWidth);
    }

    [Fact]
    public void WidthStaysInsideTheSharedBoundaryAndResetRestoresTheDefault()
    {
        var layout = new NativeNotesWorkspaceLayout();

        layout.SetSidebarWidth(10);
        Assert.Equal(NativeNotesWorkspaceLayout.MinSidebarWidth, layout.SidebarWidth);

        layout.SetSidebarWidth(10_000);
        Assert.Equal(NativeNotesWorkspaceLayout.MaxSidebarWidth, layout.SidebarWidth);

        layout.AdjustSidebarWidth(-1);
        Assert.Equal(
            NativeNotesWorkspaceLayout.MaxSidebarWidth - NativeNotesWorkspaceLayout.WidthStep,
            layout.SidebarWidth);

        layout.AdjustSidebarWidth(1);
        Assert.Equal(NativeNotesWorkspaceLayout.MaxSidebarWidth, layout.SidebarWidth);

        layout.ResetSidebarWidth();
        Assert.Equal(NativeNotesWorkspaceLayout.DefaultSidebarWidth, layout.SidebarWidth);
    }

    [Fact]
    public void KeyboardShortcutsAdjustResetToggleAndOpenTheSharedPanes()
    {
        Assert.True(NativeNotesWorkspaceShortcuts.TryResolve(
            control: true,
            shift: false,
            alt: true,
            NativeNotesWorkspaceKey.Right,
            out var wider));
        Assert.Equal(NativeNotesWorkspaceAction.IncreaseSidebarWidth, wider);

        Assert.True(NativeNotesWorkspaceShortcuts.TryResolve(
            control: true,
            shift: false,
            alt: true,
            NativeNotesWorkspaceKey.Left,
            out var narrower));
        Assert.Equal(NativeNotesWorkspaceAction.DecreaseSidebarWidth, narrower);

        Assert.True(NativeNotesWorkspaceShortcuts.TryResolve(
            control: true,
            shift: false,
            alt: true,
            NativeNotesWorkspaceKey.D0,
            out var reset));
        Assert.Equal(NativeNotesWorkspaceAction.ResetSidebarWidth, reset);

        Assert.True(NativeNotesWorkspaceShortcuts.TryResolve(
            control: true,
            shift: false,
            alt: true,
            NativeNotesWorkspaceKey.B,
            out var toggle));
        Assert.Equal(NativeNotesWorkspaceAction.ToggleSidebar, toggle);

        Assert.True(NativeNotesWorkspaceShortcuts.TryResolve(
            control: true,
            shift: false,
            alt: true,
            NativeNotesWorkspaceKey.M,
            out var materials));
        Assert.Equal(NativeNotesWorkspaceAction.ShowMaterials, materials);

        Assert.False(NativeNotesWorkspaceShortcuts.TryResolve(
            control: false,
            shift: false,
            alt: false,
            NativeNotesWorkspaceKey.Right,
            out _));

        var layout = new NativeNotesWorkspaceLayout();
        layout.SetSidebarWidth(400);
        Assert.True(layout.Apply(NativeNotesWorkspaceAction.IncreaseSidebarWidth));
        Assert.Equal(400 + NativeNotesWorkspaceLayout.WidthStep, layout.SidebarWidth);
        Assert.True(layout.Apply(NativeNotesWorkspaceAction.ResetSidebarWidth));
        Assert.Equal(NativeNotesWorkspaceLayout.DefaultSidebarWidth, layout.SidebarWidth);
        Assert.True(layout.Apply(NativeNotesWorkspaceAction.ShowMaterials));
        Assert.Equal(NativeNotesSidebarPane.Materials, layout.SidebarPane);
        Assert.True(layout.Apply(NativeNotesWorkspaceAction.ToggleSidebar));
        Assert.True(layout.IsSidebarCollapsed);
    }

    [Fact]
    public async Task WorkspacePreferencesRestoreLastNotePaneWidthAndCollapsedState()
    {
        var noteId = Guid.NewGuid();
        var store = new NativeNotesWorkspaceStore(_root);
        var before = new NativeNotesWorkspaceLayout();
        before.SetSidebarWidth(456);
        before.ShowPane(NativeNotesSidebarPane.Materials);
        before.RememberNote(noteId);
        before.ToggleSidebar();

        await store.SaveAsync(before.ToPreferences());
        var loaded = NativeNotesWorkspaceLayout.FromPreferences(await store.LoadAsync());

        Assert.Equal(456, loaded.SidebarWidth);
        Assert.Equal(NativeNotesSidebarPane.Materials, loaded.SidebarPane);
        Assert.Equal(noteId, loaded.LastNoteId);
        Assert.True(loaded.IsSidebarCollapsed);
    }

    [Fact]
    public async Task CorruptWorkspacePreferencesFallBackToSafeDefaults()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, NativeNotesWorkspaceStore.FileName),
            "{not-valid-json");
        var store = new NativeNotesWorkspaceStore(_root);

        var loaded = NativeNotesWorkspaceLayout.FromPreferences(await store.LoadAsync());

        Assert.Equal(NativeNotesWorkspaceLayout.DefaultSidebarWidth, loaded.SidebarWidth);
        Assert.Equal(NativeNotesSidebarPane.Notes, loaded.SidebarPane);
        Assert.False(loaded.IsSidebarCollapsed);
        Assert.Null(loaded.LastNoteId);
    }

    [Fact]
    public void OutOfRangePersistedWidthAndUnknownPaneFallBackToSafeValues()
    {
        var loaded = NativeNotesWorkspaceLayout.FromPreferences(new NativeNotesWorkspacePreferences
        {
            SidebarWidth = -200,
            SidebarPane = (NativeNotesSidebarPane)99,
            LastNoteId = Guid.Empty,
        });

        Assert.Equal(NativeNotesWorkspaceLayout.MinSidebarWidth, loaded.SidebarWidth);
        Assert.Equal(NativeNotesSidebarPane.Notes, loaded.SidebarPane);
        Assert.Null(loaded.LastNoteId);
    }

    [Theory]
    [InlineData(NoteProcessingState.WaitingForMaterial, "同步：等待材料", NativeNotesSyncTone.Warning)]
    [InlineData(NoteProcessingState.PendingProcessing, "同步：待处理", NativeNotesSyncTone.Active)]
    [InlineData(NoteProcessingState.Processing, "同步：处理中", NativeNotesSyncTone.Active)]
    [InlineData(NoteProcessingState.PartiallyCompleted, "同步：部分完成", NativeNotesSyncTone.Active)]
    [InlineData(NoteProcessingState.Completed, "同步：已完成", NativeNotesSyncTone.Success)]
    [InlineData(NoteProcessingState.NeedsAttention, "同步：需核对", NativeNotesSyncTone.Warning)]
    public void SyncStatusComesFromTheRealServerProcessingState(
        NoteProcessingState state,
        string expectedText,
        NativeNotesSyncTone expectedTone)
    {
        var status = NativeNotesSyncStatusView.FromProcessing(CreateStatus(state));

        Assert.Equal(expectedText, status.Text);
        Assert.Equal(expectedTone, status.Tone);
        Assert.DoesNotContain("TaskId", status.Text);
        Assert.DoesNotContain("processing", status.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyncStatusDistinguishesMissingAccountTargetUploadAndServerFailure()
    {
        Assert.Equal("同步：未选择笔记", NativeNotesSyncStatusView.NoNote().Text);
        Assert.Equal("同步：未登录", NativeNotesSyncStatusView.SignedOut().Text);
        Assert.Equal("同步：未上传", NativeNotesSyncStatusView.LocalOnly().Text);
        Assert.Equal("同步：正在上传", NativeNotesSyncStatusView.Uploading().Text);
        Assert.Equal("同步：正在核对", NativeNotesSyncStatusView.Checking().Text);
        Assert.Equal("同步：本机保存中", NativeNotesSyncStatusView.Saving().Text);
        Assert.Equal("同步：本机已保存", NativeNotesSyncStatusView.Saved().Text);
        Assert.Equal("同步：本机保存失败", NativeNotesSyncStatusView.LocalSaveFailed().Text);
        Assert.Equal("同步：服务器状态不可用", NativeNotesSyncStatusView.Unavailable().Text);
        Assert.Equal(NativeNotesSyncTone.Error, NativeNotesSyncStatusView.Unavailable().Tone);
    }

    [Fact]
    public void WindowChromeRemovesTutorialCopyAndKeepsUploadRecordsCollapsed()
    {
        var xaml = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml"));

        Assert.DoesNotContain("我的笔记", xaml);
        Assert.DoesNotContain("内容优先 · 离线保存 · 固定材料版本", xaml);
        Assert.DoesNotContain("当前固定版本中的材料（双击照片预览或音频回放）", xaml);
        Assert.DoesNotContain("云端结果会显示在这里", xaml);
        Assert.DoesNotContain("登录后可上传固定版本并读取云端结果", xaml);
        Assert.DoesNotContain("本机原稿", xaml);
        Assert.DoesNotContain("离线记录", xaml);
        Assert.DoesNotContain("云端处理尚未接入", xaml);
        Assert.Contains("x:Name=\"SyncStatusText\"", xaml);
        Assert.Contains("x:Name=\"UploadRecordsExpander\"", xaml);
        Assert.Contains("Header=\"更多：上传记录与云端版本\"", xaml);
        Assert.Matches(
            new Regex(
                "<Expander\\s+x:Name=\"UploadRecordsExpander\"[\\s\\S]*?IsExpanded=\"False\"",
                RegexOptions.CultureInvariant),
            xaml);
        Assert.Contains("x:Name=\"SidebarSplitter\"", xaml);
        Assert.Contains("x:Name=\"SidebarPanel\"", xaml);
        Assert.Contains("PreviewKeyDown=\"OnPreviewKeyDown\"", xaml);
    }

    [Fact]
    public void WindowRestoresLastNoteAndKeepsProcessingStatusBoundToRealServerState()
    {
        var code = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml.cs"));

        Assert.Contains("_workspace.LastNoteId", code);
        Assert.Contains("RememberNote(note.NoteId)", code);
        Assert.Contains("NativeNotesSyncStatusView.FromProcessing(status)", code);
        Assert.Contains("NativeNotesSyncStatusView.LocalOnly()", code);
        Assert.Contains("NativeNotesSyncStatusView.Saving()", code);
        Assert.Contains("NativeNotesSyncStatusView.Saved()", code);
        Assert.Contains("NativeNotesSyncStatusView.LocalSaveFailed()", code);
        Assert.Contains("RefreshProcessingStatusAsync", code);
        Assert.Contains("NoteProcessingStatusRequest", code);
        Assert.Contains("ForUploadedVersion", code);
        Assert.Contains("FromServerSource", code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static NoteProcessingStatusDto CreateStatus(NoteProcessingState state)
        => new()
        {
            Artifacts = [],
            Asr = new NoteProcessingChainDto
            {
                Kind = NoteProcessingTaskKind.Asr,
                State = NoteProcessingChainState.NotApplicable,
                TotalItems = 0,
                CompletedItems = 0,
                FailedItems = 0,
                HasResult = false,
                TaskCreated = false,
                WaitingReason = null,
                Errors = [],
            },
            AutoProcessingEnabled = true,
            AutoStarted = false,
            CreatedAt = DateTimeOffset.UtcNow,
            Derived = new NoteProcessingChainDto
            {
                Kind = NoteProcessingTaskKind.Derived,
                State = NoteProcessingChainState.NotApplicable,
                TotalItems = 0,
                CompletedItems = 0,
                FailedItems = 0,
                HasResult = false,
                TaskCreated = false,
                WaitingReason = null,
                Errors = [],
            },
            IncrementalScopeSupported = true,
            Issues = [],
            ManualStartAvailable = true,
            NoteId = Guid.NewGuid().ToString("D"),
            PendingReason = null,
            ProcessingId = Guid.NewGuid().ToString("D"),
            Scope = "note",
            State = state,
            UpdatedAt = DateTimeOffset.UtcNow,
            VersionId = Guid.NewGuid().ToString("D"),
        };

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }
}
