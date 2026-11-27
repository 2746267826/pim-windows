using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class LocalInkDocumentCodecTests
{
    [Fact]
    public void D04PerStrokeStylesRoundTripAndLegacyPayloadsKeepFrozenDefaults()
    {
        var redId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var blueId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var shared = $$"""
            {
              "width": 320,
              "height": 180,
              "strokeIds": ["{{redId:D}}", "{{blueId:D}}"],
              "strokeStyles": [
                {"color": "#b8373c", "width": 1.8},
                {"color": "#245fba", "width": 6}
              ],
              "strokes": [
                {
                  "color": "#b8373c",
                  "width": 1.8,
                  "points": [
                    {"x": 10, "y": 20, "pressure": 0.25},
                    {"x": 30, "y": 40, "pressure": 0.75}
                  ]
                },
                {
                  "color": "#245fba",
                  "width": 6,
                  "points": [{"x": 50, "y": 60, "pressure": 0.5}]
                }
              ]
            }
            """;

        var parsed = LocalInkDocumentCodec.Parse(shared);

        Assert.Equal(new[] { redId, blueId }, parsed.Strokes.Select(stroke => stroke.Id));
        Assert.Equal(new[] { "#b8373c", "#245fba" }, parsed.Strokes.Select(stroke => stroke.Color));
        Assert.Equal(new[] { 1.8, 6d }, parsed.Strokes.Select(stroke => stroke.Width));

        var saved = LocalInkDocumentCodec.Serialize(parsed);
        var reopened = LocalInkDocumentCodec.Parse(saved);
        Assert.Equal(new[] { "#b8373c", "#245fba" }, reopened.Strokes.Select(stroke => stroke.Color));
        Assert.Equal(new[] { 1.8, 6d }, reopened.Strokes.Select(stroke => stroke.Width));
        Assert.Equal(new[] { redId, blueId }, reopened.Strokes.Select(stroke => stroke.Id));
        Assert.Contains("\"schemaVersion\":1", saved, StringComparison.Ordinal);
        Assert.Contains("\"strokeStyles\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"color\":\"#b8373c\"", saved, StringComparison.Ordinal);

        const string legacyWindows =
            """{"schemaVersion":1,"width":320,"height":180,"strokes":[[[10,20,0.5],[30,40,0.5]]]}""";
        var legacy = LocalInkDocumentCodec.Parse(legacyWindows);
        Assert.Equal(LocalInkPalette.LegacyColor, legacy.Strokes.Single().Color);
        Assert.Equal(LocalInkPalette.LegacyWidth, legacy.Strokes.Single().Width);
    }

    [Fact]
    public void SharedInkWithoutPressureUsesTheCrossEndDefault()
    {
        const string shared = """
            {
              "width": 320,
              "height": 180,
              "strokes": [
                {"points": [{"x": 10, "y": 20}, {"x": 30, "y": 40}]}
              ]
            }
            """;

        var parsed = LocalInkDocumentCodec.Parse(shared);

        Assert.All(parsed.Strokes.Single().Points, point => Assert.Equal(1, point.Pressure));
        Assert.Equal(LocalInkPalette.LegacyColor, parsed.Strokes.Single().Color);
        Assert.Equal(LocalInkPalette.LegacyWidth, parsed.Strokes.Single().Width);
    }

    [Fact]
    public void SharedInkStylesMayReplaceInlineStylesForAndroidAndWebPayloads()
    {
        const string shared = """
            {
              "width": 320.0,
              "height": 180.0,
              "strokeStyles": [
                {"color": "#B8373C", "width": 1.8},
                {"color": "#216e4e", "width": 6.0}
              ],
              "strokes": [
                {"points": [{"x": 10, "y": 20}]},
                {"points": [{"x": 30, "y": 40, "pressure": 0.5}]}
              ]
            }
            """;

        var parsed = LocalInkDocumentCodec.Parse(shared);

        Assert.Equal(320, parsed.Width);
        Assert.Equal(180, parsed.Height);
        Assert.Equal(
            new[] { LocalInkPalette.RedColor, LocalInkPalette.GreenColor },
            parsed.Strokes.Select(stroke => stroke.Color));
        Assert.Equal(
            new[] { LocalInkPalette.ThinWidth, 6d },
            parsed.Strokes.Select(stroke => stroke.Width));
    }

    [Fact]
    public void LegacyInkReopensInOrderAndSavesStableIdsWithoutChangingPressure()
    {
        var json = "{\"schemaVersion\":1,\"width\":320,\"height\":180,\"strokes\":["
            + "[{\"x\":10,\"y\":20,\"pressure\":0.25},{\"x\":30,\"y\":40,\"pressure\":0.75}],"
            + "[{\"x\":50,\"y\":60,\"pressure\":0.5}]]}";

        var first = LocalInkDocumentCodec.Parse(json);
        var edited = first with
        {
            Strokes = first.Strokes.Select(stroke => stroke with
            {
                Points = stroke.Points.Select(point => point with { X = point.X + 1 }).ToArray(),
            }).ToArray(),
        };
        var saved = LocalInkDocumentCodec.Serialize(edited);
        var reopened = LocalInkDocumentCodec.Parse(saved);

        Assert.Equal(2, reopened.Strokes.Count);
        Assert.Equal(first.Strokes.Select(stroke => stroke.Id), reopened.Strokes.Select(stroke => stroke.Id));
        Assert.Equal(new[] { 11d, 31d }, reopened.Strokes[0].Points.Select(point => point.X));
        Assert.Equal(new[] { 0.25d, 0.75d }, reopened.Strokes[0].Points.Select(point => point.Pressure));
        Assert.NotEqual(reopened.Strokes[0].Id, reopened.Strokes[1].Id);
    }

    [Fact]
    public void OriginalPascalCasePayloadAndNormalWindowDimensionsRemainReadable()
    {
        var legacyJson = "{\"SchemaVersion\":1,\"Width\":680,\"Height\":450,\"Strokes\":["
            + "[{\"X\":8.5,\"Y\":12.25,\"Pressure\":0.35},{\"X\":120,\"Y\":240,\"Pressure\":0.9}],"
            + "[{\"X\":44,\"Y\":52,\"Pressure\":0.6}]]}";

        var ink = LocalInkDocumentCodec.Parse(legacyJson);

        Assert.Equal(680, ink.Width);
        Assert.Equal(450, ink.Height);
        Assert.Equal(2, ink.Strokes.Count);
        Assert.Equal(new[] { 8.5, 120d }, ink.Strokes[0].Points.Select(point => point.X));
        Assert.Equal(new[] { 0.35, 0.9 }, ink.Strokes[0].Points.Select(point => point.Pressure));
        Assert.Equal(0.6, ink.Strokes[1].Points[0].Pressure);
    }

    [Theory]
    [InlineData(4096, 2160)]
    [InlineData(680, 420)]
    public void LegacyCanvasExtentsAndEdgeCoordinateToleranceRemainReadable(int width, int height)
    {
        var json = $"{{\"SchemaVersion\":1,\"Width\":{width},\"Height\":{height},\"Strokes\":[["
            + $"{{\"X\":{width + 0.5},\"Y\":{height + 0.5},\"Pressure\":0.5}}]]}}";

        var ink = LocalInkDocumentCodec.Parse(json);

        Assert.Equal(width, ink.Width);
        Assert.Equal(height, ink.Height);
        Assert.Equal(width + 0.5, ink.Strokes[0].Points[0].X);
        Assert.Equal(height + 0.5, ink.Strokes[0].Points[0].Y);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"width\":320,\"height\":180,\"strokes\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"width\":320,\"height\":180,\"strokes\":[[{\"x\":1,\"y\":2,\"pressure\":2}]]}")]
    [InlineData("{\"schemaVersion\":1,\"width\":320,\"height\":180,\"strokeIds\":[\"00000000-0000-0000-0000-000000000001\",\"00000000-0000-0000-0000-000000000001\"],\"strokes\":[[[1,2,0.5]],[[3,4,0.5]]]}")]
    [InlineData("{\"schemaVersion\":1,\"SchemaVersion\":1,\"width\":320,\"height\":180,\"strokes\":[[[1,2,0.5]]]}")]
    [InlineData("{\"schemaVersion\":1,\"width\":320,\"height\":180,\"strokeIds\":[17],\"strokes\":[[[1,2,0.5]]]}")]
    [InlineData("{\"schemaVersion\":1,\"width\":320,\"height\":180,\"strokes\":[{\"points\":[[1,2,0.5]],\"Points\":[[3,4,0.5]]}]}")]
    [InlineData("{\"schemaVersion\":1,\"width\":320,\"height\":180,\"strokes\":[[[1,2,0.5]]],\"strokeStyles\":[{\"color\":\"#b8373c\",\"width\":1.8}]}")]
    [InlineData("{\"schemaVersion\":1,\"width\":320,\"height\":180,\"strokes\":[{\"color\":\"#245fba\",\"width\":6,\"points\":[{\"x\":1,\"y\":2}]}],\"strokeStyles\":[{\"color\":\"#b8373c\",\"width\":1.8}]}")]
    [InlineData("{\"schemaVersion\":1,\"width\":320,\"height\":180,\"strokes\":[{\"points\":[{\"x\":1,\"y\":2}]}],\"strokeStyles\":[{\"color\":\"red\",\"width\":1.8}]}")]
    [InlineData("{\"schemaVersion\":1,\"width\":320,\"height\":180,\"strokes\":[{\"points\":[{\"x\":1,\"y\":2}]}],\"strokeStyles\":[{\"color\":\"#b8373c\",\"width\":0.4}]}")]
    [InlineData("{\"schemaVersion\":1,\"width\":320,\"height\":180,\"strokes\":[{\"points\":[{\"x\":1,\"y\":2}]}],\"strokeStyles\":[{\"color\":\"#b8373c\",\"color\":\"#245fba\",\"width\":1.8}]}")]
    [InlineData("{\"schemaVersion\":2,\"width\":320,\"height\":180,\"strokes\":[[[1,2,0.5]]]}")]
    [InlineData("{\"schemaVersion\":1,\"width\":320.5,\"height\":180,\"strokes\":[[[1,2,0.5]]]}")]
    public void CorruptInkIsRejectedWithoutProducingAnEditableDocument(string json)
    {
        Assert.Throws<InvalidDataException>(() => LocalInkDocumentCodec.Parse(json));
    }

    [Fact]
    public void ReopenedInkRejectsOversizedSourceBeforeParsing()
    {
        var oversized = new string(' ', LocalInkDocumentCodec.MaxDocumentBytes + 1);
        Assert.Throws<InvalidDataException>(() => LocalInkDocumentCodec.Parse(oversized));
    }
}
