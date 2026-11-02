using System.IO;
using Pim.Client.Core.Services.Notes;
using System.Windows.Ink;
using System.Windows.Input;

namespace Pim.Client.App;

internal static class HandwritingStrokeMapper
{
    private static readonly Guid StrokeIdProperty = new("a2c08a04-765b-4bb2-9b06-c7cf9c41ea02");

    public static Stroke ToStroke(LocalInkStroke source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var points = new StylusPointCollection();
        foreach (var point in source.Points)
        {
            points.Add(new StylusPoint(point.X, point.Y, (float)point.Pressure));
        }

        var stroke = new Stroke(points);
        stroke.AddPropertyData(StrokeIdProperty, source.Id.ToString("D"));
        return stroke;
    }

    public static LocalInkStroke FromStroke(Stroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        Guid id;
        if (stroke.ContainsPropertyData(StrokeIdProperty))
        {
            var rawId = stroke.GetPropertyData(StrokeIdProperty)?.ToString();
            if (!Guid.TryParse(rawId, out id) || id == Guid.Empty)
            {
                throw new InvalidDataException("笔迹标识损坏，已拒绝静默更换原材料身份。");
            }
        }
        else
        {
            id = Guid.NewGuid();
            stroke.AddPropertyData(StrokeIdProperty, id.ToString("D"));
        }

        return new LocalInkStroke(
            id,
            stroke.StylusPoints
                .Select(point => new LocalInkPoint(point.X, point.Y, point.PressureFactor))
                .ToArray());
    }

    public static IReadOnlyList<LocalInkStroke> FromStrokes(StrokeCollection strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        var seen = new HashSet<Guid>();
        var mapped = new List<LocalInkStroke>(strokes.Count);
        foreach (var stroke in strokes)
        {
            var local = FromStroke(stroke);
            if (!seen.Add(local.Id))
            {
                local = local with { Id = Guid.NewGuid() };
                stroke.RemovePropertyData(StrokeIdProperty);
                stroke.AddPropertyData(StrokeIdProperty, local.Id.ToString("D"));
                seen.Add(local.Id);
            }

            mapped.Add(local);
        }

        return mapped;
    }
}
