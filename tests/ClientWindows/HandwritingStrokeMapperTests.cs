using Pim.Client.App;
using Pim.Client.Core.Services.Notes;
using System.Windows.Ink;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class HandwritingStrokeMapperTests
{
    [Fact]
    public void WpfStrokeRoundTripPreservesOrderPressureCoordinatesAndIds()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var source = new[]
        {
            new LocalInkStroke(firstId, [new LocalInkPoint(12.5, 33.25, 0.2), new LocalInkPoint(61, 79, 0.85)]),
            new LocalInkStroke(secondId, [new LocalInkPoint(90, 110, 0.5)]),
        };

        var reopened = source
            .Select(HandwritingStrokeMapper.ToStroke)
            .Select(HandwritingStrokeMapper.FromStroke)
            .ToArray();

        Assert.Equal(new[] { firstId, secondId }, reopened.Select(stroke => stroke.Id));
        Assert.Equal(new[] { 12.5, 61d }, reopened[0].Points.Select(point => point.X));
        Assert.Equal(new[] { 33.25, 79d }, reopened[0].Points.Select(point => point.Y));
        Assert.Equal(0.2, reopened[0].Points[0].Pressure, 5);
        Assert.Equal(0.85, reopened[0].Points[1].Pressure, 5);
        Assert.Equal(0.5, reopened[1].Points[0].Pressure, 5);
    }

    [Fact]
    public void WpfEraseResultSplitsStrokeAndCopiesItsIdentityProperty()
    {
        var originalId = Guid.NewGuid();
        var source = HandwritingStrokeMapper.ToStroke(new LocalInkStroke(
            originalId,
            [new LocalInkPoint(10, 50, 0.5), new LocalInkPoint(90, 50, 0.5)]));

        var split = source.GetEraseResult(
            [new System.Windows.Point(50, 30), new System.Windows.Point(50, 70)],
            new EllipseStylusShape(12, 12));
        var mapped = HandwritingStrokeMapper.FromStrokes(split);

        Assert.Equal(2, split.Count);
        Assert.Equal(originalId, mapped[0].Id);
        Assert.NotEqual(originalId, mapped[1].Id);
        Assert.NotEqual(mapped[0].Id, mapped[1].Id);
        Assert.Equal(mapped.Select(stroke => stroke.Id), HandwritingStrokeMapper.FromStrokes(split).Select(stroke => stroke.Id));

        var saved = LocalInkDocumentCodec.Parse(LocalInkDocumentCodec.Serialize(
            new LocalInkDocument(1, 100, 100, mapped)));
        Assert.Equal(mapped.Select(stroke => stroke.Id), saved.Strokes.Select(stroke => stroke.Id));
    }
}
