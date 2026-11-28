using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pim.Client.Core.Services.Notes;

public sealed record LocalInkUnknownProperty(string Name, JsonElement Value);

public sealed record LocalInkPoint(double X, double Y, double Pressure)
{
    public IReadOnlyList<LocalInkUnknownProperty>? UnknownProperties { get; init; }
}

public sealed record LocalInkStroke(
    Guid Id,
    IReadOnlyList<LocalInkPoint> Points,
    string Color = LocalInkPalette.LegacyColor,
    double Width = LocalInkPalette.LegacyWidth)
{
    public LocalInkStyle Style => new(Color, Width, StyleUnknownProperties);

    public IReadOnlyList<LocalInkUnknownProperty>? UnknownProperties { get; init; }
    public IReadOnlyList<LocalInkUnknownProperty>? StyleUnknownProperties { get; init; }
}

public sealed record LocalInkDocument(
    int SchemaVersion,
    int Width,
    int Height,
    IReadOnlyList<LocalInkStroke> Strokes)
{
    public IReadOnlyList<LocalInkUnknownProperty>? UnknownProperties { get; init; }
}

public static class LocalInkDocumentCodec
{
    public const int MaxDocumentBytes = 16 * 1024 * 1024;
    public const int MinCanvasWidth = 80;
    public const int MaxCanvasWidth = 2048;
    public const int MinCanvasHeight = 80;
    public const int MaxCanvasHeight = 4096;
    private const int MaxStrokes = 10_000;
    private const int MaxPoints = 50_000;
    private static readonly string[] RootKnownProperties =
    [
        "schemaVersion",
        "SchemaVersion",
        "width",
        "Width",
        "height",
        "Height",
        "strokeIds",
        "StrokeIds",
        "strokeStyles",
        "StrokeStyles",
        "strokes",
        "Strokes",
    ];
    private static readonly string[] StrokeKnownProperties =
    [
        "color",
        "Color",
        "width",
        "Width",
        "points",
        "Points",
    ];
    private static readonly string[] PointKnownProperties =
    [
        "x",
        "X",
        "y",
        "Y",
        "pressure",
        "Pressure",
    ];
    private static readonly string[] StyleKnownProperties =
    [
        "color",
        "Color",
        "width",
        "Width",
    ];

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
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidInk();
            }

            var unknownRootProperties = ReadUnknownProperties(root, RootKnownProperties);
            if (TryReadProperty(root, "schemaVersion", "SchemaVersion", out var schemaElement)
                && (!schemaElement.TryGetInt32(out var schemaVersion) || schemaVersion != 1))
            {
                throw InvalidInk();
            }

            var width = ReadCanvasDimension(root, "width", "Width");
            var height = ReadCanvasDimension(root, "height", "Height");
            if (width is < MinCanvasWidth or > MaxCanvasWidth
                || height is < MinCanvasHeight or > MaxCanvasHeight)
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

            var suppliedStyles = TryReadProperty(
                root,
                "strokeStyles",
                "StrokeStyles",
                out var stylesElement);
            if (suppliedStyles
                && (stylesElement.ValueKind != JsonValueKind.Array
                    || stylesElement.GetArrayLength() != rawStrokes.GetArrayLength()))
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
                var isObject = rawStroke.ValueKind == JsonValueKind.Object;
                if (suppliedStyles && !isObject)
                {
                    throw InvalidInk();
                }

                var unknownStrokeProperties = isObject
                    ? ReadUnknownProperties(rawStroke, StrokeKnownProperties)
                    : null;
                var rawPoints = isObject
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
                    if (!Guid.TryParse(idText, out strokeId) || strokeId == Guid.Empty)
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

                var hasInlineColor = false;
                var hasInlineWidth = false;
                var inlineColor = LocalInkPalette.LegacyColor;
                var inlineWidth = LocalInkPalette.LegacyWidth;
                if (isObject)
                {
                    hasInlineColor = TryReadProperty(
                        rawStroke,
                        "color",
                        "Color",
                        out var inlineColorElement);
                    if (hasInlineColor)
                    {
                        inlineColor = ReadColor(inlineColorElement);
                    }

                    hasInlineWidth = TryReadProperty(
                        rawStroke,
                        "width",
                        "Width",
                        out var inlineWidthElement);
                    if (hasInlineWidth)
                    {
                        inlineWidth = ReadFiniteNumber(inlineWidthElement);
                    }
                }

                LocalInkStyle style;
                if (suppliedStyles)
                {
                    var styleElement = stylesElement[strokeIndex];
                    if (styleElement.ValueKind != JsonValueKind.Object)
                    {
                        throw InvalidInk();
                    }

                    style = ReadStyle(styleElement);
                    if ((hasInlineColor
                            && !string.Equals(
                                inlineColor,
                                style.Color,
                                StringComparison.Ordinal))
                        || (hasInlineWidth
                            && Math.Abs(inlineWidth - style.Width) > 1e-9))
                    {
                        throw InvalidInk();
                    }
                }
                else
                {
                    style = new LocalInkStyle(inlineColor, inlineWidth);
                }

                var points = new List<LocalInkPoint>(rawPoints.GetArrayLength());
                foreach (var rawPoint in rawPoints.EnumerateArray())
                {
                    double x;
                    double y;
                    double pressure;
                    IReadOnlyList<LocalInkUnknownProperty>? unknownPointProperties = null;
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
                        pressure = TryReadProperty(
                            rawPoint,
                            "pressure",
                            "Pressure",
                            out var pressureElement)
                            ? ReadFiniteNumber(pressureElement)
                            : 1;
                        unknownPointProperties = ReadUnknownProperties(
                            rawPoint,
                            PointKnownProperties);
                    }
                    else
                    {
                        throw InvalidInk();
                    }

                    if (x < -1 || x > width + 1 || y < -1 || y > height + 1 || pressure is < 0 or > 1)
                    {
                        throw InvalidInk();
                    }

                    points.Add(new LocalInkPoint(x, y, pressure)
                    {
                        UnknownProperties = unknownPointProperties,
                    });
                }

                strokes.Add(new LocalInkStroke(strokeId, points, style.Color, style.Width)
                {
                    UnknownProperties = unknownStrokeProperties,
                    StyleUnknownProperties = style.UnknownProperties,
                });
                strokeIndex++;
            }

            return new LocalInkDocument(1, width, height, strokes)
            {
                UnknownProperties = unknownRootProperties,
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("手写源 JSON 损坏。", ex);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("手写源包含超限数据。", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("手写源包含无效的颜色或笔宽。", ex);
        }
    }

    public static string Serialize(LocalInkDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = Parse(WriteDocument(document));
        return WriteDocument(normalized);
    }

    private static string WriteDocument(LocalInkDocument document)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WriteNumber("width", document.Width);
            writer.WriteNumber("height", document.Height);

            writer.WriteStartArray("strokeIds");
            foreach (var stroke in document.Strokes)
            {
                writer.WriteStringValue(stroke.Id.ToString("D"));
            }

            writer.WriteEndArray();
            writer.WriteStartArray("strokeStyles");
            foreach (var stroke in document.Strokes)
            {
                writer.WriteStartObject();
                writer.WriteString("color", stroke.Color);
                writer.WriteNumber("width", stroke.Width);
                WriteUnknownProperties(writer, stroke.StyleUnknownProperties);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("strokes");
            foreach (var stroke in document.Strokes)
            {
                writer.WriteStartObject();
                writer.WriteString("color", stroke.Color);
                writer.WriteNumber("width", stroke.Width);
                writer.WriteStartArray("points");
                foreach (var point in stroke.Points)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("x", point.X);
                    writer.WriteNumber("y", point.Y);
                    writer.WriteNumber("pressure", point.Pressure);
                    WriteUnknownProperties(writer, point.UnknownProperties);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                WriteUnknownProperties(writer, stroke.UnknownProperties);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteUnknownProperties(writer, document.UnknownProperties);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteUnknownProperties(
        Utf8JsonWriter writer,
        IReadOnlyList<LocalInkUnknownProperty>? properties)
    {
        if (properties is null)
        {
            return;
        }

        foreach (var property in properties)
        {
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }
    }

    private static int ReadCanvasDimension(JsonElement element, string camelCase, string pascalCase)
    {
        var value = ReadFiniteNumber(ReadProperty(element, camelCase, pascalCase));
        if (value < int.MinValue
            || value > int.MaxValue
            || Math.Abs(value - Math.Round(value)) > 1e-9)
        {
            throw InvalidInk();
        }

        return checked((int)value);
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

    private static string ReadColor(JsonElement element)
    {
        var value = element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        return value is null ? throw InvalidInk() : LocalInkStyle.NormalizeColor(value);
    }

    private static LocalInkStyle ReadStyle(JsonElement element)
    {
        var color = ReadColor(ReadProperty(element, "color", "Color"));
        var width = ReadFiniteNumber(ReadProperty(element, "width", "Width"));
        return new LocalInkStyle(
            color,
            width,
            ReadUnknownProperties(element, StyleKnownProperties));
    }

    private static IReadOnlyList<LocalInkUnknownProperty>? ReadUnknownProperties(
        JsonElement element,
        string[] knownProperties)
    {
        List<LocalInkUnknownProperty>? unknown = null;
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(knownProperties, property.Name) >= 0)
            {
                continue;
            }

            (unknown ??= []).Add(new LocalInkUnknownProperty(
                property.Name,
                property.Value.Clone()));
        }

        return unknown;
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
        => new("手写源字段、笔画顺序、样式、压力或坐标无效。");
}
