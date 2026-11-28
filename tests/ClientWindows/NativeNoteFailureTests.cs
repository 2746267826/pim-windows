using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class NativeNoteFailureTests
{
    private static readonly Guid NoteId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void SummaryIdentifiesOperationTypeNoteAndConcreteReason()
    {
        var error = new IOException("磁盘空间不足");

        var summary = NativeNoteFailure.Describe("保存正文", error, NoteId);

        Assert.Contains("保存正文", summary);
        Assert.Contains(nameof(IOException), summary);
        Assert.Contains(NoteId.ToString("D"), summary);
        Assert.Contains("磁盘空间不足", summary);
    }

    [Fact]
    public void DetailsKeepStackForTheExpandableTraceableView()
    {
        Exception error;
        try
        {
            throw new InvalidDataException("材料摘要校验失败");
        }
        catch (Exception ex)
        {
            error = ex;
        }

        var details = NativeNoteFailure.Details(
            "读取手写原稿",
            error,
            NoteId,
            DateTimeOffset.Parse("2026-11-29T08:30:00+08:00"));

        Assert.Contains("读取手写原稿", details);
        Assert.Contains("InvalidDataException", details);
        Assert.Contains("材料摘要校验失败", details);
        Assert.Contains("2026-11-29 08:30:00 +08:00", details);
        Assert.Contains(nameof(NativeNoteFailureTests), details);
    }

    [Fact]
    public void EmptyExceptionMessageFallsBackToTheExceptionType()
    {
        var summary = NativeNoteFailure.Describe(
            "读取笔记",
            new InvalidOperationException(),
            null);

        Assert.Contains(nameof(InvalidOperationException), summary);
        Assert.DoesNotContain("note=", summary);
    }
}
