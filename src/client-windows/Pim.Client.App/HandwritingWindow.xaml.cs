using System.IO;
using System.Globalization;
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
    private static readonly IReadOnlyList<(string Color, string Label)> BrushColorLabels =
    [
        (LocalInkPalette.InkColor, "墨色"),
        (LocalInkPalette.RedColor, "红"),
        (LocalInkPalette.BlueColor, "蓝"),
        (LocalInkPalette.GreenColor, "绿"),
    ];

    private static readonly IReadOnlyList<(double Width, string Label)> BrushWidthLabels =
    [
        (LocalInkPalette.ThinWidth, "细"),
        (LocalInkPalette.MediumWidth, "中"),
        (LocalInkPalette.ThickWidth, "粗"),
    ];

    private readonly LocalInkEditor _editor;
    private readonly LocalInkPreferenceStore? _preferences;
    private LocalInkClipboard? _clipboard;
    private LocalInkStyle _brush;
    private LocalInkEraserMode _eraserMode = LocalInkEraserMode.Partial;
    private bool _eraserToolActive;
    private bool _eraserDrawing;
    private readonly List<LocalInkPoint> _eraserPoints = [];
    private bool _refreshingCanvas;

    public HandwritingWindow()
        : this(
            new LocalInkPreferenceStore(LocalInkPreferenceStore.DefaultPath),
            null)
    {
    }

    public HandwritingWindow(LocalInkDocument document)
        : this(
            new LocalInkPreferenceStore(LocalInkPreferenceStore.DefaultPath),
            document)
    {
    }

    internal HandwritingWindow(
        LocalInkPreferenceStore? preferences,
        LocalInkDocument? document)
    {
        var validated = document is null
            ? null
            : LocalInkDocumentCodec.Parse(LocalInkDocumentCodec.Serialize(document));
        InitializeComponent();
        _preferences = preferences;
        _brush = preferences?.Load() ?? LocalInkStyle.Default;
        _editor = validated is null
            ? new LocalInkEditor(680, 420)
            : new LocalInkEditor(validated);
        _editor.Brush = _brush;
        BrushPopup.PlacementTarget = InkModeButton;
        EraserPopup.PlacementTarget = EraserButton;
        InitializeEditor();
        if (validated is not null)
        {
            HandwritingTitle.Text = "编辑手写";
            SaveButton.Content = "保存新版本";
        }
    }

    public HandwritingCapture? Capture { get; private set; }

    internal LocalInkEditor Editor => _editor;
    internal LocalInkEraserMode EraserModeForTest => _eraserMode;

    internal int ApplyEraserForTest(
        IReadOnlyList<LocalInkPoint> points,
        LocalInkEraserMode mode)
        => ApplyEraserGesture(points, mode);

    internal HandwritingCapture? TryCapture()
    {
        if (!_editor.HasStrokes)
        {
            return null;
        }

        ClearCanvasSelection();
        Canvas.EditingMode = InkCanvasEditingMode.Ink;

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
        ApplyBrushToCanvas();
        RefreshCanvasFromEditor();
        UpdateToolUi();
    }

    private void ApplyBrushToCanvas()
    {
        _editor.Brush = _brush;
        Canvas.DefaultDrawingAttributes = HandwritingStrokeMapper.ToDrawingAttributes(_brush);
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

    private void OnSelectionResizing(object sender, InkCanvasSelectionEditingEventArgs e)
    {
        e.Cancel = true;
        StatusText.Text = "当前手写源保存坐标与压力但不保存缩放后的笔宽；已取消缩放，可拖动整笔位置。";
    }

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
            StatusText.Text = "移动后的笔迹无效，已恢复到上次有效状态。";
        }

        UpdateCommandState();
    }

    private void OnInkMode(object sender, RoutedEventArgs e)
        => OnBrushSettings(sender, e);

    private void OnBrushSettings(object sender, RoutedEventArgs e)
    {
        ClearCanvasSelection();
        Canvas.EditingMode = InkCanvasEditingMode.Ink;
        _eraserToolActive = false;
        _eraserPoints.Clear();
        EraserPopup.IsOpen = false;
        BrushPopup.IsOpen = true;
        ApplyBrushToCanvas();
        UpdateToolUi();
        StatusText.Text = "画笔模式：鼠标按下、移动、抬起形成独立笔迹。";
    }

    private void OnBrushColorSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string color })
        {
            return;
        }

        try
        {
            _brush = new LocalInkStyle(color, _brush.Width);
        }
        catch (ArgumentException)
        {
            return;
        }

        ApplyBrushChoice();
    }

    private void OnBrushWidthSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string rawWidth }
            || !double.TryParse(
                rawWidth,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var width))
        {
            return;
        }

        try
        {
            _brush = new LocalInkStyle(_brush.Color, width);
        }
        catch (ArgumentException)
        {
            return;
        }

        ApplyBrushChoice();
    }

    private void ApplyBrushChoice()
    {
        Canvas.EditingMode = InkCanvasEditingMode.Ink;
        _eraserToolActive = false;
        _eraserPoints.Clear();
        ApplyBrushToCanvas();
        _preferences?.Save(_brush);
        UpdateToolUi();
        StatusText.Text = $"画笔：{ColorLabel(_brush.Color)} · {WidthLabel(_brush.Width)}；只影响之后的新笔。";
    }

    private void OnBrushDone(object sender, RoutedEventArgs e)
    {
        BrushPopup.IsOpen = false;
        Canvas.Focus();
    }

    private void OnEraserSettings(object sender, RoutedEventArgs e)
    {
        _eraserToolActive = true;
        _eraserPoints.Clear();
        ClearCanvasSelection();
        Canvas.EditingMode = InkCanvasEditingMode.None;
        BrushPopup.IsOpen = false;
        EraserPopup.IsOpen = true;
        UpdateToolUi();
        StatusText.Text = EraserStatusText();
    }

    private void ClearCanvasSelection()
    {
        if (Canvas.GetSelectedStrokes().Count > 0)
        {
            Canvas.Select(new StrokeCollection());
        }
    }

    private void OnPartialEraser(object sender, RoutedEventArgs e)
    {
        _eraserMode = LocalInkEraserMode.Partial;
        _eraserToolActive = true;
        _eraserPoints.Clear();
        Canvas.EditingMode = InkCanvasEditingMode.None;
        EraserPopup.IsOpen = false;
        UpdateToolUi();
        StatusText.Text = EraserStatusText();
    }

    private void OnWholeEraser(object sender, RoutedEventArgs e)
    {
        _eraserMode = LocalInkEraserMode.WholeStroke;
        _eraserToolActive = true;
        _eraserPoints.Clear();
        Canvas.EditingMode = InkCanvasEditingMode.None;
        EraserPopup.IsOpen = false;
        UpdateToolUi();
        StatusText.Text = EraserStatusText();
    }

    private void OnSelectMode(object sender, RoutedEventArgs e)
    {
        _eraserToolActive = false;
        _eraserPoints.Clear();
        Canvas.EditingMode = InkCanvasEditingMode.Select;
        BrushPopup.IsOpen = false;
        EraserPopup.IsOpen = false;
        UpdateToolUi();
        StatusText.Text = "选择模式：点击笔迹后可删除、复制或粘贴。";
    }

    private void OnCanvasPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_eraserToolActive || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var point = ToInkPoint(e.GetPosition(Canvas));
        e.Handled = true;
        _eraserDrawing = true;
        _eraserPoints.Clear();
        _eraserPoints.Add(point);
        Canvas.CaptureMouse();
        Canvas.Focus();
    }

    private void OnCanvasPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_eraserDrawing)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishEraserGesture();
            return;
        }

        AddEraserPoint(ToInkPoint(e.GetPosition(Canvas)));
        e.Handled = true;
    }

    private void OnCanvasPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_eraserDrawing)
        {
            return;
        }

        AddEraserPoint(ToInkPoint(e.GetPosition(Canvas)));
        e.Handled = true;
        FinishEraserGesture();
    }

    private void OnCanvasLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_eraserDrawing)
        {
            FinishEraserGesture();
        }
    }

    private void AddEraserPoint(LocalInkPoint point)
    {
        if (_eraserPoints.Count == 0)
        {
            _eraserPoints.Add(point);
            return;
        }

        var previous = _eraserPoints[^1];
        var distance = Math.Sqrt(
            Math.Pow(point.X - previous.X, 2)
            + Math.Pow(point.Y - previous.Y, 2));
        if (distance >= 0.5)
        {
            _eraserPoints.Add(point);
        }
    }

    private void FinishEraserGesture()
    {
        if (!_eraserDrawing)
        {
            return;
        }

        _eraserDrawing = false;
        if (Canvas.IsMouseCaptured)
        {
            Canvas.ReleaseMouseCapture();
        }

        var points = _eraserPoints.ToArray();
        _eraserPoints.Clear();
        if (points.Length == 0)
        {
            return;
        }

        ApplyEraserGesture(points, _eraserMode);
    }

    private int ApplyEraserGesture(
        IReadOnlyList<LocalInkPoint> points,
        LocalInkEraserMode mode)
    {
        try
        {
            var changed = _editor.Erase(points, mode);
            if (changed > 0)
            {
                RefreshCanvasFromEditor();
                StatusText.Text = $"已擦除 {changed} 笔的触碰范围；一次拖动可一步撤销。";
            }
            else
            {
                StatusText.Text = "橡皮未碰到笔迹，画布未修改。";
            }

            UpdateCommandState();
            return changed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            StatusText.Text = ex.Message;
            return 0;
        }
    }

    private LocalInkPoint ToInkPoint(System.Windows.Point point)
        => new(
            Math.Clamp(point.X, 0, Math.Max(0, Canvas.Width)),
            Math.Clamp(point.Y, 0, Math.Max(0, Canvas.Height)),
            1);

    private string EraserStatusText()
        => _eraserMode == LocalInkEraserMode.Partial
            ? "橡皮：局部擦除。拖过笔迹中段会保留两侧，一次拖动可一步撤销。"
            : "橡皮：整笔擦除。碰到任意部分即删除整笔，一次拖动可一步撤销。";

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

    private void UpdateToolUi()
    {
        var colorLabel = ColorLabel(_brush.Color);
        var widthLabel = WidthLabel(_brush.Width);
        InkModeButton.Content = $"画笔 · {colorLabel} · {widthLabel}";
        BrushStateText.Text = $"画笔 · {colorLabel} · {widthLabel}";
        EraserButton.Content = _eraserMode == LocalInkEraserMode.Partial
            ? "橡皮 · 局部"
            : "橡皮 · 整笔";
    }

    private static string ColorLabel(string color)
        => BrushColorLabels.FirstOrDefault(option =>
                string.Equals(option.Color, color, StringComparison.OrdinalIgnoreCase))
            .Label ?? "墨色";

    private static string WidthLabel(double width)
        => BrushWidthLabels.FirstOrDefault(option =>
                Math.Abs(option.Width - width) <= 1e-9)
            .Label ?? "中";
}
