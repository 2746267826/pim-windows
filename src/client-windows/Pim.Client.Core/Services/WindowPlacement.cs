namespace Pim.Client.Core.Services;

public readonly record struct WindowRectangle(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

public static class WindowPlacement
{
    private const double MinimumVisibleExtent = 48;

    public static WindowRectangle EnsureVisible(
        WindowRectangle window,
        IReadOnlyList<WindowRectangle> workAreas)
    {
        if (workAreas.Count == 0)
        {
            return window;
        }

        var target = workAreas
            .Select(area => (Area: area, Intersection: IntersectionArea(window, area)))
            .OrderByDescending(candidate => candidate.Intersection)
            .Select(candidate => candidate.Area)
            .First();

        if (HasUsableIntersection(window, target)
            && window.Width <= target.Width
            && window.Height <= target.Height)
        {
            return window;
        }

        var width = Math.Min(Math.Max(window.Width, 0), target.Width);
        var height = Math.Min(Math.Max(window.Height, 0), target.Height);
        var x = Math.Clamp(window.X, target.X, Math.Max(target.X, target.Right - width));
        var y = Math.Clamp(window.Y, target.Y, Math.Max(target.Y, target.Bottom - height));
        return new WindowRectangle(x, y, width, height);
    }

    private static bool HasUsableIntersection(WindowRectangle window, WindowRectangle area)
    {
        var width = Math.Min(window.Right, area.Right) - Math.Max(window.X, area.X);
        var height = Math.Min(window.Bottom, area.Bottom) - Math.Max(window.Y, area.Y);
        return width >= MinimumVisibleExtent && height >= MinimumVisibleExtent;
    }

    private static double IntersectionArea(WindowRectangle left, WindowRectangle right)
    {
        var width = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.X, right.X));
        var height = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y));
        return width * height;
    }
}
