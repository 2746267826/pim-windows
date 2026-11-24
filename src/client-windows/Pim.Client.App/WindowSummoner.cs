using System.Windows;
using System.Windows.Media;
using Pim.Client.Core.Services;
using Forms = System.Windows.Forms;

namespace Pim.Client.App;

internal static class WindowSummoner
{
    internal static TWindow ShowOrActivate<TWindow>(
        IEnumerable<TWindow> windows,
        Func<TWindow> factory)
        where TWindow : Window
    {
        var existing = windows.FirstOrDefault();
        if (existing is not null)
        {
            BringToFront(existing);
            return existing;
        }

        var created = factory() ?? throw new InvalidOperationException("窗口工厂未返回窗口。");
        created.Show();
        BringToFront(created);
        return created;
    }

    internal static void BringToFront(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        EnsureVisibleOnScreen(window);
        window.Activate();
    }

    internal static void EnsureVisibleOnScreen(Window window)
    {
        if (double.IsNaN(window.Left)
            || double.IsNaN(window.Top)
            || double.IsNaN(window.Width)
            || double.IsNaN(window.Height)
            || window.Width <= 0
            || window.Height <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var workAreas = Forms.Screen.AllScreens
            .Select(screen => new WindowRectangle(
                screen.WorkingArea.X / dpi.DpiScaleX,
                screen.WorkingArea.Y / dpi.DpiScaleY,
                screen.WorkingArea.Width / dpi.DpiScaleX,
                screen.WorkingArea.Height / dpi.DpiScaleY))
            .ToArray();
        if (workAreas.Length == 0)
        {
            return;
        }

        var current = new WindowRectangle(window.Left, window.Top, window.Width, window.Height);
        var visible = WindowPlacement.EnsureVisible(current, workAreas);
        if (visible == current)
        {
            return;
        }

        var wasMaximized = window.WindowState == WindowState.Maximized;
        if (wasMaximized)
        {
            var restore = window.RestoreBounds;
            if (restore.IsEmpty || restore.Width <= 0 || restore.Height <= 0)
            {
                return;
            }

            visible = WindowPlacement.EnsureVisible(
                new WindowRectangle(restore.X, restore.Y, restore.Width, restore.Height),
                workAreas);
            window.WindowState = WindowState.Normal;
        }

        window.Left = visible.X;
        window.Top = visible.Y;
        window.Width = visible.Width;
        window.Height = visible.Height;
        if (wasMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }
}
