using System.IO;
using Pim.Client.Core.Services.Notes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Ink;

namespace Pim.Client.App;

public sealed record HandwritingCapture(
    byte[] PngBytes,
    string VectorJson,
    int Width,
    int Height);

public partial class HandwritingWindow : Window
{
    public HandwritingWindow()
    {
        InitializeComponent();
        Canvas.Width = 680;
        Canvas.Height = 420;
    }

    public HandwritingWindow(LocalInkDocument document) : this()
    {
        ArgumentNullException.ThrowIfNull(document);
        var validated = LocalInkDocumentCodec.Parse(LocalInkDocumentCodec.Serialize(document));
        Canvas.Width = validated.Width;
        Canvas.Height = validated.Height;
        foreach (var sourceStroke in validated.Strokes)
        {
            Canvas.Strokes.Add(HandwritingStrokeMapper.ToStroke(sourceStroke));
        }

        HandwritingTitle.Text = "编辑手写";
        SaveButton.Content = "保存新版本";
    }

    public HandwritingCapture? Capture { get; private set; }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Canvas.Strokes.Clear();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (Canvas.Strokes.Count == 0)
        {
            MessageBox.Show(this, "请先写下手写内容。", "手写材料", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Canvas.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(Canvas.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(Canvas.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Canvas);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);

        var strokes = HandwritingStrokeMapper.FromStrokes(Canvas.Strokes);
        var document = new LocalInkDocument(1, width, height, strokes);
        Capture = new HandwritingCapture(
            png.ToArray(),
            LocalInkDocumentCodec.Serialize(document),
            width,
            height);
        DialogResult = true;
    }

}
