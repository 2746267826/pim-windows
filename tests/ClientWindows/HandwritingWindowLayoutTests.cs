using System.Reflection;
using System.Windows.Controls;
using System.Windows.Ink;
using Pim.Client.App;
using Pim.Client.Core.Services.Notes;
using Button = System.Windows.Controls.Button;
using Xunit;

namespace Pim.Client.App.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class HandwritingWindowLayoutTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void NewCanvasHasDrawableWpfLayoutAndCancelLeavesNoCapture()
    {
        wpf.Run(() =>
        {
            var layoutWindow = new HandwritingWindow();
            layoutWindow.Show();
            layoutWindow.UpdateLayout();
            var canvas = Assert.IsType<InkCanvas>(layoutWindow.FindName("Canvas"));
            Assert.True(canvas.ActualWidth >= 640, $"Canvas width was {canvas.ActualWidth}.");
            Assert.True(canvas.ActualHeight >= 400, $"Canvas height was {canvas.ActualHeight}.");
            Assert.True(canvas.IsVisible);
            layoutWindow.Close();

            var editWindow = new HandwritingWindow();
            var cancelButton = Assert.IsType<Button>(editWindow.FindName("CancelButton"));
            Assert.True(cancelButton.IsCancel);
            editWindow.Dispatcher.BeginInvoke(() => editWindow.DialogResult = false);
            Assert.False(editWindow.ShowDialog().GetValueOrDefault());
            Assert.Null(editWindow.Capture);
        });
    }

    [Fact]
    public void SavedCaptureCanBeReopenedAndUndoRemovesOneMouseGesture()
    {
        wpf.Run(() =>
        {
            var window = new HandwritingWindow();
            window.Show();
            window.UpdateLayout();
            window.Editor.BeginStroke(new LocalInkPoint(20, 30, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(60, 70, 0.6));
            window.Editor.AppendPoint(new LocalInkPoint(100, 110, 0.4));
            var first = window.Editor.EndStroke();
            window.Editor.BeginStroke(new LocalInkPoint(140, 150, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(180, 190, 0.5));
            var second = window.Editor.EndStroke();
            window.RefreshCanvasFromEditorForTest();
            window.UpdateLayout();

            var canvas = Assert.IsType<InkCanvas>(window.FindName("Canvas"));
            Assert.Equal(2, canvas.Strokes.Count);

            var capture = window.TryCapture();
            Assert.NotNull(capture);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, capture.PngBytes.Take(4));
            var parsed = LocalInkDocumentCodec.Parse(capture.VectorJson);
            Assert.Equal(new[] { first!.Id, second!.Id }, parsed.Strokes.Select(stroke => stroke.Id));

            var reopened = new HandwritingWindow(parsed);
            reopened.Show();
            reopened.UpdateLayout();
            Assert.Equal(
                new[] { first.Id, second.Id },
                reopened.Editor.Strokes.Select(stroke => stroke.Id));
            var reopenedCanvas = Assert.IsType<InkCanvas>(reopened.FindName("Canvas"));
            Assert.Equal(2, reopenedCanvas.Strokes.Count);

            var undoButton = Assert.IsType<Button>(window.FindName("UndoButton"));
            undoButton.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert.Single(window.Editor.Strokes);
            Assert.Single(canvas.Strokes);

            reopened.Close();
            window.Close();
        });
    }

    [Fact]
    public void EmptyWindowCannotCaptureAndClearIsUndoable()
    {
        wpf.Run(() =>
        {
            var window = new HandwritingWindow();
            window.Show();
            window.UpdateLayout();
            Assert.Null(window.TryCapture());

            window.Editor.BeginStroke(new LocalInkPoint(10, 10, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(30, 40, 0.5));
            window.Editor.EndStroke();
            window.RefreshCanvasFromEditorForTest();
            var clearButton = Assert.IsType<Button>(window.FindName("ClearButton"));
            clearButton.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(window.Editor.Strokes);

            var undoButton = Assert.IsType<Button>(window.FindName("UndoButton"));
            undoButton.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert.Single(window.Editor.Strokes);
            window.Close();
        });
    }

    [Fact]
    public void MovedSelectionIsWrittenToEditableInkBeforePngCapture()
    {
        wpf.Run(() =>
        {
            var window = new HandwritingWindow();
            window.Show();
            window.UpdateLayout();
            window.Editor.BeginStroke(new LocalInkPoint(20, 30, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(60, 70, 0.6));
            window.Editor.AppendPoint(new LocalInkPoint(100, 110, 0.4));
            var original = window.Editor.EndStroke();
            Assert.NotNull(original);
            window.RefreshCanvasFromEditorForTest();
            window.UpdateLayout();

            var canvas = Assert.IsType<InkCanvas>(window.FindName("Canvas"));
            canvas.Select(new StrokeCollection(canvas.Strokes));
            canvas.Strokes.Transform(new System.Windows.Media.Matrix(1, 0, 0, 1, 25, 15), applyToStylusTip: false);
            var onMoved = typeof(InkCanvas).GetMethod(
                "OnSelectionMoved",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onMoved);
            onMoved.Invoke(canvas, [EventArgs.Empty]);

            var moved = Assert.Single(window.Editor.Strokes);
            Assert.Equal(original.Id, moved.Id);
            Assert.Equal(
                original.Points.Select(point => (point.X + 25, point.Y + 15)),
                moved.Points.Select(point => (point.X, point.Y)));

            var capture = window.TryCapture();
            Assert.NotNull(capture);
            var parsed = LocalInkDocumentCodec.Parse(capture.VectorJson);
            Assert.Equal(
                moved.Points.Select(point => (point.X, point.Y)),
                parsed.Strokes.Single().Points.Select(point => (point.X, point.Y)));

            var undoButton = Assert.IsType<Button>(window.FindName("UndoButton"));
            undoButton.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(
                original.Points.Select(point => (point.X, point.Y)),
                window.Editor.Strokes.Single().Points.Select(point => (point.X, point.Y)));
            window.Close();
        });
    }

    [Fact]
    public void MovedMultipleSelectedStrokesUndoAndRedoAsOneStep()
    {
        wpf.Run(() =>
        {
            var window = new HandwritingWindow();
            window.Show();
            window.UpdateLayout();
            window.Editor.BeginStroke(new LocalInkPoint(20, 30, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(60, 70, 0.6));
            window.Editor.AppendPoint(new LocalInkPoint(100, 110, 0.4));
            var first = window.Editor.EndStroke();
            window.Editor.BeginStroke(new LocalInkPoint(160, 50, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(210, 90, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(250, 140, 0.5));
            var second = window.Editor.EndStroke();
            window.RefreshCanvasFromEditorForTest();
            window.UpdateLayout();

            var original = window.Editor.Strokes.ToArray();
            var canvas = Assert.IsType<InkCanvas>(window.FindName("Canvas"));
            canvas.Select(new StrokeCollection(canvas.Strokes));
            canvas.Strokes.Transform(
                new System.Windows.Media.Matrix(1, 0, 0, 1, 24, 16),
                applyToStylusTip: false);
            var onMoved = typeof(InkCanvas).GetMethod(
                "OnSelectionMoved",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onMoved);
            onMoved.Invoke(canvas, [EventArgs.Empty]);

            var moved = window.Editor.Strokes.ToArray();
            Assert.Equal(new[] { first!.Id, second!.Id }, moved.Select(stroke => stroke.Id));
            Assert.Equal(
                original.SelectMany(stroke => stroke.Points.Select(point => (point.X + 24, point.Y + 16))),
                moved.SelectMany(stroke => stroke.Points.Select(point => (point.X, point.Y))));

            var undoButton = Assert.IsType<Button>(window.FindName("UndoButton"));
            undoButton.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(
                original.SelectMany(stroke => stroke.Points.Select(point => (point.X, point.Y))),
                window.Editor.Strokes.SelectMany(stroke => stroke.Points.Select(point => (point.X, point.Y))));

            var redoButton = Assert.IsType<Button>(window.FindName("RedoButton"));
            redoButton.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(
                moved.SelectMany(stroke => stroke.Points.Select(point => (point.X, point.Y))),
                window.Editor.Strokes.SelectMany(stroke => stroke.Points.Select(point => (point.X, point.Y))));
            window.Close();
        });
    }

    [Fact]
    public void InvalidGroupMoveRestoresCanvasToLastValidInkState()
    {
        wpf.Run(() =>
        {
            var window = new HandwritingWindow();
            window.Show();
            window.UpdateLayout();
            window.Editor.BeginStroke(new LocalInkPoint(20, 30, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(60, 70, 0.6));
            window.Editor.AppendPoint(new LocalInkPoint(100, 110, 0.4));
            var first = window.Editor.EndStroke();
            window.Editor.BeginStroke(new LocalInkPoint(160, 50, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(210, 90, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(250, 140, 0.5));
            var second = window.Editor.EndStroke();
            window.RefreshCanvasFromEditorForTest();
            window.UpdateLayout();

            var original = window.Editor.Strokes.ToArray();
            var canvas = Assert.IsType<InkCanvas>(window.FindName("Canvas"));
            canvas.Select(new StrokeCollection(canvas.Strokes));
            canvas.Strokes.Transform(
                new System.Windows.Media.Matrix(1, 0, 0, 1, 5000, 5000),
                applyToStylusTip: false);
            var onMoved = typeof(InkCanvas).GetMethod(
                "OnSelectionMoved",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onMoved);
            onMoved.Invoke(canvas, [EventArgs.Empty]);

            Assert.Equal(new[] { first!.Id, second!.Id }, window.Editor.Strokes.Select(stroke => stroke.Id));
            Assert.Equal(
                original.SelectMany(stroke => stroke.Points.Select(point => (point.X, point.Y))),
                window.Editor.Strokes.SelectMany(stroke => stroke.Points.Select(point => (point.X, point.Y))));
            Assert.Equal(2, canvas.Strokes.Count);
            Assert.Equal(
                original.SelectMany(stroke => stroke.Points.Select(point => (point.X, point.Y))),
                HandwritingStrokeMapper.FromStrokes(canvas.Strokes)
                    .SelectMany(stroke => stroke.Points.Select(point => (point.X, point.Y))));
            var status = Assert.IsType<TextBlock>(window.FindName("StatusText"));
            Assert.Contains("恢复到上次有效状态", status.Text, StringComparison.Ordinal);

            var undoButton = Assert.IsType<Button>(window.FindName("UndoButton"));
            undoButton.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert.Single(window.Editor.Strokes);
            Assert.Single(canvas.Strokes);
            window.Close();
        });
    }
}
