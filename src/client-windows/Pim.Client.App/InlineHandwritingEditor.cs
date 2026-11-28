using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

/// <summary>
/// Handwriting editing surface embedded in the note body. It deliberately is
/// not a Window: the user keeps the note, scroll position and surrounding
/// photo/text blocks visible while adding or revising ink.
/// </summary>
internal sealed class InlineHandwritingEditor : Border
{
    private readonly LocalInkEditor _editor;
    private readonly InkCanvas _canvas;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly Button _clearButton;
    private readonly TextBlock _statusText;
    private bool _refreshingCanvas;
    private bool _busy;

    public InlineHandwritingEditor(LocalInkDocument? document)
    {
        var validated = document is null
            ? null
            : LocalInkDocumentCodec.Parse(LocalInkDocumentCodec.Serialize(document));
        _editor = validated is null
            ? new LocalInkEditor(620, 320)
            : new LocalInkEditor(validated);
        _editor.Brush = LocalInkStyle.Default;

        Background = (Brush)Application.Current.FindResource("PimMutedSurfaceBrush");
        BorderBrush = (Brush)Application.Current.FindResource("PimBorderBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(12);
        Margin = new Thickness(0, 6, 0, 12);

        var root = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
        };
        DockPanel.SetDock(actions, Dock.Right);
        _saveButton = new Button
        {
            Content = "保存手写",
            Style = (Style)Application.Current.FindResource("PimPrimaryButton"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
        };
        _saveButton.Click += OnSave;
        _cancelButton = new Button
        {
            Content = "取消",
            Style = (Style)Application.Current.FindResource("PimSecondaryButton"),
            Padding = new Thickness(10, 4, 10, 4),
        };
        _cancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(_cancelButton);
        actions.Children.Add(_saveButton);
        header.Children.Add(actions);
        header.Children.Add(new TextBlock
        {
            Text = validated is null ? "正文内手写" : "正文内编辑手写",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("PimTextBrush"),
        });
        root.Children.Add(header);

        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _undoButton = ToolButton("撤销", OnUndo);
        _redoButton = ToolButton("重做", OnRedo);
        _clearButton = ToolButton("清空", OnClear);
        tools.Children.Add(_undoButton);
        tools.Children.Add(_redoButton);
        tools.Children.Add(_clearButton);
        root.Children.Add(tools);

        var canvasBorder = new Border
        {
            Background = Brushes.White,
            BorderBrush = (Brush)Application.Current.FindResource("PimBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _canvas = new InkCanvas
                {
                    Width = _editor.Width,
                    Height = _editor.Height,
                    Background = Brushes.White,
                    EditingMode = InkCanvasEditingMode.Ink,
                    DefaultDrawingAttributes =
                        HandwritingStrokeMapper.ToDrawingAttributes(_editor.Brush),
                },
            },
        };
        _canvas.StrokeCollected += OnStrokeCollected;
        root.Children.Add(canvasBorder);

        _statusText = new TextBlock
        {
            Text = "可直接用鼠标或触控笔书写；保存后生成新的本机固定版本。",
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("PimMutedTextBrush"),
        };
        root.Children.Add(_statusText);
        Child = root;
        RefreshCanvas();
    }

    public event EventHandler<HandwritingCapture>? SaveRequested;
    public event EventHandler? CancelRequested;

    internal LocalInkEditor Editor => _editor;
    internal InkCanvas Canvas => _canvas;

    public void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        UpdateCommandState();
        if (status is not null)
        {
            _statusText.Text = status;
        }
    }

    internal HandwritingCapture? TryCapture()
    {
        if (!_editor.HasStrokes)
        {
            return null;
        }

        _canvas.UpdateLayout();
        var width = _editor.Width;
        var height = _editor.Height;
        if (_canvas.ActualWidth < width || _canvas.ActualHeight < height)
        {
            _canvas.Measure(new System.Windows.Size(width, height));
            _canvas.Arrange(new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(_canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        return new HandwritingCapture(png.ToArray(), _editor.Serialize(), width, height);
    }

    private Button ToolButton(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)Application.Current.FindResource("PimSecondaryButton"),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(0, 0, 6, 0),
        };
        button.Click += handler;
        return button;
    }

    private void OnStrokeCollected(object? sender, InkCanvasStrokeCollectedEventArgs e)
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
                _canvas.Strokes.Remove(e.Stroke);
            }
            else if (accepted.Id != local.Id)
            {
                e.Stroke.RemovePropertyData(HandwritingStrokeMapper.StrokeIdProperty);
                e.Stroke.AddPropertyData(
                    HandwritingStrokeMapper.StrokeIdProperty,
                    accepted.Id.ToString("D"));
            }
        }
        catch (Exception ex)
        {
            _canvas.Strokes.Remove(e.Stroke);
            _statusText.Text = $"这笔没有写入：{ex.Message}";
        }

        UpdateCommandState();
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_editor.Undo())
        {
            RefreshCanvas();
        }
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_editor.Redo())
        {
            RefreshCanvas();
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (_editor.Clear())
        {
            RefreshCanvas();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var capture = TryCapture();
        if (capture is null)
        {
            _statusText.Text = "请先书写内容，再保存手写。";
            return;
        }

        SaveRequested?.Invoke(this, capture);
    }

    private void RefreshCanvas()
    {
        _refreshingCanvas = true;
        try
        {
            _canvas.Strokes.Clear();
            foreach (var stroke in _editor.Strokes)
            {
                _canvas.Strokes.Add(HandwritingStrokeMapper.ToStroke(stroke));
            }
        }
        finally
        {
            _refreshingCanvas = false;
        }

        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        _canvas.IsEnabled = !_busy;
        _saveButton.IsEnabled = !_busy && _editor.HasStrokes;
        _cancelButton.IsEnabled = !_busy;
        _undoButton.IsEnabled = !_busy && _editor.CanUndo;
        _redoButton.IsEnabled = !_busy && _editor.CanRedo;
        _clearButton.IsEnabled = !_busy && _editor.HasStrokes;
    }
}
