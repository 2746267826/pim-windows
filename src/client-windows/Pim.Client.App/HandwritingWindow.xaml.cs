using System.IO;
using System.Text.Json;
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

        var strokes = Canvas.Strokes
            .Select(stroke => stroke.StylusPoints
                .Select(point => new InkPoint((float)point.X, (float)point.Y, point.PressureFactor))
                .ToArray())
            .ToArray();
        var document = new InkDocument(1, width, height, strokes);
        Capture = new HandwritingCapture(
            png.ToArray(),
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = false }),
            width,
            height);
        DialogResult = true;
    }

    private sealed record InkDocument(
        int SchemaVersion,
        int Width,
        int Height,
        IReadOnlyList<InkPoint[]> Strokes);

    private sealed record InkPoint(float X, float Y, float Pressure);
}
