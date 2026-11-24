namespace Pim.Client.Core.Services.Notes;

public sealed record LocalInkClipboard(Guid OwnerSessionId, IReadOnlyList<LocalInkStroke> Strokes);

public sealed class LocalInkEditor
{
    public const int MaxUndoDepth = 100;
    private const double MinStrokeLength = 0.5;
    private const double MinPointDistance = 0.01;
    private readonly int _width;
    private readonly int _height;
    private readonly List<LocalInkStroke> _strokes = [];
    private readonly List<LocalInkDocument> _undo = [];
    private readonly List<LocalInkDocument> _redo = [];
    private List<LocalInkPoint>? _activePoints;
    private double _activeLength;

    public LocalInkEditor(int width, int height)
    {
        ValidateCanvas(width, height);
        _width = width;
        _height = height;
    }

    public LocalInkEditor(LocalInkDocument document)
        : this(
            document?.Width ?? throw new ArgumentNullException(nameof(document)),
            document.Height,
            document.Strokes)
    {
    }

    public LocalInkEditor(int width, int height, IEnumerable<LocalInkStroke>? strokes)
        : this(width, height)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        var seen = new HashSet<Guid>();
        foreach (var stroke in strokes)
        {
            ValidateLoadedStroke(stroke, width, height);
            if (!seen.Add(stroke.Id))
            {
                throw new InvalidDataException("手写笔迹标识重复。");
            }

            _strokes.Add(CopyStroke(stroke));
        }
    }

    public int Width => _width;
    public int Height => _height;
    public Guid SessionId { get; } = Guid.NewGuid();
    public IReadOnlyList<LocalInkStroke> Strokes => _strokes.ToArray();
    public bool HasStrokes => _strokes.Count > 0;
    public bool IsDrawing => _activePoints is not null;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void BeginStroke(LocalInkPoint point)
    {
        if (IsDrawing)
        {
            throw new InvalidOperationException("上一笔尚未结束。");
        }

        ValidateInputPoint(point, _width, _height);
        _activePoints = [point];
        _activeLength = 0;
    }

    public void AppendPoint(LocalInkPoint point)
    {
        if (_activePoints is null)
        {
            throw new InvalidOperationException("没有正在书写的笔迹。");
        }

        ValidateInputPoint(point, _width, _height);
        var previous = _activePoints[^1];
        var distance = Math.Sqrt(
            Math.Pow(point.X - previous.X, 2) + Math.Pow(point.Y - previous.Y, 2));
        if (distance < MinPointDistance)
        {
            if (!previous.Pressure.Equals(point.Pressure))
            {
                _activePoints[^1] = point;
            }

            return;
        }

        _activePoints.Add(point);
        _activeLength += distance;
    }

    public LocalInkStroke? EndStroke()
    {
        if (_activePoints is null)
        {
            return null;
        }

        var points = _activePoints;
        var length = _activeLength;
        _activePoints = null;
        _activeLength = 0;
        if (points.Count < 2 || length < MinStrokeLength)
        {
            return null;
        }

        var stroke = new LocalInkStroke(Guid.NewGuid(), points.ToArray());
        AddConfirmedStroke(stroke);
        return stroke;
    }

    public void CancelStroke()
    {
        _activePoints = null;
        _activeLength = 0;
    }

    public LocalInkStroke? AddCompletedStroke(LocalInkStroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        ValidateNewStroke(stroke, _width, _height);
        if (IsDegenerate(stroke))
        {
            throw new InvalidDataException("空笔划或过短笔划不能作为手写块保存。");
        }

        var accepted = _strokes.Any(existing => existing.Id == stroke.Id)
            ? stroke with { Id = Guid.NewGuid() }
            : stroke;
        AddConfirmedStroke(CopyStroke(accepted));
        return accepted;
    }

    public int DeleteStrokes(IReadOnlyCollection<Guid> strokeIds)
    {
        ArgumentNullException.ThrowIfNull(strokeIds);
        if (strokeIds.Count == 0)
        {
            return 0;
        }

        var ids = strokeIds.ToHashSet();
        if (!_strokes.Any(stroke => ids.Contains(stroke.Id)))
        {
            return 0;
        }

        PushUndoSnapshot();
        var removed = _strokes.RemoveAll(stroke => ids.Contains(stroke.Id));
        _redo.Clear();
        return removed;
    }

    public int UpdateStrokeGeometry(IReadOnlyCollection<LocalInkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        if (IsDrawing)
        {
            throw new InvalidOperationException("正在书写时不能移动或缩放已有笔迹。");
        }

        if (strokes.Count == 0)
        {
            return 0;
        }

        var existingById = _strokes.ToDictionary(stroke => stroke.Id);
        var updates = new Dictionary<Guid, LocalInkStroke>();
        var changedIds = new HashSet<Guid>();
        foreach (var stroke in strokes)
        {
            if (stroke is null)
            {
                throw new InvalidDataException("笔迹更新数据不能为空。");
            }

            ValidateLoadedStroke(stroke, _width, _height);
            if (!updates.TryAdd(stroke.Id, stroke))
            {
                throw new InvalidDataException("同一次笔迹更新包含重复标识。");
            }

            if (!existingById.TryGetValue(stroke.Id, out var existing))
            {
                throw new InvalidOperationException("所选笔迹不属于当前手写窗口。");
            }

            if (stroke.Points.Count != existing.Points.Count)
            {
                throw new InvalidDataException("移动或缩放不能改变笔迹点数。");
            }

            if (!HasSameGeometry(existing, stroke))
            {
                if (IsDegenerate(stroke))
                {
                    throw new InvalidDataException("移动或缩放后的笔迹过短，已拒绝。");
                }

                changedIds.Add(stroke.Id);
            }
        }

        if (changedIds.Count == 0)
        {
            return 0;
        }

        PushUndoSnapshot();
        for (var index = 0; index < _strokes.Count; index++)
        {
            var existing = _strokes[index];
            if (changedIds.Contains(existing.Id))
            {
                _strokes[index] = CopyStroke(updates[existing.Id]);
            }
        }

        _redo.Clear();
        return changedIds.Count;
    }

    public bool Clear()
    {
        if (_strokes.Count == 0)
        {
            return false;
        }

        PushUndoSnapshot();
        _strokes.Clear();
        _redo.Clear();
        return true;
    }

    public bool Undo()
    {
        if (IsDrawing)
        {
            throw new InvalidOperationException("正在书写的笔迹尚未结束，不能撤销。");
        }

        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Add(CreateDocument());
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        ApplyDocument(previous);
        return true;
    }

    public bool Redo()
    {
        if (IsDrawing)
        {
            throw new InvalidOperationException("正在书写的笔迹尚未结束，不能重做。");
        }

        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Add(CreateDocument());
        TrimUndo();
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        ApplyDocument(next);
        return true;
    }

    public LocalInkClipboard Copy(IReadOnlyCollection<Guid> strokeIds)
    {
        ArgumentNullException.ThrowIfNull(strokeIds);
        if (strokeIds.Count == 0)
        {
            throw new ArgumentException("请先选择要复制的笔迹。", nameof(strokeIds));
        }

        var ids = strokeIds.ToHashSet();
        var selected = _strokes
            .Where(stroke => ids.Contains(stroke.Id))
            .Select(CopyStroke)
            .ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("所选笔迹不属于当前手写窗口。");
        }

        return new LocalInkClipboard(SessionId, selected);
    }

    public IReadOnlyList<LocalInkStroke> Paste(LocalInkClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (clipboard.OwnerSessionId != SessionId)
        {
            throw new InvalidOperationException("剪贴板笔迹不属于当前手写窗口，已拒绝跨笔记/账号粘贴。");
        }

        if (clipboard.Strokes.Count == 0)
        {
            throw new InvalidOperationException("剪贴板中没有可粘贴的笔迹。");
        }

        foreach (var stroke in clipboard.Strokes)
        {
            if (stroke is null)
            {
                throw new InvalidDataException("剪贴板包含空笔迹。");
            }

            ValidateLoadedStroke(stroke, _width, _height);
            if (IsDegenerate(stroke))
            {
                throw new InvalidDataException("剪贴板包含空笔划或过短笔划。");
            }
        }

        var allPoints = clipboard.Strokes.SelectMany(stroke => stroke.Points).ToArray();
        var offsetX = CalculatePasteOffset(
            allPoints.Min(point => point.X),
            allPoints.Max(point => point.X),
            _width);
        var offsetY = CalculatePasteOffset(
            allPoints.Min(point => point.Y),
            allPoints.Max(point => point.Y),
            _height);
        var pasted = clipboard.Strokes.Select(stroke => CopyStroke(stroke) with
        {
            Id = Guid.NewGuid(),
            Points = stroke.Points.Select(point => new LocalInkPoint(
                point.X + offsetX,
                point.Y + offsetY,
                point.Pressure)).ToArray(),
        }).ToArray();
        PushUndoSnapshot();
        _strokes.AddRange(pasted);
        _redo.Clear();
        return pasted;
    }

    public LocalInkDocument CreateDocument()
    {
        var seen = new HashSet<Guid>();
        foreach (var stroke in _strokes)
        {
            ValidateLoadedStroke(stroke, _width, _height);
            if (!seen.Add(stroke.Id))
            {
                throw new InvalidDataException("手写笔迹标识重复。");
            }
        }

        return new LocalInkDocument(1, _width, _height, _strokes.Select(CopyStroke).ToArray());
    }

    public string Serialize() => LocalInkDocumentCodec.Serialize(CreateDocument());

    private void AddConfirmedStroke(LocalInkStroke stroke)
    {
        PushUndoSnapshot();
        _strokes.Add(stroke);
        _redo.Clear();
    }

    private void PushUndoSnapshot()
    {
        _undo.Add(CreateDocument());
        TrimUndo();
    }

    private void TrimUndo()
    {
        if (_undo.Count > MaxUndoDepth)
        {
            _undo.RemoveRange(0, _undo.Count - MaxUndoDepth);
        }
    }

    private void ApplyDocument(LocalInkDocument document)
    {
        if (document.Width != _width || document.Height != _height)
        {
            throw new InvalidDataException("撤销记录画布尺寸不一致。");
        }

        _strokes.Clear();
        _strokes.AddRange(document.Strokes.Select(CopyStroke));
    }

    private static void ValidateCanvas(int width, int height)
    {
        if (width is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "画布宽度无效。");
        }

        if (height is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "画布高度无效。");
        }
    }

    private static void ValidateInputPoint(LocalInkPoint point, int width, int height)
    {
        if (!double.IsFinite(point.X)
            || !double.IsFinite(point.Y)
            || !double.IsFinite(point.Pressure)
            || point.X < -1
            || point.X > width + 1
            || point.Y < -1
            || point.Y > height + 1
            || point.Pressure is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(point), "手写坐标或压力无效。");
        }
    }

    private static void ValidateLoadedStroke(LocalInkStroke stroke, int width, int height)
    {
        if (stroke is null || stroke.Id == Guid.Empty || stroke.Points is null || stroke.Points.Count == 0)
        {
            throw new InvalidDataException("手写笔迹字段无效。");
        }

        foreach (var point in stroke.Points)
        {
            if (!double.IsFinite(point.X)
                || !double.IsFinite(point.Y)
                || !double.IsFinite(point.Pressure)
                || point.X < -1
                || point.X > width + 1
                || point.Y < -1
                || point.Y > height + 1
                || point.Pressure is < 0 or > 1)
            {
                throw new InvalidDataException("手写坐标或压力无效。");
            }
        }
    }

    private static void ValidateNewStroke(LocalInkStroke stroke, int width, int height)
    {
        if (stroke.Id == Guid.Empty || stroke.Points is null)
        {
            throw new InvalidDataException("手写笔迹字段无效。");
        }

        if (stroke.Points.Count == 0)
        {
            throw new InvalidDataException("空笔划不能作为手写块保存。");
        }

        ValidateLoadedStroke(stroke, width, height);
    }

    private static bool IsDegenerate(LocalInkStroke stroke)
    {
        if (stroke.Points.Count < 2)
        {
            return true;
        }

        var length = 0d;
        for (var index = 1; index < stroke.Points.Count; index++)
        {
            var previous = stroke.Points[index - 1];
            var point = stroke.Points[index];
            length += Math.Sqrt(
                Math.Pow(point.X - previous.X, 2) + Math.Pow(point.Y - previous.Y, 2));
        }

        return length < MinStrokeLength;
    }

    private static bool HasSameGeometry(LocalInkStroke existing, LocalInkStroke updated)
    {
        for (var index = 0; index < existing.Points.Count; index++)
        {
            var before = existing.Points[index];
            var after = updated.Points[index];
            if (!before.X.Equals(after.X)
                || !before.Y.Equals(after.Y)
                || !before.Pressure.Equals(after.Pressure))
            {
                return false;
            }
        }

        return true;
    }

    private static double CalculatePasteOffset(double minimum, double maximum, int limit)
    {
        var lower = -minimum;
        var upper = limit - maximum;
        return lower <= upper ? Math.Clamp(12, lower, upper) : 0;
    }

    private static LocalInkStroke CopyStroke(LocalInkStroke stroke)
        => stroke with
        {
            Points = stroke.Points
                .Select(point => new LocalInkPoint(point.X, point.Y, point.Pressure))
                .ToArray(),
        };
}
