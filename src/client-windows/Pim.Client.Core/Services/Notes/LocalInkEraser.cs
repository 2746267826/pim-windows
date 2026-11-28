namespace Pim.Client.Core.Services.Notes;

public enum LocalInkEraserMode
{
    Partial,
    WholeStroke,
}

internal sealed record LocalInkEraseResult(
    bool Changed,
    int ChangedStrokeCount,
    IReadOnlyList<LocalInkStroke> Strokes);

internal static class LocalInkEraser
{
    private const double Epsilon = 1e-9;

    public static LocalInkEraseResult Erase(
        IReadOnlyList<LocalInkStroke> strokes,
        IReadOnlyList<LocalInkPoint> eraserPoints,
        LocalInkEraserMode mode,
        double radius)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentNullException.ThrowIfNull(eraserPoints);
        if (eraserPoints.Count == 0)
        {
            return new LocalInkEraseResult(false, 0, strokes);
        }

        var segments = PathSegments(eraserPoints);
        var usedIds = strokes.Select(stroke => stroke.Id).ToHashSet();
        var next = new List<LocalInkStroke>(strokes.Count);
        var changed = 0;
        foreach (var stroke in strokes)
        {
            var hitRadius = radius + stroke.Width / 2;
            if (mode == LocalInkEraserMode.WholeStroke)
            {
                if (TouchesPath(stroke, segments, hitRadius))
                {
                    changed++;
                }
                else
                {
                    next.Add(stroke);
                }

                continue;
            }

            var pieces = SplitStroke(stroke, segments, hitRadius);
            if (pieces.Count == 1 && SamePoints(pieces[0], stroke.Points))
            {
                next.Add(stroke);
                continue;
            }

            changed++;
            for (var index = 0; index < pieces.Count; index++)
            {
                next.Add(stroke with
                {
                    Id = index == 0 ? stroke.Id : NewUniqueId(usedIds),
                    Points = pieces[index],
                });
            }
        }

