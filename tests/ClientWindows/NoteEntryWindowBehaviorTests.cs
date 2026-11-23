using System.Windows;
using Pim.Client.App;
using Xunit;
using WpfApplication = System.Windows.Application;

namespace Pim.Client.App.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class NoteEntryWindowBehaviorTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void RepeatedSummoningReusesAndRestoresTheExistingWindow()
    {
        wpf.Run(() =>
        {
            var created = 0;
            var windows = WpfApplication.Current.Windows.OfType<SummonWindow>();
            var factory = () =>
            {
                created++;
                return new SummonWindow();
            };

            var first = WindowSummoner.ShowOrActivate(windows, factory);
            first.WindowState = WindowState.Minimized;

            var second = WindowSummoner.ShowOrActivate(
                WpfApplication.Current.Windows.OfType<SummonWindow>(),
                factory);

            Assert.Same(first, second);
            Assert.Equal(1, created);
            Assert.Equal(WindowState.Normal, first.WindowState);
            Assert.True(first.IsVisible);
            first.Close();
        });
    }

    [Fact]
    public void ClosingAndSummoningAgainCreatesExactlyOneFreshWindow()
    {
        wpf.Run(() =>
        {
            var created = 0;
            var factory = () =>
            {
                created++;
                return new SummonWindow();
            };

            var first = WindowSummoner.ShowOrActivate(
                WpfApplication.Current.Windows.OfType<SummonWindow>(),
                factory);
            first.Close();
            Assert.Empty(WpfApplication.Current.Windows.OfType<SummonWindow>());

            var second = WindowSummoner.ShowOrActivate(
                WpfApplication.Current.Windows.OfType<SummonWindow>(),
                factory);

            Assert.NotSame(first, second);
            Assert.Equal(2, created);
            Assert.Single(WpfApplication.Current.Windows.OfType<SummonWindow>());
            second.Close();
        });
    }

    [Fact]
    public void SummoningAMaximizedWindowKeepsItMaximized()
    {
        wpf.Run(() =>
        {
            var window = WindowSummoner.ShowOrActivate(
                WpfApplication.Current.Windows.OfType<SummonWindow>(),
                () => new SummonWindow());
            window.WindowState = WindowState.Maximized;

            var summoned = WindowSummoner.ShowOrActivate(
                WpfApplication.Current.Windows.OfType<SummonWindow>(),
                () => throw new InvalidOperationException("must reuse the existing window"));

            Assert.Same(window, summoned);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            window.Close();
        });
    }

    [Fact]
    public void SummoningRepositionsAWindowLeftOnADisconnectedMonitor()
    {
        wpf.Run(() =>
        {
            var window = WindowSummoner.ShowOrActivate(
                WpfApplication.Current.Windows.OfType<SummonWindow>(),
                () => new SummonWindow());
            window.Left = SystemParameters.VirtualScreenLeft - 5000;
            window.Top = SystemParameters.VirtualScreenTop + 40;

            WindowSummoner.ShowOrActivate(
                WpfApplication.Current.Windows.OfType<SummonWindow>(),
                () => throw new InvalidOperationException("must reuse the existing window"));

            Assert.True(window.Left >= SystemParameters.VirtualScreenLeft);
            Assert.True(window.Top >= SystemParameters.VirtualScreenTop);
            Assert.True(window.Left + window.Width <=
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth);
            window.Close();
        });
    }

    private sealed class SummonWindow : Window
    {
        public SummonWindow()
        {
            Width = 900;
            Height = 640;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 100;
            Top = 100;
        }
    }
}
