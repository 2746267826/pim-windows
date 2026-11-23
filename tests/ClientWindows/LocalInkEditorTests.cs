using System.Text;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class LocalInkEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-local-ink-editor-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MouseDownMoveUpCreatesIndependentStrokesAndEmptyTapsAreRejected()
    {
        var editor = new LocalInkEditor(680, 420);

        editor.BeginStroke(new LocalInkPoint(20, 30, 0.5));
        editor.AppendPoint(new LocalInkPoint(40, 50, 0.6));
        editor.AppendPoint(new LocalInkPoint(60, 70, 0.4));
        var first = editor.EndStroke();

        Assert.NotNull(first);
        Assert.Equal(3, first.Points.Count);
        Assert.Single(editor.Strokes);
        Assert.True(editor.CanUndo);

        editor.BeginStroke(new LocalInkPoint(200, 200, 0.5));
        Assert.Null(editor.EndStroke());
        Assert.Single(editor.Strokes);

        editor.BeginStroke(new LocalInkPoint(100, 100, 0.5));
        editor.AppendPoint(new LocalInkPoint(140, 120, 0.5));
        var second = editor.EndStroke();

        Assert.NotNull(second);
        Assert.Equal(2, editor.Strokes.Count);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(new[] { first.Id, second.Id }, editor.Strokes.Select(stroke => stroke.Id));
    }

    [Fact]
    public async Task SaveThenReloadReadsBackPngBytesAndStrokeIdentity()
    {
        var editor = new LocalInkEditor(320, 180);
        DrawStroke(editor, 10, 20, 50, 60);
        DrawStroke(editor, 90, 100, 150, 140);
        var vectorJson = editor.Serialize();
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 8, 9 };
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("鼠标手写");

        var saved = await store.AddHandwritingAsync(
            note.NoteId,
            new MemoryStream(pngBytes),
            vectorJson,
            "鼠标手写",
            320,
            180,
            expectedCurrentVersionId: note.CurrentVersionId);
        var files = await store.ResolveVersionMaterialsAsync(note.NoteId, saved.CurrentVersionId);
        var preview = files.Single(file => file.Material.Kind == LocalNoteMaterialKind.Handwriting);
        var source = files.Single(file => file.Material.Kind == LocalNoteMaterialKind.HandwritingSource);

        Assert.Equal(pngBytes, await File.ReadAllBytesAsync(preview.AbsolutePath));
        var reloaded = LocalInkDocumentCodec.Parse(await File.ReadAllTextAsync(source.AbsolutePath));
        Assert.Equal(editor.Strokes.Select(stroke => stroke.Id), reloaded.Strokes.Select(stroke => stroke.Id));
        Assert.Equal(
            editor.Strokes.SelectMany(stroke => stroke.Points),
            reloaded.Strokes.SelectMany(stroke => stroke.Points));

        var reopenedEditor = new LocalInkEditor(reloaded);
        Assert.Equal(2, reopenedEditor.Strokes.Count);
        Assert.Equal(
            editor.Strokes.SelectMany(stroke => stroke.Points),
            reopenedEditor.Strokes.SelectMany(stroke => stroke.Points));
    }

    [Fact]
    public void UndoRedoDeleteAndClearAreSingleEditorSteps()
    {
        var editor = new LocalInkEditor(680, 420);
        DrawStroke(editor, 10, 10, 80, 80);
        DrawStroke(editor, 100, 100, 160, 160);
        var firstId = editor.Strokes[0].Id;

        Assert.True(editor.Undo());
        Assert.Single(editor.Strokes);
        Assert.True(editor.Undo());
        Assert.Empty(editor.Strokes);
        Assert.False(editor.Undo());

        Assert.True(editor.Redo());
        Assert.Single(editor.Strokes);
        Assert.True(editor.Redo());
        Assert.Equal(2, editor.Strokes.Count);

        Assert.Equal(1, editor.DeleteStrokes([firstId]));
        Assert.Single(editor.Strokes);
        Assert.True(editor.Undo());
        Assert.Equal(2, editor.Strokes.Count);

        Assert.True(editor.Clear());
        Assert.Empty(editor.Strokes);
        Assert.True(editor.Undo());
        Assert.Equal(2, editor.Strokes.Count);
        Assert.False(editor.Clear() && editor.CanRedo);
    }

    [Fact]
    public void CopyPasteCreatesNewIdsAndRejectsCrossWindowClipboard()
    {
        var editor = new LocalInkEditor(680, 420);
        DrawStroke(editor, 10, 10, 80, 80);
        DrawStroke(editor, 100, 100, 160, 160);
        var source = editor.Strokes[0];
        var clipboard = editor.Copy([source.Id]);

        Assert.Equal(source.Id, clipboard.Strokes.Single().Id);
        Assert.Throws<ArgumentException>(() => editor.Copy([]));
        Assert.Throws<InvalidOperationException>(() => editor.Copy([Guid.NewGuid()]));

        var pasted = editor.Paste(clipboard);
        Assert.Single(pasted);
        Assert.NotEqual(source.Id, pasted[0].Id);
        Assert.Equal(3, editor.Strokes.Count);
        Assert.All(pasted[0].Points, point => Assert.True(point.X >= source.Points.Min(item => item.X)));

        Assert.True(editor.Undo());
        Assert.Equal(2, editor.Strokes.Count);

        var otherWindowEditor = new LocalInkEditor(680, 420);
        var foreign = Assert.Throws<InvalidOperationException>(() => otherWindowEditor.Paste(clipboard));
        Assert.Contains("当前手写窗口", foreign.Message, StringComparison.Ordinal);
        Assert.Empty(otherWindowEditor.Strokes);
    }

    [Fact]
    public void MovedSelectionUpdatesInkGeometryAsOneUndoStep()
    {
        var editor = new LocalInkEditor(680, 420);
        DrawStroke(editor, 10, 10, 80, 80);
        var original = editor.Strokes.Single();
        var moved = original with
        {
            Points = original.Points
                .Select(point => new LocalInkPoint(point.X + 25, point.Y + 15, point.Pressure))
                .ToArray(),
        };

        Assert.Equal(1, editor.UpdateStrokeGeometry([moved]));
        Assert.Equal(
            moved.Points.Select(point => (point.X, point.Y)),
            editor.Strokes.Single().Points.Select(point => (point.X, point.Y)));

        Assert.True(editor.Undo());
        Assert.Equal(
            original.Points.Select(point => (point.X, point.Y)),
            editor.Strokes.Single().Points.Select(point => (point.X, point.Y)));
        Assert.True(editor.Redo());
        Assert.Equal(
            moved.Points.Select(point => (point.X, point.Y)),
            editor.Strokes.Single().Points.Select(point => (point.X, point.Y)));
    }

    [Fact]
    public void InvalidSelectionGeometryUpdatesAreAtomic()
    {
        var editor = new LocalInkEditor(320, 180);
        DrawStroke(editor, 10, 10, 80, 80);
        var original = editor.Strokes.Single();
        var withOnePoint = original with { Points = [original.Points[0]] };
        var outsideCanvas = original with
        {
            Points = [new LocalInkPoint(10, 10, 0.5), new LocalInkPoint(500, 500, 0.5)],
        };

        Assert.Throws<InvalidOperationException>(() => editor.UpdateStrokeGeometry(
        [
            new LocalInkStroke(Guid.NewGuid(), [new LocalInkPoint(10, 10, 0.5)]),
        ]));
        Assert.Throws<InvalidDataException>(() => editor.UpdateStrokeGeometry([withOnePoint]));
        Assert.Throws<InvalidDataException>(() => editor.UpdateStrokeGeometry([outsideCanvas]));
        Assert.Throws<InvalidDataException>(() => editor.UpdateStrokeGeometry([
            original with { Points = [new LocalInkPoint(10, 10, 0.5)] },
            original with { Points = [new LocalInkPoint(20, 20, 0.5)] },
        ]));

        Assert.Equal(
            original.Points.Select(point => (point.X, point.Y)),
            editor.Strokes.Single().Points.Select(point => (point.X, point.Y)));
        Assert.True(editor.Undo());
        Assert.Empty(editor.Strokes);
    }

    [Fact]
    public void EdgePastePreservesStrokeGeometryAndRejectsForgedClipboardAtomically()
    {
        var editor = new LocalInkEditor(100, 100);
        DrawStroke(editor, 88, 88, 98, 98);
        var source = editor.Strokes.Single();
        var clipboard = editor.Copy([source.Id]);

        var pasted = Assert.Single(editor.Paste(clipboard));
        Assert.All(pasted.Points, point =>
        {
            Assert.InRange(point.X, 0, 100);
            Assert.InRange(point.Y, 0, 100);
        });
        var offsetX = pasted.Points[0].X - source.Points[0].X;
        var offsetY = pasted.Points[0].Y - source.Points[0].Y;
        Assert.Equal(
            source.Points.Select(point => (point.X + offsetX, point.Y + offsetY)),
            pasted.Points.Select(point => (point.X, point.Y)));
        Assert.True(editor.Undo());
        Assert.Single(editor.Strokes);

        var forged = new LocalInkClipboard(editor.SessionId,
        [
            new LocalInkStroke(Guid.NewGuid(),
            [
                new LocalInkPoint(10, 10, 0.5),
                new LocalInkPoint(double.NaN, 20, 0.5),
            ]),
        ]);
        Assert.Throws<InvalidDataException>(() => editor.Paste(forged));
        Assert.Single(editor.Strokes);
        Assert.True(editor.Undo());
        Assert.Empty(editor.Strokes);
    }

    [Fact]
    public void InvalidOrOutOfBoundsInputIsRejectedWithoutChangingTheDocument()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalInkEditor(0, 420));
        var editor = new LocalInkEditor(320, 180);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            editor.BeginStroke(new LocalInkPoint(double.NaN, 20, 0.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            editor.BeginStroke(new LocalInkPoint(10, 20, 1.1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            editor.BeginStroke(new LocalInkPoint(321.5, 20, 0.5)));
        Assert.False(editor.IsDrawing);
        Assert.Empty(editor.Strokes);
        Assert.False(editor.CanUndo);

        Assert.Throws<InvalidDataException>(() =>
            editor.AddCompletedStroke(new LocalInkStroke(Guid.NewGuid(), [])));
        Assert.Throws<InvalidDataException>(() =>
            editor.AddCompletedStroke(new LocalInkStroke(Guid.Empty, [new LocalInkPoint(1, 2, 0.5)])));
        Assert.Empty(editor.Strokes);

        editor.BeginStroke(new LocalInkPoint(10, 10, 0.5));
        editor.AppendPoint(new LocalInkPoint(20, 20, 0.5));
        var accepted = editor.EndStroke();
        Assert.NotNull(accepted);
        Assert.Equal(accepted.Id, Assert.Single(editor.Strokes).Id);
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public async Task RemovingHandwritingCreatesNewVersionAndKeepsHistoryAndOtherMaterials()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("删除手写");
        var photoBytes = Encoding.UTF8.GetBytes("photo");
        var withPhoto = await store.AddMaterialsAsync(note.NoteId,
        [
            new LocalNoteMaterialInput(
                new MemoryStream(photoBytes),
                "board.png",
                LocalNoteMaterialKind.Photo,
                "image/png"),
        ]);
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var ink = """{"schemaVersion":1,"width":320,"height":180,"strokes":[[[10,20,0.5],[30,40,0.5]]]}""";
        var withInk = await store.AddHandwritingAsync(
            note.NoteId,
            new MemoryStream(pngBytes),
            ink,
            "鼠标手写",
            320,
            180,
            expectedCurrentVersionId: withPhoto.CurrentVersionId);
        var preview = withInk.Materials.Single(material => material.Kind == LocalNoteMaterialKind.Handwriting);
        var source = withInk.Materials.Single(material => material.Kind == LocalNoteMaterialKind.HandwritingSource);
        var previewPath = Path.Combine(_root, preview.RelativePath);
        var sourcePath = Path.Combine(_root, source.RelativePath);

        await Assert.ThrowsAsync<LocalNoteStoreException>(() => store.RemoveHandwritingAsync(
            note.NoteId,
            preview.MaterialId,
            expectedCurrentVersionId: withPhoto.CurrentVersionId));

        var removed = await store.RemoveHandwritingAsync(
            note.NoteId,
            preview.MaterialId,
            expectedCurrentVersionId: withInk.CurrentVersionId);

        Assert.NotEqual(withInk.CurrentVersionId, removed.CurrentVersionId);
        Assert.Single(removed.Materials);
        Assert.Equal(LocalNoteMaterialKind.Photo, removed.Materials[0].Kind);
        Assert.True(File.Exists(previewPath));
        Assert.True(File.Exists(sourcePath));
        var history = await store.LoadVersionAsync(note.NoteId, withInk.CurrentVersionId);
        Assert.Contains(preview.MaterialId, history.MaterialIds);
        Assert.Contains(source.MaterialId, history.MaterialIds);

        await Assert.ThrowsAsync<LocalNoteStoreException>(() => store.RemoveHandwritingAsync(
            note.NoteId,
            preview.MaterialId,
            expectedCurrentVersionId: removed.CurrentVersionId));
        await Assert.ThrowsAsync<LocalNoteStoreException>(() => store.RemoveHandwritingAsync(
            note.NoteId,
            source.MaterialId,
            expectedCurrentVersionId: removed.CurrentVersionId));
        await Assert.ThrowsAsync<LocalNoteStoreException>(() => store.RemoveHandwritingAsync(
            note.NoteId,
            removed.Materials[0].MaterialId,
            expectedCurrentVersionId: removed.CurrentVersionId));

        var unchanged = await store.LoadAsync(note.NoteId);
        Assert.Equal(removed.CurrentVersionId, unchanged.CurrentVersionId);
        Assert.Single(unchanged.Materials);
    }

    [Fact]
    public void InvalidLoadedDocumentIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => new LocalInkEditor(new LocalInkDocument(
            1,
            320,
            180,
            [new LocalInkStroke(Guid.NewGuid(), [new LocalInkPoint(400, 20, 0.5)])])));
        Assert.Throws<InvalidDataException>(() => new LocalInkEditor(new LocalInkDocument(
            1,
            320,
            180,
            [
                new LocalInkStroke(Guid.NewGuid(), [new LocalInkPoint(1, 2, 0.5)]),
                new LocalInkStroke(Guid.NewGuid(), [new LocalInkPoint(1, 2, 2.0)]),
            ])));
    }

    private static void DrawStroke(LocalInkEditor editor, double x1, double y1, double x2, double y2)
    {
        editor.BeginStroke(new LocalInkPoint(x1, y1, 0.5));
        editor.AppendPoint(new LocalInkPoint((x1 + x2) / 2, (y1 + y2) / 2, 0.6));
        editor.AppendPoint(new LocalInkPoint(x2, y2, 0.4));
        Assert.NotNull(editor.EndStroke());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