        return new LocalInkEraseResult(changed > 0, changed, next);
    }

    private static IReadOnlyList<IReadOnlyList<LocalInkPoint>> SplitStroke(
        LocalInkStroke stroke,
        IReadOnlyList<Segment> eraserSegments,
        double radius)
    {
        var points = stroke.Points;
        if (points.Count == 1)
        {
            return eraserSegments.Any(segment =>
                DistanceSquaredToSegment(points[0], segment.Start, segment.End) <= radius * radius)
                ? []
                : [points];
        }

        var pieces = new List<IReadOnlyList<LocalInkPoint>>();
        var current = new List<LocalInkPoint>();
        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            var erased = ErasableIntervals(start, end, eraserSegments, radius);
            foreach (var kept in ComplementIntervals(erased))
            {
                if (kept.To - kept.From <= Epsilon)
                {
                    continue;
                }

                var first = InterpolatePoint(start, end, kept.From);
                var last = InterpolatePoint(start, end, kept.To);
                if (current.Count == 0)
                {
                    current.Add(first);
                }
                else if (!SamePosition(current[^1], first))
                {
                    pieces.Add(current.ToArray());
                    current = [first];
                }

                AddPosition(current, last);
                if (kept.To < 1 - Epsilon)
                {
                    pieces.Add(current.ToArray());
                    current = [];
                }
            }
        }

        if (current.Count > 0)
        {
            pieces.Add(current.ToArray());
        }

        return pieces;
    }

    private static bool TouchesPath(
        LocalInkStroke stroke,
        IReadOnlyList<Segment> eraserSegments,
        double radius)
    {
        var points = stroke.Points;
        if (points.Count == 1)
        {
            return eraserSegments.Any(segment =>
                DistanceSquaredToSegment(points[0], segment.Start, segment.End) <= radius * radius);
        }

        for (var index = 1; index < points.Count; index++)
        {
            if (ErasableIntervals(points[index - 1], points[index], eraserSegments, radius).Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static List<Interval> ErasableIntervals(
        LocalInkPoint start,
        LocalInkPoint end,
        IReadOnlyList<Segment> eraserSegments,
        double radius)
    {
        if (radius <= 0)
        {
            return [];
        }

        var intervals = new List<Interval>();
        foreach (var segment in eraserSegments)
        {
            var next = SegmentCapsuleIntervals(
                start,
                end,
                segment.Start,
                segment.End,
                radius);
            if (next.Count > 0)
            {
                intervals = MergeIntervals(intervals.Concat(next));
            }
        }

        return intervals;
    }

    private static List<Interval> SegmentCapsuleIntervals(
        LocalInkPoint start,
        LocalInkPoint end,
        LocalInkPoint eraserStart,
        LocalInkPoint eraserEnd,
        double radius)
    {
        var vx = end.X - start.X;
        var vy = end.Y - start.Y;
        var lengthSquared = vx * vx + vy * vy;
        if (lengthSquared <= Epsilon)
        {
            return DistanceSquaredToSegment(start, eraserStart, eraserEnd) <= radius * radius
                ? [new Interval(0, 1)]
                : [];
        }

        var wx = eraserEnd.X - eraserStart.X;
        var wy = eraserEnd.Y - eraserStart.Y;
        var eraserLengthSquared = wx * wx + wy * wy;
        if (eraserLengthSquared <= Epsilon)
        {
            var interval = CircleInterval(start, end, eraserStart, radius);
            return interval is null ? [] : [interval.Value];
        }

        var projectionStart =
            (start.X - eraserStart.X) * wx + (start.Y - eraserStart.Y) * wy;
        var projectionSpeed = vx * wx + vy * wy;
        var breakpoints = new List<double> { 0, 1 };
        if (Math.Abs(projectionSpeed) > Epsilon)
        {
            var atStart = -projectionStart / projectionSpeed;
            var atEnd = (eraserLengthSquared - projectionStart) / projectionSpeed;
            if (atStart > Epsilon && atStart < 1 - Epsilon)
            {
                breakpoints.Add(atStart);
            }

            if (atEnd > Epsilon && atEnd < 1 - Epsilon)
            {
                breakpoints.Add(atEnd);
            }
        }

        breakpoints.Sort();
        var intervals = new List<Interval>();
        for (var index = 1; index < breakpoints.Count; index++)
        {
            var from = breakpoints[index - 1];
            var to = breakpoints[index];
            if (to - from <= Epsilon)
            {
                continue;
            }

            var middle = (from + to) / 2;
            var projection = projectionStart + projectionSpeed * middle;
            Interval? interval;
            if (projection <= 0)
            {
                interval = CircleInterval(start, end, eraserStart, radius);
            }
            else if (projection >= eraserLengthSquared)
            {
                interval = CircleInterval(start, end, eraserEnd, radius);
            }
            else
            {
                interval = InfiniteLineInterval(
                    start,
                    end,
                    eraserStart,
                    wx,
                    wy,
                    eraserLengthSquared,
                    radius);
            }

            if (interval is { } clipped
                && IntersectInterval(clipped, new Interval(from, to)) is { } intersection)
            {
                intervals.Add(intersection);
            }
        }

        return MergeIntervals(intervals);
    }

    private static Interval? CircleInterval(
        LocalInkPoint start,
        LocalInkPoint end,
        LocalInkPoint center,
        double radius)
    {
        var vx = end.X - start.X;
        var vy = end.Y - start.Y;
        var dx = start.X - center.X;
        var dy = start.Y - center.Y;
        var a = vx * vx + vy * vy;
        var b = 2 * (dx * vx + dy * vy);
        var c = dx * dx + dy * dy - radius * radius;
        if (a <= Epsilon)
        {
            return c <= 0 ? new Interval(0, 1) : null;
        }

        var discriminant = b * b - 4 * a * c;
        if (discriminant < 0)
        {
            return null;
        }

        var root = Math.Sqrt(discriminant);
        var first = (-b - root) / (2 * a);
        var second = (-b + root) / (2 * a);
        return IntersectInterval(
            new Interval(Math.Min(first, second), Math.Max(first, second)),
            new Interval(0, 1));
    }

    private static Interval? InfiniteLineInterval(
        LocalInkPoint start,
        LocalInkPoint end,
        LocalInkPoint eraserStart,
        double wx,
        double wy,
        double eraserLengthSquared,
        double radius)
    {
        var startCross = wx * (start.Y - eraserStart.Y)
            - wy * (start.X - eraserStart.X);
        var speedCross = wx * (end.Y - start.Y)
            - wy * (end.X - start.X);
        var limit = radius * Math.Sqrt(eraserLengthSquared);
        if (Math.Abs(speedCross) <= Epsilon)
        {
            return Math.Abs(startCross) <= limit ? new Interval(0, 1) : null;
        }

        var first = (-limit - startCross) / speedCross;
        var second = (limit - startCross) / speedCross;
        return new Interval(Math.Min(first, second), Math.Max(first, second));
    }

    private static Interval? IntersectInterval(Interval first, Interval second)
    {
        var from = Math.Max(first.From, second.From);
        var to = Math.Min(first.To, second.To);
        return to - from > Epsilon ? new Interval(from, to) : null;
    }

    private static List<Interval> MergeIntervals(IEnumerable<Interval> intervals)
    {
        var sorted = intervals
            .Select(interval => new Interval(
                Math.Max(0, interval.From),
                Math.Min(1, interval.To)))
            .Where(interval => interval.To - interval.From > Epsilon)
            .OrderBy(interval => interval.From)
            .ToList();
        if (sorted.Count == 0)
        {
            return [];
        }

        var merged = new List<Interval> { sorted[0] };
        foreach (var interval in sorted.Skip(1))
        {
            var last = merged[^1];
            if (interval.From <= last.To + Epsilon)
            {
                merged[^1] = new Interval(last.From, Math.Max(last.To, interval.To));
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }

    private static List<Interval> ComplementIntervals(IReadOnlyList<Interval> intervals)
    {
        if (intervals.Count == 0)
        {
            return [new Interval(0, 1)];
        }

        var kept = new List<Interval>();
        var cursor = 0d;
        foreach (var interval in intervals)
        {
            if (interval.From > cursor + Epsilon)
            {
                kept.Add(new Interval(cursor, interval.From));
            }

            cursor = Math.Max(cursor, interval.To);
        }

        if (cursor < 1 - Epsilon)
        {
            kept.Add(new Interval(cursor, 1));
        }

        return kept;
    }

    private static IReadOnlyList<Segment> PathSegments(IReadOnlyList<LocalInkPoint> points)
    {
        if (points.Count == 0)
        {
            return [];
        }

        if (points.Count == 1)
        {
            return [new Segment(points[0], points[0])];
        }

        var segments = new List<Segment>(points.Count - 1);
        for (var index = 1; index < points.Count; index++)
        {
            segments.Add(new Segment(points[index - 1], points[index]));
        }

        return segments;
    }

    private static double DistanceSquaredToSegment(
        LocalInkPoint point,
        LocalInkPoint start,
        LocalInkPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= Epsilon)
        {
            return (point.X - start.X) * (point.X - start.X)
                + (point.Y - start.Y) * (point.Y - start.Y);
        }

        var projection = Math.Clamp(
            ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared,
            0,
            1);
        var projectedX = start.X + projection * dx;
        var projectedY = start.Y + projection * dy;
        return (point.X - projectedX) * (point.X - projectedX)
            + (point.Y - projectedY) * (point.Y - projectedY);
    }

    private static LocalInkPoint InterpolatePoint(
        LocalInkPoint start,
        LocalInkPoint end,
        double amount)
    {
        if (amount <= Epsilon)
        {
            return start;
        }

        if (amount >= 1 - Epsilon)
        {
            return end;
        }

        return new LocalInkPoint(
            start.X + (end.X - start.X) * amount,
            start.Y + (end.Y - start.Y) * amount,
            start.Pressure + (end.Pressure - start.Pressure) * amount);
    }

    private static void AddPosition(List<LocalInkPoint> points, LocalInkPoint next)
    {
        if (points.Count > 0 && SamePosition(points[^1], next))
        {
            return;
        }

        points.Add(next);
    }

    private static bool SamePosition(LocalInkPoint first, LocalInkPoint second)
        => Math.Abs(first.X - second.X) <= Epsilon
            && Math.Abs(first.Y - second.Y) <= Epsilon;

    private static bool SamePoints(
        IReadOnlyList<LocalInkPoint> first,
        IReadOnlyList<LocalInkPoint> second)
        => first.Count == second.Count
            && first.Select((point, index) =>
                SamePosition(point, second[index])
                && Math.Abs(point.Pressure - second[index].Pressure) <= Epsilon)
                .All(equal => equal);

    private static Guid NewUniqueId(HashSet<Guid> usedIds)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (id == Guid.Empty || !usedIds.Add(id));

        return id;
    }

    private readonly record struct Interval(double From, double To);
    private readonly record struct Segment(LocalInkPoint Start, LocalInkPoint End);
}
