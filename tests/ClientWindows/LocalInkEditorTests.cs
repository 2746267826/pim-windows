using System.Text;
using System.Text.Json;
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
    public void PerStrokeBrushStyleIsCapturedAtStrokeStartAndSurvivesUndoRedoAndReload()
    {
        var editor = new LocalInkEditor(320, 180)
        {
            Brush = new LocalInkStyle(LocalInkPalette.RedColor, LocalInkPalette.ThinWidth),
        };
        editor.BeginStroke(new LocalInkPoint(10, 20, 0.5));
        editor.AppendPoint(new LocalInkPoint(40, 50, 0.5));
        editor.Brush = new LocalInkStyle(LocalInkPalette.BlueColor, LocalInkPalette.ThickWidth);
        var first = editor.EndStroke();

        editor.BeginStroke(new LocalInkPoint(80, 90, 0.5));
        editor.AppendPoint(new LocalInkPoint(120, 130, 0.5));
        var second = editor.EndStroke();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(LocalInkPalette.RedColor, first.Color);
        Assert.Equal(LocalInkPalette.ThinWidth, first.Width);
        Assert.Equal(LocalInkPalette.BlueColor, second.Color);
        Assert.Equal(LocalInkPalette.ThickWidth, second.Width);

        Assert.True(editor.Undo());
        Assert.Equal(
            new[] { (LocalInkPalette.RedColor, LocalInkPalette.ThinWidth) },
            editor.Strokes.Select(stroke => (stroke.Color, stroke.Width)));
        Assert.True(editor.Redo());
        Assert.Equal(
            new[]
            {
                (LocalInkPalette.RedColor, LocalInkPalette.ThinWidth),
                (LocalInkPalette.BlueColor, LocalInkPalette.ThickWidth),
            },
            editor.Strokes.Select(stroke => (stroke.Color, stroke.Width)));

        var reopened = new LocalInkEditor(
            LocalInkDocumentCodec.Parse(LocalInkDocumentCodec.Serialize(editor.CreateDocument())));
        Assert.Equal(
            editor.Strokes.Select(stroke => (stroke.Id, stroke.Color, stroke.Width)),
            reopened.Strokes.Select(stroke => (stroke.Id, stroke.Color, stroke.Width)));
    }

    [Fact]
    public void PartialEraserSplitsOneStrokeAsOneUndoStepAndPreservesStyles()
    {
        var editor = new LocalInkEditor(320, 180)
        {
            Brush = new LocalInkStyle(LocalInkPalette.RedColor, LocalInkPalette.ThickWidth),
        };
        DrawStroke(editor, 20, 100, 300, 100);
        var crossed = editor.Strokes.Single();
        editor.Brush = new LocalInkStyle(LocalInkPalette.BlueColor, LocalInkPalette.ThinWidth);
        DrawStroke(editor, 20, 150, 300, 150);
        var untouched = editor.Strokes[1];

        var changed = editor.Erase(
        [
            new LocalInkPoint(150, 70, 1),
            new LocalInkPoint(150, 130, 1),
        ],
            LocalInkEraserMode.Partial);

        Assert.Equal(1, changed);
        Assert.Equal(3, editor.Strokes.Count);
        var left = editor.Strokes[0];
        var right = editor.Strokes[1];
        Assert.Equal(crossed.Id, left.Id);
        Assert.NotEqual(crossed.Id, right.Id);
        Assert.NotEqual(left.Id, right.Id);
        Assert.Equal(20, left.Points[0].X);
        Assert.True(left.Points[^1].X < 150);
        Assert.Equal(300, right.Points[^1].X);
        Assert.True(right.Points[0].X > 150);
        Assert.All([left, right], stroke =>
        {
            Assert.Equal(LocalInkPalette.RedColor, stroke.Color);
            Assert.Equal(LocalInkPalette.ThickWidth, stroke.Width);
        });
        Assert.Equal(untouched.Id, editor.Strokes[2].Id);
        Assert.Equal(LocalInkPalette.BlueColor, editor.Strokes[2].Color);

        Assert.True(editor.Undo());
        Assert.Equal(new[] { crossed.Id, untouched.Id }, editor.Strokes.Select(stroke => stroke.Id));
        Assert.True(editor.Redo());
        Assert.Equal(3, editor.Strokes.Count);
        Assert.Equal(
            editor.Strokes.Select(stroke => stroke.Id).Distinct().Count(),
            editor.Strokes.Count);
    }

    [Fact]
    public void WholeStrokeEraserRemovesOnlyTouchedStrokesAndRedoReapplies()
    {
        var editor = new LocalInkEditor(320, 180)
        {
            Brush = new LocalInkStyle(LocalInkPalette.RedColor, LocalInkPalette.ThickWidth),
        };
        DrawStroke(editor, 20, 100, 300, 100);
        var crossed = editor.Strokes.Single();
        editor.Brush = new LocalInkStyle(LocalInkPalette.BlueColor, LocalInkPalette.ThinWidth);
        DrawStroke(editor, 20, 150, 300, 150);
        var untouched = editor.Strokes[1];

        var changed = editor.Erase(
        [
            new LocalInkPoint(150, 80, 1),
            new LocalInkPoint(150, 120, 1),
        ],
            LocalInkEraserMode.WholeStroke);

        Assert.Equal(1, changed);
        Assert.Equal(untouched.Id, Assert.Single(editor.Strokes).Id);
        Assert.Equal(LocalInkPalette.BlueColor, editor.Strokes.Single().Color);
        Assert.True(editor.Undo());
        Assert.Equal(new[] { crossed.Id, untouched.Id }, editor.Strokes.Select(stroke => stroke.Id));
        Assert.True(editor.Redo());
        Assert.Equal(untouched.Id, Assert.Single(editor.Strokes).Id);
    }

    [Fact]
    public void EraserMissIsANoOpWithoutAnExtraUndoStep()
    {
        var editor = new LocalInkEditor(320, 180);
        DrawStroke(editor, 20, 20, 80, 80);

        var changed = editor.Erase(
        [
            new LocalInkPoint(250, 150, 1),
            new LocalInkPoint(280, 160, 1),
        ],
            LocalInkEraserMode.Partial);

        Assert.Equal(0, changed);
        Assert.Single(editor.Strokes);
        Assert.True(editor.Undo());
        Assert.Empty(editor.Strokes);
        Assert.True(editor.CanRedo);
        Assert.True(editor.Redo());
        Assert.Single(editor.Strokes);
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
    public void MovedTwoStrokesAreOneUndoStepAndOneRedoRestoresTheGroup()
    {
        var editor = new LocalInkEditor(680, 420);
        DrawStroke(editor, 10, 10, 80, 80);
        DrawStroke(editor, 150, 40, 220, 120);
        var originals = editor.Strokes.ToArray();
        var moved = new[]
        {
            Move(originals[0], 25, 15),
            Move(originals[1], 18, 12),
        };

        Assert.Equal(2, editor.UpdateStrokeGeometry(moved));
        AssertStrokePoints(moved, editor.Strokes);

        Assert.True(editor.Undo());
        AssertStrokePoints(originals, editor.Strokes);

        Assert.True(editor.Redo());
        AssertStrokePoints(moved, editor.Strokes);
    }

    [Fact]
    public void MultiStrokeMoveOutsideCanvasIsRejectedWithoutAnUndoStep()
    {
        var editor = new LocalInkEditor(320, 180);
        DrawStroke(editor, 10, 10, 80, 80);
        DrawStroke(editor, 100, 20, 160, 90);
        var originals = editor.Strokes.ToArray();
        var stillInside = Move(originals[0], 15, 10);
        var outside = Move(originals[1], 400, 0);

        Assert.Throws<InvalidDataException>(() =>
            editor.UpdateStrokeGeometry([stillInside, outside]));

        AssertStrokePoints(originals, editor.Strokes);
        Assert.True(editor.Undo());
        Assert.Single(editor.Strokes);
        AssertStrokePoints([originals[0]], editor.Strokes);
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
    public void MultiStrokePasteClampsTheGroupAtTheEdgeAndUndoesAsOneStep()
    {
        var editor = new LocalInkEditor(100, 100);
        DrawStroke(editor, 60, 60, 80, 80);
        DrawStroke(editor, 82, 70, 98, 92);
        var originals = editor.Strokes.ToArray();
        var clipboard = editor.Copy(originals.Select(stroke => stroke.Id).ToArray());

        var pasted = editor.Paste(clipboard);

        Assert.Equal(2, pasted.Count);
        Assert.Equal(4, editor.Strokes.Count);
        Assert.All(pasted.SelectMany(stroke => stroke.Points), point =>
        {
            Assert.InRange(point.X, 0, 100);
            Assert.InRange(point.Y, 0, 100);
        });
        var offsetX = pasted[0].Points[0].X - originals[0].Points[0].X;
        var offsetY = pasted[0].Points[0].Y - originals[0].Points[0].Y;
        Assert.Equal(
            originals[1].Points.Select(point => (point.X + offsetX, point.Y + offsetY)),
            pasted[1].Points.Select(point => (point.X, point.Y)));

        Assert.True(editor.Undo());
        AssertStrokePoints(originals, editor.Strokes);
        Assert.True(editor.Redo());
        Assert.Equal(4, editor.Strokes.Count);
    }

    [Fact]
    public void InvalidOrOutOfBoundsInputIsRejectedWithoutChangingTheDocument()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalInkEditor(0, 420));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalInkEditor(79, 420));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalInkEditor(2049, 420));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalInkEditor(320, 79));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalInkEditor(320, 4097));
        _ = new LocalInkEditor(80, 80);
        _ = new LocalInkEditor(2048, 4096);
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
    public void EditingAndMovingStrokesPreservesUnknownFields()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "width": 320,
              "height": 180,
              "futureRoot": "root",
              "strokeIds": ["11111111-1111-4111-8111-111111111111"],
              "strokeStyles": [{"color": "#b8373c", "width": 1.8, "futureStyle": 7}],
              "strokes": [
                {
                  "color": "#b8373c",
                  "width": 1.8,
                  "futureStroke": "stroke",
                  "points": [
                    {"x": 10, "y": 20, "pressure": 0.5, "futurePoint": 1},
                    {"x": 30, "y": 40, "pressure": 0.5, "futurePoint": 2}
                  ]
                }
              ]
            }
            """;
        var editor = new LocalInkEditor(LocalInkDocumentCodec.Parse(json));
        var moved = editor.Strokes[0] with
        {
            Points = editor.Strokes[0].Points
                .Select(point => new LocalInkPoint(point.X + 5, point.Y, point.Pressure))
                .ToArray(),
        };

        Assert.Equal(1, editor.UpdateStrokeGeometry([moved]));
        editor.BeginStroke(new LocalInkPoint(60, 80, 0.5));
        editor.AppendPoint(new LocalInkPoint(100, 120, 0.5));
        Assert.NotNull(editor.EndStroke());

        using var saved = JsonDocument.Parse(
            LocalInkDocumentCodec.Serialize(editor.CreateDocument()));
        var root = saved.RootElement;
        Assert.Equal("root", root.GetProperty("futureRoot").GetString());
        Assert.Equal(7, root.GetProperty("strokeStyles")[0].GetProperty("futureStyle").GetInt32());
        var stroke = root.GetProperty("strokes")[0];
        Assert.Equal("stroke", stroke.GetProperty("futureStroke").GetString());
        Assert.Equal(
            new[] { 1, 2 },
            stroke.GetProperty("points").EnumerateArray()
                .Select(point => point.GetProperty("futurePoint").GetInt32()));
        Assert.Equal(15d, stroke.GetProperty("points")[0].GetProperty("x").GetDouble());
        Assert.Equal(2, root.GetProperty("strokes").GetArrayLength());
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
    public async Task RemovingOneHandwritingKeepsRecordingPhotoOtherPairAndHistory()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("删除单组手写");
        var photoBytes = Encoding.UTF8.GetBytes("photo-bytes");
        var withPhoto = await store.AddMaterialsAsync(note.NoteId,
        [
            new LocalNoteMaterialInput(
                new MemoryStream(photoBytes),
                "board.png",
                LocalNoteMaterialKind.Photo,
                "image/png"),
        ]);
        var firstPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 };
        const string firstInk =
            """{"schemaVersion":1,"width":320,"height":180,"strokes":[[[10,20,0.5],[30,40,0.5]]]}""";
        var withFirst = await store.AddHandwritingAsync(
            note.NoteId,
            new MemoryStream(firstPng),
            firstInk,
            "手写 A",
            320,
            180,
            expectedCurrentVersionId: withPhoto.CurrentVersionId);
        var recordingBytes = Encoding.UTF8.GetBytes("recording-bytes");
        var withRecording = await store.AddMaterialsAsync(note.NoteId,
        [
            new LocalNoteMaterialInput(
                new MemoryStream(recordingBytes),
                "录音.wav",
                LocalNoteMaterialKind.Recording,
                "audio/wav",
                DurationMilliseconds: 100),
        ],
        expectedCurrentVersionId: withFirst.CurrentVersionId);
        var secondPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 2 };
        const string secondInk =
            """{"schemaVersion":1,"width":320,"height":180,"strokes":[[[50,60,0.5],[70,80,0.5]]]}""";
        var withSecond = await store.AddHandwritingAsync(
            note.NoteId,
            new MemoryStream(secondPng),
            secondInk,
            "手写 B",
            320,
            180,
            expectedCurrentVersionId: withRecording.CurrentVersionId);

        var firstPreview = withSecond.Materials.Single(material =>
            material.Kind == LocalNoteMaterialKind.Handwriting
            && material.DisplayName == "手写 A.png");
        var firstSource = withSecond.Materials.Single(material =>
            material.Kind == LocalNoteMaterialKind.HandwritingSource
            && material.DisplayName == "手写 A.ink.json");
        var secondPreview = withSecond.Materials.Single(material =>
            material.Kind == LocalNoteMaterialKind.Handwriting
            && material.DisplayName == "手写 B.png");
        var secondSource = withSecond.Materials.Single(material =>
            material.Kind == LocalNoteMaterialKind.HandwritingSource
            && material.DisplayName == "手写 B.ink.json");
        var recording = withSecond.Materials.Single(material =>
            material.Kind == LocalNoteMaterialKind.Recording);

        var removed = await store.RemoveHandwritingAsync(
            note.NoteId,
            firstPreview.MaterialId,
            expectedCurrentVersionId: withSecond.CurrentVersionId);

        Assert.NotEqual(withSecond.CurrentVersionId, removed.CurrentVersionId);
        Assert.Equal(
            new[]
            {
                LocalNoteMaterialKind.Photo,
                LocalNoteMaterialKind.Recording,
                LocalNoteMaterialKind.Handwriting,
                LocalNoteMaterialKind.HandwritingSource,
            },
            removed.Materials.Select(material => material.Kind));
        Assert.Contains(removed.Materials, material => material.MaterialId == recording.MaterialId);
        Assert.Contains(removed.Materials, material => material.MaterialId == secondPreview.MaterialId);
        Assert.Contains(removed.Materials, material => material.MaterialId == secondSource.MaterialId);
        Assert.DoesNotContain(removed.Materials, material => material.MaterialId == firstSource.MaterialId);
        Assert.True(File.Exists(Path.Combine(_root, firstPreview.RelativePath)));
        Assert.True(File.Exists(Path.Combine(_root, firstSource.RelativePath)));

        var history = await store.LoadVersionAsync(note.NoteId, withSecond.CurrentVersionId);
        Assert.Equal(withSecond.Materials.Select(material => material.MaterialId), history.MaterialIds);
        var unchanged = await store.LoadAsync(note.NoteId);
        Assert.Equal(removed.CurrentVersionId, unchanged.CurrentVersionId);
    }

    [Fact]
    public async Task DeletingPreviewFromOlderVersionIsRejectedAndKeepsCurrentVersion()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("跨版本删除");
        var firstPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 };
        const string firstInk =
            """{"schemaVersion":1,"width":320,"height":180,"strokes":[[[10,20,0.5],[30,40,0.5]]]}""";
        var withFirst = await store.AddHandwritingAsync(
            note.NoteId,
            new MemoryStream(firstPng),
            firstInk,
            "旧手写",
            320,
            180,
            expectedCurrentVersionId: note.CurrentVersionId);
        var oldPreview = withFirst.Materials.Single(material =>
            material.Kind == LocalNoteMaterialKind.Handwriting);
        var removed = await store.RemoveHandwritingAsync(
            note.NoteId,
            oldPreview.MaterialId,
            expectedCurrentVersionId: withFirst.CurrentVersionId);
        var secondPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 2 };
        const string secondInk =
            """{"schemaVersion":1,"width":320,"height":180,"strokes":[[[50,60,0.5],[70,80,0.5]]]}""";
        var withSecond = await store.AddHandwritingAsync(
            note.NoteId,
            new MemoryStream(secondPng),
            secondInk,
            "新手写",
            320,
            180,
            expectedCurrentVersionId: removed.CurrentVersionId);

        var rejected = await Assert.ThrowsAsync<LocalNoteStoreException>(() =>
            store.RemoveHandwritingAsync(
                note.NoteId,
                oldPreview.MaterialId,
                expectedCurrentVersionId: withSecond.CurrentVersionId));

        Assert.Contains("不在当前固定版本", rejected.Message, StringComparison.Ordinal);
        var unchanged = await store.LoadAsync(note.NoteId);
        Assert.Equal(withSecond.CurrentVersionId, unchanged.CurrentVersionId);
        Assert.Equal(
            withSecond.Materials.Select(material => material.MaterialId),
            unchanged.Materials.Select(material => material.MaterialId));
        var currentVersion = await store.LoadVersionAsync(note.NoteId, withSecond.CurrentVersionId);
        Assert.Equal(withSecond.Materials.Count, currentVersion.MaterialIds.Count);
    }

    [Fact]
    public void HandwritingPairResolutionRejectsMissingDuplicateOrMismatchedSource()
    {
        var preview = Material(Guid.NewGuid(), LocalNoteMaterialKind.Handwriting, "手写.png", 320, 180);
        var source = Material(Guid.NewGuid(), LocalNoteMaterialKind.HandwritingSource, "手写.ink.json", 320, 180);
        var photo = Material(Guid.NewGuid(), LocalNoteMaterialKind.Photo, "照片.png", 320, 180);

        Assert.Equal(
            source.MaterialId,
            LocalInkMaterialPairing.ResolveSource(
                preview.MaterialId,
                [photo, preview, source]).MaterialId);
        Assert.Throws<InvalidDataException>(() =>
            LocalInkMaterialPairing.ResolveSource(preview.MaterialId, [preview]));
        Assert.Throws<InvalidDataException>(() =>
            LocalInkMaterialPairing.ResolveSource(preview.MaterialId, [preview, photo]));
        Assert.Throws<InvalidDataException>(() =>
            LocalInkMaterialPairing.ResolveSource(
                preview.MaterialId,
                [preview, source with { Width = 1 }]));
        Assert.Throws<InvalidDataException>(() =>
            LocalInkMaterialPairing.ResolveSource(
                preview.MaterialId,
                [preview, source, preview]));
        Assert.Throws<InvalidDataException>(() =>
            LocalInkMaterialPairing.ResolveSource(photo.MaterialId, [photo, preview, source]));
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

    private static LocalInkStroke Move(LocalInkStroke stroke, double offsetX, double offsetY)
        => stroke with
        {
            Points = stroke.Points
                .Select(point => new LocalInkPoint(point.X + offsetX, point.Y + offsetY, point.Pressure))
                .ToArray(),
        };

    private static void AssertStrokePoints(
        IReadOnlyList<LocalInkStroke> expected,
        IReadOnlyList<LocalInkStroke> actual)
    {
        Assert.Equal(expected.Select(stroke => stroke.Id), actual.Select(stroke => stroke.Id));
        Assert.Equal(
            expected.SelectMany(stroke => stroke.Points.Select(point => (point.X, point.Y))),
            actual.SelectMany(stroke => stroke.Points.Select(point => (point.X, point.Y))));
    }

    private static LocalNoteMaterial Material(
        Guid id,
        LocalNoteMaterialKind kind,
        string displayName,
        int? width,
        int? height)
        => new()
        {
            MaterialId = id,
            Kind = kind,
            DisplayName = displayName,
            ContentSha256 = new string('0', 64),
            RelativePath = "materials/" + displayName,
            MediaType = "application/octet-stream",
            Length = 1,
            AddedAt = DateTimeOffset.UnixEpoch,
            Width = width,
            Height = height,
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
