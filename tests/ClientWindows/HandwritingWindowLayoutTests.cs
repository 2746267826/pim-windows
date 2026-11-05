using System.Windows.Controls;
using System.Windows.Ink;
using Pim.Client.App;
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
}
