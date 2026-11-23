using System.IO;
using Pim.Client.Core.Services.Notes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pim.Client.App;

public sealed record HandwritingCapture(
    byte[] PngBytes,
    string VectorJson,
    int Width,
    int Height);

public partial class HandwritingWindow : Window
{
    private readonly LocalInkEditor _editor;
    private LocalInkClipboard? _clipboard;
    private bool _refreshingCanvas;

    public HandwritingWindow()
    {
        InitializeComponent();
        _editor = new LocalInkEditor(680, 420);
        InitializeEditor();
    }

    public HandwritingWindow(LocalInkDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var validated = LocalInkDocumentCodec.Parse(LocalInkDocumentCodec.Serialize(document));
        InitializeComponent();
        _editor = new LocalInkEditor(validated);
        InitializeEditor();
        HandwritingTitle.Text = "编辑手写";
        SaveButton.Content = "保存新版本";
    }

    public HandwritingCapture? Capture { get; private set; }

    internal LocalInkEditor Editor => _editor;

    internal HandwritingCapture? TryCapture()
    {
        if (!_editor.HasStrokes)
        {
            return null;
        }

        Canvas.EditingMode = InkCanvasEditingMode.Ink;
        if (Canvas.GetSelectedStrokes().Count > 0)
        {
            Canvas.Select(new StrokeCollection());
        }

        Canvas.UpdateLayout();
        var width = _editor.Width;
        var height = _editor.Height;
        if (Canvas.ActualWidth < width || Canvas.ActualHeight < height)
        {
            Canvas.Measure(new System.Windows.Size(width, height));
            Canvas.Arrange(new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Canvas);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);

        return new HandwritingCapture(
            png.ToArray(),
            _editor.Serialize(),
            width,
            height);
    }

    internal void RefreshCanvasFromEditorForTest() => RefreshCanvasFromEditor();

    private void InitializeEditor()
    {
        Canvas.Width = _editor.Width;
        Canvas.Height = _editor.Height;
        RefreshCanvasFromEditor();
    }

    private void RefreshCanvasFromEditor()
    {
        _refreshingCanvas = true;
        try
        {
            Canvas.Strokes.Clear();
            foreach (var sourceStroke in _editor.Strokes)
            {
                Canvas.Strokes.Add(HandwritingStrokeMapper.ToStroke(sourceStroke));
            }

            if (Canvas.GetSelectedStrokes().Count > 0)
            {
                Canvas.Select(new StrokeCollection());
            }
        }
        finally
        {
            _refreshingCanvas = false;
        }

        UpdateCommandState();
    }

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (_refreshingCanvas)
        {
            return;
        }

        try
        {
            var local = HandwritingStrokeMapper.FromStroke(e.Stroke);
            var accepted = _editor.AddCompletedStroke(local);
            if (accepted is null)
            {
                Canvas.Strokes.Remove(e.Stroke);
                StatusText.Text = "空笔划已忽略；请按住鼠标拖动后再抬起。";
            }
            else if (accepted.Id != local.Id)
            {
                RefreshCanvasFromEditor();
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            Canvas.Strokes.Remove(e.Stroke);
            StatusText.Text = "空笔划或无效笔迹已忽略，原稿未修改。";
        }

        UpdateCommandState();
    }

    private void OnSelectionChanged(object sender, EventArgs e)
        => UpdateCommandState();

    private void OnSelectionMoved(object sender, EventArgs e)
        => SyncCanvasGeometryToEditor("已移动所选笔迹；保存会包含新位置。");

    private void OnSelectionResized(object sender, EventArgs e)
        => SyncCanvasGeometryToEditor("已缩放所选笔迹；保存会包含新尺寸。");

    private void SyncCanvasGeometryToEditor(string status)
    {
        if (_refreshingCanvas)
        {
            return;
        }

        try
        {
            var updated = _editor.UpdateStrokeGeometry(
                HandwritingStrokeMapper.FromStrokes(Canvas.Strokes));
            if (updated > 0)
            {
                StatusText.Text = $"{status} 已合并为一步，可撤销。";
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            RefreshCanvasFromEditor();
            StatusText.Text = "移动或缩放后的笔迹无效，已恢复到上次有效状态。";
        }

        UpdateCommandState();
    }

    private void OnInkMode(object sender, RoutedEventArgs e)
    {
        Canvas.EditingMode = InkCanvasEditingMode.Ink;
        StatusText.Text = "画笔模式：鼠标按下、移动、抬起形成独立笔迹。";
    }

    private void OnSelectMode(object sender, RoutedEventArgs e)
    {
        Canvas.EditingMode = InkCanvasEditingMode.Select;
        StatusText.Text = "选择模式：点击笔迹后可删除、复制或粘贴。";
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_editor.Undo())
            {
                RefreshCanvasFromEditor();
                StatusText.Text = "已撤销一步操作。";
            }
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_editor.Redo())
            {
                RefreshCanvasFromEditor();
                StatusText.Text = "已重做一步操作。";
            }
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnDeleteSelection(object sender, RoutedEventArgs e)
    {
        var ids = SelectedStrokeIds();
        if (ids.Length == 0)
        {
            StatusText.Text = "请先切换到选择模式并点击要删除的笔迹。";
            return;
        }

        var removed = _editor.DeleteStrokes(ids);
        RefreshCanvasFromEditor();
        StatusText.Text = removed > 0
            ? $"已删除 {removed} 笔；可撤销恢复。"
            : "所选笔迹已不在当前画布中。";
    }

