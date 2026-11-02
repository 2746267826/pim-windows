using System.Security.Cryptography;
using System.Text.Json;

namespace Pim.Client.Core.Services.Notes;

public sealed record LocalInkPoint(double X, double Y, double Pressure);

public sealed record LocalInkStroke(Guid Id, IReadOnlyList<LocalInkPoint> Points);

public sealed record LocalInkDocument(
    int SchemaVersion,
    int Width,
    int Height,
    IReadOnlyList<LocalInkStroke> Strokes);

public static class LocalInkDocumentCodec
{
    public const int MaxDocumentBytes = 16 * 1024 * 1024;
    private const int MaxStrokes = 10_000;
    private const int MaxPoints = 500_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static LocalInkDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length == 0 || json.Length > MaxDocumentBytes)
        {
            throw new InvalidDataException("手写源文件为空或超过读取上限。");
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        if (bytes.Length is 0 or > MaxDocumentBytes)
        {
            throw new InvalidDataException("手写源文件为空或超过读取上限。");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || ReadInt(root, "schemaVersion", "SchemaVersion") != 1)
            {
                throw InvalidInk();
            }

            var width = ReadInt(root, "width", "Width");
            var height = ReadInt(root, "height", "Height");
            if (width is < 1 or > 16_384 || height is < 1 or > 16_384)
            {
                throw InvalidInk();
            }

            var rawStrokes = ReadProperty(root, "strokes", "Strokes");
            if (rawStrokes.ValueKind != JsonValueKind.Array
                || rawStrokes.GetArrayLength() is 0 or > MaxStrokes)
            {
                throw InvalidInk();
            }

            var suppliedIds = TryReadProperty(root, "strokeIds", "StrokeIds", out var idsElement);
            if (suppliedIds
                && (idsElement.ValueKind != JsonValueKind.Array
                    || idsElement.GetArrayLength() != rawStrokes.GetArrayLength()))
            {
                throw InvalidInk();
            }

            var sourceHash = SHA256.HashData(bytes);
            var ids = new HashSet<Guid>();
            var strokes = new List<LocalInkStroke>(rawStrokes.GetArrayLength());
            var totalPoints = 0;
            var strokeIndex = 0;
            foreach (var rawStroke in rawStrokes.EnumerateArray())
            {
                var rawPoints = rawStroke.ValueKind == JsonValueKind.Object
                    ? ReadProperty(rawStroke, "points", "Points")
                    : rawStroke;
                if (rawPoints.ValueKind != JsonValueKind.Array || rawPoints.GetArrayLength() == 0)
                {
                    throw InvalidInk();
                }

                totalPoints = checked(totalPoints + rawPoints.GetArrayLength());
                if (totalPoints > MaxPoints)
                {
                    throw InvalidInk();
                }

                Guid strokeId;
                if (suppliedIds)
                {
                    var idText = idsElement[strokeIndex].ValueKind == JsonValueKind.String
                        ? idsElement[strokeIndex].GetString()
                        : null;
                    if (!Guid.TryParseExact(idText, "D", out strokeId) || strokeId == Guid.Empty)
                    {
                        throw InvalidInk();
                    }
                }
                else
                {
                    strokeId = StableStrokeId(sourceHash, strokeIndex);
                }

                if (!ids.Add(strokeId))
                {
                    throw InvalidInk();
                }

                var points = new List<LocalInkPoint>(rawPoints.GetArrayLength());
                foreach (var rawPoint in rawPoints.EnumerateArray())
                {
                    double x;
                    double y;
                    double pressure;
                    if (rawPoint.ValueKind == JsonValueKind.Array && rawPoint.GetArrayLength() == 3)
                    {
                        x = ReadFiniteNumber(rawPoint[0]);
                        y = ReadFiniteNumber(rawPoint[1]);
                        pressure = ReadFiniteNumber(rawPoint[2]);
                    }
                    else if (rawPoint.ValueKind == JsonValueKind.Object)
                    {
                        x = ReadNumber(rawPoint, "x", "X");
                        y = ReadNumber(rawPoint, "y", "Y");
                        pressure = ReadNumber(rawPoint, "pressure", "Pressure");
                    }
                    else
                    {
                        throw InvalidInk();
                    }

                    if (x < -1 || x > width + 1 || y < -1 || y > height + 1 || pressure is < 0 or > 1)
                    {
                        throw InvalidInk();
                    }

                    points.Add(new LocalInkPoint(x, y, pressure));
                }

                strokes.Add(new LocalInkStroke(strokeId, points));
                strokeIndex++;
            }

            return new LocalInkDocument(1, width, height, strokes);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("手写源 JSON 损坏。", ex);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("手写源包含超限数据。", ex);
        }
    }

    public static string Serialize(LocalInkDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = document.SchemaVersion,
            width = document.Width,
            height = document.Height,
            strokeIds = document.Strokes.Select(stroke => stroke.Id.ToString("D")).ToArray(),
            strokes = document.Strokes.Select(stroke => stroke.Points.Select(point => new
            {
                x = point.X,
                y = point.Y,
                pressure = point.Pressure,
            }).ToArray()).ToArray(),
        }, JsonOptions));

        return JsonSerializer.Serialize(new
        {
            schemaVersion = normalized.SchemaVersion,
            width = normalized.Width,
            height = normalized.Height,
            strokeIds = normalized.Strokes.Select(stroke => stroke.Id.ToString("D")).ToArray(),
            strokes = normalized.Strokes.Select(stroke => stroke.Points.Select(point => new
            {
                x = point.X,
                y = point.Y,
                pressure = point.Pressure,
            }).ToArray()).ToArray(),
        }, JsonOptions);
    }

    private static int ReadInt(JsonElement element, string camelCase, string pascalCase)
    {
        var property = ReadProperty(element, camelCase, pascalCase);
        return property.TryGetInt32(out var value) ? value : throw InvalidInk();
    }

    private static double ReadNumber(JsonElement element, string camelCase, string pascalCase)
        => ReadFiniteNumber(ReadProperty(element, camelCase, pascalCase));

    private static double ReadFiniteNumber(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var value)
            || !double.IsFinite(value))
        {
            throw InvalidInk();
        }

        return value;
    }

    private static JsonElement ReadProperty(JsonElement element, string camelCase, string pascalCase)
        => TryReadProperty(element, camelCase, pascalCase, out var value) ? value : throw InvalidInk();

    private static bool TryReadProperty(
        JsonElement element,
        string camelCase,
        string pascalCase,
        out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidInk();
        }

        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, camelCase, StringComparison.Ordinal)
                && !string.Equals(property.Name, pascalCase, StringComparison.Ordinal))
            {
                continue;
            }

            if (found)
            {
                throw InvalidInk();
            }

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static Guid StableStrokeId(byte[] sourceHash, int index)
    {
        Span<byte> material = stackalloc byte[sourceHash.Length + sizeof(int)];
        sourceHash.CopyTo(material);
        BitConverter.TryWriteBytes(material[sourceHash.Length..], index);
        var digest = SHA256.HashData(material);
        var id = new Guid(digest.AsSpan(0, 16));
        return id == Guid.Empty ? new Guid("00000000-0000-0000-0000-000000000001") : id;
    }

    private static InvalidDataException InvalidInk()
        => new("手写源字段、笔画顺序、压力或坐标无效。");
}
