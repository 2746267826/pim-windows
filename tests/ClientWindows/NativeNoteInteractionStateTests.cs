using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class NativeNoteInteractionStateTests
{
    [Fact]
    public void OlderLibraryResultCannotOverwriteTheNewestQuery()
    {
        var state = new NativeNoteLibraryLoadState();
        var first = Summary("第一版");
        var second = Summary("第二版");
        var initialTicket = state.BeginRefresh(string.Empty);
        Assert.True(state.TryApply(initialTicket, [first]));

        var oldTicket = state.BeginRefresh("旧查询");
        var newTicket = state.BeginRefresh("新查询");

        Assert.False(state.TryApply(oldTicket, [Summary("旧结果")]));
        Assert.False(state.TryFail(oldTicket, "旧请求失败"));
        Assert.True(state.IsLoading);
        Assert.True(state.TryApply(newTicket, [second]));

        Assert.Equal("新查询", state.Query);
        Assert.Equal([second.NoteId], state.Notes.Select(note => note.NoteId));
        Assert.False(state.IsLoading);
        Assert.Null(state.Error);
    }

    [Fact]
    public void RefreshFailureKeepsRowsAndUsesNonBlockingInlinePresentation()
    {
        var state = new NativeNoteLibraryLoadState();
        var row = Summary("保留的笔记");
        var loaded = state.BeginRefresh(string.Empty);
        Assert.True(state.TryApply(loaded, [row]));

        var failed = state.BeginRefresh("课堂");
        var loadingPresentation = state.Presentation;
        Assert.True(loadingPresentation.ShowList);
        Assert.False(loadingPresentation.ShowOverlay);
        Assert.True(loadingPresentation.ShowInlineProgress);

        Assert.True(state.TryFail(failed, "磁盘暂时不可读"));
        var failurePresentation = state.Presentation;

        Assert.Equal([row.NoteId], state.Notes.Select(note => note.NoteId));
        Assert.True(failurePresentation.ShowList);
        Assert.False(failurePresentation.ShowOverlay);
        Assert.True(failurePresentation.ShowInlineError);
        Assert.True(failurePresentation.ShowRetryAction);
    }

    [Fact]
    public void MarkerLayoutInterleavesEverySupportedMaterialKindInBodyOrder()
    {
        var photo = Material(LocalNoteMaterialKind.Photo, "照片.png");
        var handwriting = Material(LocalNoteMaterialKind.Handwriting, "手写 1.png");
        var handwritingSource = Material(
            LocalNoteMaterialKind.HandwritingSource,
            "手写 1.ink.json",
            width: handwriting.Width,
            height: handwriting.Height);
        var recording = Material(
            LocalNoteMaterialKind.Recording,
            "录音.wav",
            durationMilliseconds: 42_000,
            recordingId: Guid.NewGuid());
        var unplaced = Material(LocalNoteMaterialKind.Photo, "未定位.png");
        var markdown = string.Join(
            "\n\n",
            "开头",
            NativeNoteDocumentLayout.CreateMarker(photo.MaterialId),
            "中间",
            NativeNoteDocumentLayout.CreateMarker(handwriting.MaterialId),
            "录音前",
            NativeNoteDocumentLayout.CreateMarker(recording.MaterialId),
            "结尾");

        var layout = NativeNoteDocumentLayout.Parse(
            markdown,
            [photo, handwriting, handwritingSource, recording, unplaced]);

        Assert.Equal(
            [
                NativeNoteDocumentSegmentKind.Markdown,
                NativeNoteDocumentSegmentKind.Material,
                NativeNoteDocumentSegmentKind.Markdown,
                NativeNoteDocumentSegmentKind.Material,
                NativeNoteDocumentSegmentKind.Markdown,
                NativeNoteDocumentSegmentKind.Material,
                NativeNoteDocumentSegmentKind.Markdown,
            ],
            layout.Segments.Select(segment => segment.Kind));
        Assert.Equal(
            [photo.MaterialId, handwriting.MaterialId, recording.MaterialId],
            layout.Segments
                .Where(segment => segment.Kind == NativeNoteDocumentSegmentKind.Material)
                .Select(segment => segment.MaterialId!.Value));
        Assert.Equal([unplaced.MaterialId], layout.UnplacedMaterials.Select(material => material.MaterialId));
        Assert.Equal(
            [
                photo.MaterialId,
                handwriting.MaterialId,
                handwritingSource.MaterialId,
                recording.MaterialId,
                unplaced.MaterialId,
            ],
            layout.OrderedMaterialIds);
    }

    [Fact]
    public void RepeatedHandwritingNamesStayPairedWhenMarkersReorderFamilies()
    {
        var firstPreview = Material(LocalNoteMaterialKind.Handwriting, "同名手写.png");
        var firstSource = Material(LocalNoteMaterialKind.HandwritingSource, "同名手写.ink.json");
        var secondPreview = Material(LocalNoteMaterialKind.Handwriting, "同名手写.png");
        var secondSource = Material(LocalNoteMaterialKind.HandwritingSource, "同名手写.ink.json");
        var materials = new[] { firstPreview, firstSource, secondPreview, secondSource };
        var markdown = string.Join(
            "\n\n",
            NativeNoteDocumentLayout.CreateMarker(secondPreview.MaterialId),
            "中间",
            NativeNoteDocumentLayout.CreateMarker(firstPreview.MaterialId));

        var layout = NativeNoteDocumentLayout.Parse(markdown, materials);
        var ordered = layout.OrderedMaterialIds
            .Select(id => materials.Single(material => material.MaterialId == id))
            .ToArray();

        Assert.Equal(
            [
                secondPreview.MaterialId,
                secondSource.MaterialId,
                firstPreview.MaterialId,
                firstSource.MaterialId,
            ],
            ordered.Select(material => material.MaterialId));
        Assert.Equal(
            secondSource.MaterialId,
            LocalInkMaterialPairing.ResolveSource(secondPreview.MaterialId, ordered).MaterialId);
        Assert.Equal(
            firstSource.MaterialId,
            LocalInkMaterialPairing.ResolveSource(firstPreview.MaterialId, ordered).MaterialId);
    }

    [Fact]
    public void InsertingMarkersAtCaretPreservesTextAndMovesTheCaretAfterTheBlock()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        const string original = "第一段\n\n第二段";

        var inserted = NativeNoteDocumentLayout.InsertMarkers(original, "第一段".Length, [first, second]);
        var parsed = NativeNoteDocumentLayout.Parse(inserted.Markdown, [
            Material(LocalNoteMaterialKind.Photo, "a.png", first),
            Material(LocalNoteMaterialKind.Recording, "b.wav", second, Guid.NewGuid()),
        ]);

        Assert.Equal(inserted.CaretIndex, inserted.Markdown.Length - "\n\n第二段".Length);
        Assert.StartsWith("第一段\n\n<!-- pim-material:", inserted.Markdown, StringComparison.Ordinal);
        Assert.Contains(second.ToString("D"), inserted.Markdown, StringComparison.Ordinal);
        Assert.EndsWith("第二段", inserted.Markdown, StringComparison.Ordinal);
        Assert.Equal(
            [first, second],
            parsed.Segments
                .Where(segment => segment.Kind == NativeNoteDocumentSegmentKind.Material)
                .Select(segment => segment.MaterialId!.Value));
    }

    [Fact]
    public void UnknownMarkerStaysVisibleAndNeverDropsKnownMaterials()
    {
        var known = Material(LocalNoteMaterialKind.Photo, "known.png");
        var unknownId = Guid.NewGuid();
        var markdown = $"前\n\n{NativeNoteDocumentLayout.CreateMarker(unknownId)}\n\n后";

        var layout = NativeNoteDocumentLayout.Parse(markdown, [known]);

        Assert.Contains(
            layout.Segments,
            segment => segment.Kind == NativeNoteDocumentSegmentKind.UnresolvedMaterial
                && segment.MaterialId == unknownId);
        Assert.Equal([known.MaterialId], layout.OrderedMaterialIds);
        Assert.Equal([known.MaterialId], layout.UnplacedMaterials.Select(material => material.MaterialId));
    }

    [Fact]
    public void ServerSourceBlocksRenderPhotosAndHandwritingAtTheirStoredPositions()
    {
        var photo = Material(LocalNoteMaterialKind.Photo, "server-photo.png");
        var preview = Material(LocalNoteMaterialKind.Handwriting, "server-ink.png");
        var source = Material(
            LocalNoteMaterialKind.HandwritingSource,
            "server-ink.ink.json",
            width: preview.Width,
            height: preview.Height);
        var recording = Material(
            LocalNoteMaterialKind.RecordingSegment,
            "server-audio.wav",
            recordingId: Guid.NewGuid(),
            width: null,
            height: null);
        var serverSource = new LocalNoteServerSource(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "{}",
            "{}",
            "{}",
            string.Empty,
            [
                new LocalNoteServerSourceBlock(
                    Guid.NewGuid().ToString("D"),
                    "markdown",
                    """{"text":"开头"}""",
                    []),
                new LocalNoteServerSourceBlock(
                    Guid.NewGuid().ToString("D"),
                    "photo",
                    """{"kind":"photo"}""",
                    [photo.MaterialId]),
                new LocalNoteServerSourceBlock(
                    Guid.NewGuid().ToString("D"),
                    "markdown",
                    """{"text":"中间"}""",
                    []),
                new LocalNoteServerSourceBlock(
                    Guid.NewGuid().ToString("D"),
                    "ink",
                    """{"kind":"ink"}""",
                    [source.MaterialId, preview.MaterialId]),
                new LocalNoteServerSourceBlock(
                    Guid.NewGuid().ToString("D"),
                    "markdown",
                    """{"text":"结尾"}""",
                    []),
            ],
            []);

        var layout = NativeNoteDocumentLayout.ParseServerSource(
            serverSource,
            [photo, preview, source, recording]);

        Assert.Equal(
            [photo.MaterialId, preview.MaterialId],
            layout.Segments
                .Where(segment => segment.Kind == NativeNoteDocumentSegmentKind.Material)
                .Select(segment => segment.MaterialId!.Value));
        Assert.Equal([recording.MaterialId], layout.UnplacedMaterials.Select(material => material.MaterialId));
    }

    [Fact]
    public void WideLayoutDocksBothPanelsAndNarrowLayoutCollapsesToSingleColumn()
    {
        var layout = new NativeNoteLayoutState(1480);
        Assert.True(layout.Current.LibraryVisible);
        Assert.False(layout.Current.CloudVisible);

        layout.ToggleCloud(noteAvailable: true);
        Assert.True(layout.Current.LibraryVisible);
        Assert.True(layout.Current.CloudVisible);
        Assert.False(layout.Current.LibraryOverlay);
        Assert.False(layout.Current.CloudOverlay);
        Assert.False(layout.Current.SingleColumn);

        layout.Resize(900);
        Assert.Equal(NativeNoteLayoutMode.Narrow, layout.Current.Mode);
        Assert.False(layout.Current.LibraryVisible);
        Assert.False(layout.Current.CloudVisible);
        Assert.True(layout.Current.SingleColumn);

        layout.ToggleLibrary();
        Assert.True(layout.Current.LibraryVisible);
        Assert.True(layout.Current.LibraryOverlay);

        layout.ToggleCloud(noteAvailable: true);
        Assert.False(layout.Current.LibraryVisible);
        Assert.True(layout.Current.CloudVisible);
        Assert.True(layout.Current.CloudOverlay);
    }

    [Fact]
    public void SplitLayoutDocksLibraryAndOverlaysCloudToProtectEditorWidth()
    {
        var layout = new NativeNoteLayoutState(1100);
        layout.ToggleCloud(noteAvailable: true);

        Assert.Equal(NativeNoteLayoutMode.Split, layout.Current.Mode);
        Assert.True(layout.Current.LibraryVisible);
        Assert.False(layout.Current.LibraryOverlay);
        Assert.True(layout.Current.CloudVisible);
        Assert.True(layout.Current.CloudOverlay);
    }

    [Fact]
    public void HidingLibraryKeepsCloudDockedWhenThereIsRoomForBoth()
    {
        var layout = new NativeNoteLayoutState(1100);
        layout.ToggleCloud(noteAvailable: true);
        layout.ToggleLibrary();

        Assert.False(layout.Current.LibraryVisible);
        Assert.True(layout.Current.CloudVisible);
        Assert.False(layout.Current.CloudOverlay);
        Assert.False(layout.Current.SingleColumn);
    }

    [Fact]
    public void WindowStateRaisesBindableTitleChangesForDraftAndNoteSwitches()
    {
        var state = new NativeNoteWindowState();
        var changed = new List<string?>();
        state.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        state.OpenNote(first, "第一课");
        state.UpdateDraftTitle("第一课（修订）");
        state.UpdateDraftTitle("第一课（最终）");
        state.SetRecording(true);
        state.OpenNote(second, "第二课");
        state.ShowLibrary(hasNotes: true);

        Assert.Contains(nameof(NativeNoteWindowState.WindowTitle), changed);
        Assert.Contains(nameof(NativeNoteWindowState.EditorContext), changed);
        Assert.Equal("选择一篇笔记", state.WindowTitle);
        Assert.Equal("选择一篇笔记继续", state.EditorContext);

        state.OpenNote(first, "  实时标题  ");
        Assert.Equal("实时标题 · 笔记", state.WindowTitle);
        state.UpdateDraftTitle(string.Empty);
        Assert.Equal("无标题笔记", state.WindowTitle);
    }

    [Fact]
    public async Task FinishWaitsForInFlightImportAndRejectsNewWorkUntilItCompletes()
    {
        using var coordinator = new NativeNoteOperationCoordinator();
        var order = new List<string>();
        var importEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseImport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importLease = coordinator.TryBeginOperation();
        Assert.NotNull(importLease);

        var import = Task.Run(async () =>
        {
            using (importLease)
            {
                await importLease!.EnterSerializedAsync();
                try
                {
                    order.Add("import-start");
                    importEntered.SetResult();
                    await releaseImport.Task;
                    order.Add("import-end");
                }
                finally
                {
                    importLease.ExitSerialized();
                }
            }
        });
        await importEntered.Task;

        Assert.Null(coordinator.TryBeginOperation());

        var finish = coordinator.FinishAsync(() =>
        {
            order.Add("finish");
            return Task.CompletedTask;
        });
        Assert.False(finish.IsCompleted);

        releaseImport.SetResult();
        await import;
        var result = await finish;

        Assert.True(result.Started);
        Assert.True(result.Succeeded);
        Assert.Equal(["import-start", "import-end", "finish"], order);
        using var afterFinish = coordinator.TryBeginOperation();
        Assert.NotNull(afterFinish);
    }

    [Fact]
    public async Task FinishFailureUnlocksRetryAndDoesNotLeaveTheGateClosed()
    {
        using var coordinator = new NativeNoteOperationCoordinator();

        var failed = await coordinator.FinishAsync(
            () => throw new InvalidOperationException("磁盘写入失败"));

        Assert.True(failed.Started);
        Assert.False(failed.Succeeded);
        Assert.IsType<InvalidOperationException>(failed.Failure);
        using (var lease = coordinator.TryBeginOperation())
        {
            Assert.NotNull(lease);
        }

        var retried = await coordinator.FinishAsync(() => Task.CompletedTask);
        Assert.True(retried.Succeeded);
    }

    private static LocalNoteSummary Summary(string title)
        => new(
            Guid.NewGuid(),
            title,
            string.Empty,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0,
            0,
            Guid.NewGuid());

    private static LocalNoteMaterial Material(
        LocalNoteMaterialKind kind,
        string displayName,
        Guid? materialId = null,
        Guid? recordingId = null,
        int? width = 320,
        int? height = 180,
        long? durationMilliseconds = null)
        => new()
        {
            MaterialId = materialId ?? Guid.NewGuid(),
            Kind = kind,
            DisplayName = displayName,
            ContentSha256 = new string('a', 64),
            RelativePath = $"materials/{displayName}",
            MediaType = kind switch
            {
                LocalNoteMaterialKind.Photo or LocalNoteMaterialKind.Handwriting => "image/png",
                LocalNoteMaterialKind.Recording => "audio/wav",
                LocalNoteMaterialKind.HandwritingSource => "application/vnd.pim.ink+json",
                _ => "application/octet-stream",
            },
            Length = 1,
            AddedAt = DateTimeOffset.UtcNow,
            Width = width,
            Height = height,
            DurationMilliseconds = durationMilliseconds,
            RecordingId = recordingId,
        };
}