    private void OnCopySelection(object sender, RoutedEventArgs e)
    {
        var ids = SelectedStrokeIds();
        if (ids.Length == 0)
        {
            StatusText.Text = "请先切换到选择模式并点击要复制的笔迹。";
            return;
        }

        try
        {
            _clipboard = _editor.Copy(ids);
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        UpdateCommandState();
        StatusText.Text = $"已复制 {_clipboard.Strokes.Count} 笔到当前手写窗口；不会写入系统剪贴板或跨账号粘贴。";
    }

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        if (_clipboard is null)
        {
            StatusText.Text = "当前手写窗口还没有复制过笔迹。";
            return;
        }

        try
        {
            var pasted = _editor.Paste(_clipboard);
            RefreshCanvasFromEditor();
            SelectStrokeIds(pasted.Select(stroke => stroke.Id));
            StatusText.Text = $"已粘贴 {pasted.Count} 笔；可撤销一次性恢复粘贴前状态。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        var count = _editor.Strokes.Count;
        if (!_editor.Clear())
        {
            StatusText.Text = "画布已是空白。";
            return;
        }

        RefreshCanvasFromEditor();
        StatusText.Text = $"已清除 {count} 笔；可撤销恢复。";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var capture = TryCapture();
        if (capture is null)
        {
            MessageBox.Show(this, "请先写下手写内容。", "手写材料", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Capture = capture;
        DialogResult = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (key)
            {
                case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                    OnRedo(sender, e);
                    e.Handled = true;
                    return;
                case Key.Z:
                    OnUndo(sender, e);
                    e.Handled = true;
                    return;
                case Key.Y:
                    OnRedo(sender, e);
                    e.Handled = true;
                    return;
                case Key.C:
                    OnCopySelection(sender, e);
                    e.Handled = true;
                    return;
                case Key.V:
                    OnPaste(sender, e);
                    e.Handled = true;
                    return;
            }

            return;
        }

        if (key is Key.Delete or Key.Back && SelectedStrokeIds().Length > 0)
        {
            OnDeleteSelection(sender, e);
            e.Handled = true;
        }
    }

    private Guid[] SelectedStrokeIds()
    {
        if (Canvas.GetSelectedStrokes().Count == 0)
        {
            return [];
        }

        var ids = new List<Guid>();
        foreach (var stroke in Canvas.GetSelectedStrokes())
        {
            try
            {
                ids.Add(HandwritingStrokeMapper.FromStroke(stroke).Id);
            }
            catch (InvalidDataException)
            {
                continue;
            }
        }

        return ids.ToArray();
    }

    private void SelectStrokeIds(IEnumerable<Guid> ids)
    {
        var selected = new HashSet<Guid>(ids);
        var strokes = Canvas.Strokes
            .Where(stroke =>
            {
                try
                {
                    return selected.Contains(HandwritingStrokeMapper.FromStroke(stroke).Id);
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            })
            .ToArray();
        if (strokes.Length > 0)
        {
            Canvas.Select(new StrokeCollection(strokes));
            Canvas.EditingMode = InkCanvasEditingMode.Select;
        }
    }

    private void UpdateCommandState()
    {
        UndoButton.IsEnabled = _editor.CanUndo && !_editor.IsDrawing;
        RedoButton.IsEnabled = _editor.CanRedo && !_editor.IsDrawing;
        ClearButton.IsEnabled = _editor.HasStrokes;
        SaveButton.IsEnabled = _editor.HasStrokes;
        PasteButton.IsEnabled = _clipboard is not null;
        var hasSelection = Canvas.GetSelectedStrokes().Count > 0;
        CopyButton.IsEnabled = hasSelection;
        DeleteSelectionButton.IsEnabled = hasSelection;
    }
}
