using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    [Fact]
    public void BrushToolsApplyFourColorsAndThreeWidthsToNewStrokesAndCaptureReopensStyles()
    {
        wpf.Run(() =>
        {
            var preferences = new LocalInkPreferenceStore(Path.Combine(
                Path.GetTempPath(),
                "pim-handwriting-preferences-" + Guid.NewGuid().ToString("N") + ".json"));
            var window = new HandwritingWindow(preferences, null);
            window.Show();
            window.UpdateLayout();

            var brushButton = Assert.IsType<Button>(window.FindName("InkModeButton"));
            brushButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var brushPopup = Assert.IsType<Popup>(window.FindName("BrushPopup"));
            Assert.True(brushPopup.IsOpen);

            Assert.Equal(LocalInkStyle.Default, window.Editor.Brush);
            var colorOptions = new[]
            {
                ("InkColorInkButton", LocalInkPalette.InkColor),
                ("InkColorRedButton", LocalInkPalette.RedColor),
                ("InkColorBlueButton", LocalInkPalette.BlueColor),
                ("InkColorGreenButton", LocalInkPalette.GreenColor),
            };
            foreach (var (buttonName, expectedColor) in colorOptions)
            {
                ClickPopupButton(window, "BrushPopup", buttonName);
                Assert.Equal(expectedColor, window.Editor.Brush.Color);
            }

            var widthOptions = new[]
            {
                ("InkWidthThinButton", LocalInkPalette.ThinWidth),
                ("InkWidthMediumButton", LocalInkPalette.MediumWidth),
                ("InkWidthThickButton", LocalInkPalette.ThickWidth),
            };
            foreach (var (buttonName, expectedWidth) in widthOptions)
            {
                ClickPopupButton(window, "BrushPopup", buttonName);
                Assert.Equal(expectedWidth, window.Editor.Brush.Width);
            }

            ClickPopupButton(window, "BrushPopup", "InkColorRedButton");
            ClickPopupButton(window, "BrushPopup", "InkWidthThickButton");
            Assert.Equal(LocalInkPalette.RedColor, window.Editor.Brush.Color);
            Assert.Equal(LocalInkPalette.ThickWidth, window.Editor.Brush.Width);
            ClickPopupButton(window, "BrushPopup", "BrushDoneButton");
            Assert.False(brushPopup.IsOpen);

            var canvas = Assert.IsType<InkCanvas>(window.FindName("Canvas"));
            Assert.Equal(
                LocalInkPalette.RedColor,
                $"#{canvas.DefaultDrawingAttributes.Color.R:X2}{canvas.DefaultDrawingAttributes.Color.G:X2}{canvas.DefaultDrawingAttributes.Color.B:X2}".ToLowerInvariant());
            Assert.Equal(LocalInkPalette.ThickWidth, canvas.DefaultDrawingAttributes.Width, 5);

            window.Editor.BeginStroke(new LocalInkPoint(20, 30, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(80, 90, 0.5));
            window.Editor.EndStroke();

            brushButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ClickPopupButton(window, "BrushPopup", "InkColorBlueButton");
            ClickPopupButton(window, "BrushPopup", "InkWidthThinButton");
            window.Editor.BeginStroke(new LocalInkPoint(120, 40, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(180, 100, 0.5));
            window.Editor.EndStroke();
            window.RefreshCanvasFromEditorForTest();
            window.UpdateLayout();

            var capture = window.TryCapture();
            Assert.NotNull(capture);
            var parsed = LocalInkDocumentCodec.Parse(capture.VectorJson);
            Assert.Equal(
                new[] { LocalInkPalette.RedColor, LocalInkPalette.BlueColor },
                parsed.Strokes.Select(stroke => stroke.Color));
            Assert.Equal(
                new[] { LocalInkPalette.ThickWidth, LocalInkPalette.ThinWidth },
                parsed.Strokes.Select(stroke => stroke.Width));

            var reopened = new HandwritingWindow(preferences, parsed);
            reopened.Show();
            reopened.UpdateLayout();
            var reopenedCanvas = Assert.IsType<InkCanvas>(reopened.FindName("Canvas"));
            Assert.Equal(2, reopenedCanvas.Strokes.Count);
            var reopenedAttributes = reopenedCanvas.Strokes.Select(stroke => (
                $"#{stroke.DrawingAttributes.Color.R:X2}{stroke.DrawingAttributes.Color.G:X2}{stroke.DrawingAttributes.Color.B:X2}".ToLowerInvariant(),
                stroke.DrawingAttributes.Width)).ToArray();
            Assert.Equal(
                new[]
                {
                    (LocalInkPalette.RedColor, LocalInkPalette.ThickWidth),
                    (LocalInkPalette.BlueColor, LocalInkPalette.ThinWidth),
                },
                reopenedAttributes);

            reopened.Close();
            window.Close();
            File.Delete(preferences.Path);
        });
    }

    [Fact]
    public void EraserToolSelectionGroupsOneGestureIntoOneUndoAndRedoStep()
    {
        wpf.Run(() =>
        {
            var preferences = new LocalInkPreferenceStore(Path.Combine(
                Path.GetTempPath(),
                "pim-handwriting-preferences-" + Guid.NewGuid().ToString("N") + ".json"));
            var window = new HandwritingWindow(preferences, null);
            window.Show();
            window.UpdateLayout();
            window.Editor.Brush = new LocalInkStyle(
                LocalInkPalette.RedColor,
                LocalInkPalette.ThickWidth);
            window.Editor.BeginStroke(new LocalInkPoint(20, 100, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(300, 100, 0.5));
            var crossed = window.Editor.EndStroke();
            window.Editor.Brush = new LocalInkStyle(
                LocalInkPalette.BlueColor,
                LocalInkPalette.ThinWidth);
            window.Editor.BeginStroke(new LocalInkPoint(20, 150, 0.5));
            window.Editor.AppendPoint(new LocalInkPoint(300, 150, 0.5));
            var untouched = window.Editor.EndStroke();
            window.RefreshCanvasFromEditorForTest();
            window.UpdateLayout();

            var canvas = Assert.IsType<InkCanvas>(window.FindName("Canvas"));
            Assert.Equal(2, canvas.Strokes.Count);
            var eraserButton = Assert.IsType<Button>(window.FindName("EraserButton"));
            eraserButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(InkCanvasEditingMode.None, canvas.EditingMode);
            ClickPopupButton(window, "EraserPopup", "PartialEraserButton");
            Assert.Equal(LocalInkEraserMode.Partial, window.EraserModeForTest);

            var changed = window.ApplyEraserForTest(
            [
                new LocalInkPoint(150, 70, 1),
                new LocalInkPoint(150, 130, 1),
            ],
                LocalInkEraserMode.Partial);
            Assert.Equal(1, changed);
            Assert.Equal(3, window.Editor.Strokes.Count);
            Assert.Equal(3, canvas.Strokes.Count);
            Assert.Equal(crossed!.Id, window.Editor.Strokes[0].Id);
            Assert.NotEqual(crossed.Id, window.Editor.Strokes[1].Id);
            Assert.Equal(untouched!.Id, window.Editor.Strokes[2].Id);
            var leftId = window.Editor.Strokes[0].Id;
            var rightId = window.Editor.Strokes[1].Id;

            var undoButton = Assert.IsType<Button>(window.FindName("UndoButton"));
            undoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, window.Editor.Strokes.Count);
            Assert.Equal(2, canvas.Strokes.Count);
            var redoButton = Assert.IsType<Button>(window.FindName("RedoButton"));
            redoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(3, window.Editor.Strokes.Count);
            Assert.Equal(3, canvas.Strokes.Count);

            eraserButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ClickPopupButton(window, "EraserPopup", "WholeEraserButton");
            Assert.Equal(LocalInkEraserMode.WholeStroke, window.EraserModeForTest);
            Assert.Equal(1, window.ApplyEraserForTest(
            [
                new LocalInkPoint(200, 80, 1),
                new LocalInkPoint(200, 120, 1),
            ],
                LocalInkEraserMode.WholeStroke));
            Assert.Equal(
                new[] { leftId, untouched.Id },
                window.Editor.Strokes.Select(stroke => stroke.Id));
            Assert.DoesNotContain(rightId, window.Editor.Strokes.Select(stroke => stroke.Id));
            Assert.Equal(2, canvas.Strokes.Count);

            window.Close();
            File.Delete(preferences.Path);
        });
    }

    private static void ClickPopupButton(Window window, string popupName, string buttonName)
    {
        var popup = Assert.IsType<Popup>(window.FindName(popupName));
        var content = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
        var button = window.FindName(buttonName) as Button
            ?? LogicalTreeHelper.FindLogicalNode(content, buttonName) as Button
            ?? content.FindName(buttonName) as Button;
        Assert.NotNull(button);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
