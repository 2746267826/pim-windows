using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Threading;
using Pim.Client.App;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class HandwritingWindowLayoutTests
{
    [Fact]
    public void NewCanvasHasDrawableWpfLayoutAndCancelLeavesNoCapture()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Pim.Client.App;component/Styles/Theme.xaml"),
                });

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
                var timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(25),
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    editWindow.DialogResult = false;
                };
                timer.Start();
                Assert.False(editWindow.ShowDialog().GetValueOrDefault());
                Assert.Null(editWindow.Capture);
                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
