using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class NativeNoteFlowDocumentTests
{
    [Fact]
    public void MarkdownPhotoAndHandwritingShareOneEditableFlow()
    {
        var photo = Material(LocalNoteMaterialKind.Photo, "课堂照片.png");
        var ink = Material(LocalNoteMaterialKind.Handwriting, "板书.png");
        var markdown = string.Join(
            "\n\n",
            "第一段",
            NativeNoteDocumentLayout.CreateMarker(photo.MaterialId),
            "照片之后的说明",
            NativeNoteDocumentLayout.CreateMarker(ink.MaterialId),
            "最后一段");

        var flow = NativeNoteFlowDocument.Create(markdown, [photo, ink]);

        Assert.Equal(
            [
                NativeNoteFlowBlockKind.Text,
                NativeNoteFlowBlockKind.Photo,
                NativeNoteFlowBlockKind.Text,
                NativeNoteFlowBlockKind.Handwriting,
                NativeNoteFlowBlockKind.Text,
            ],
            flow.Blocks.Select(block => block.Kind));
        Assert.Equal("第一段", flow.Blocks[0].Text.Trim());
        Assert.Equal(photo.MaterialId, flow.Blocks[1].MaterialId);
        Assert.Equal("照片之后的说明", flow.Blocks[2].Text.Trim());
        Assert.Equal(ink.MaterialId, flow.Blocks[3].MaterialId);
        Assert.Equal("最后一段", flow.Blocks[4].Text.Trim());
        Assert.All(
            flow.Blocks.Where(block => block.Kind == NativeNoteFlowBlockKind.Text),
            block => Assert.True(block.IsEditableText));
        Assert.All(
            flow.Blocks.Where(block => block.Kind != NativeNoteFlowBlockKind.Text),
            block => Assert.False(block.IsEditableText));
    }

    [Fact]
    public void EditingOneTextSegmentPreservesMaterialMarkersAndOtherSegments()
    {
        var photo = Material(LocalNoteMaterialKind.Photo, "照片.png");
        var marker = NativeNoteDocumentLayout.CreateMarker(photo.MaterialId);
        var markdown = $"开头\n\n{marker}\n\n中段\n\n{marker}\n\n结尾";
        var flow = NativeNoteFlowDocument.Create(markdown, [photo]);
        var middle = Assert.Single(
            flow.Blocks.Where(block => block.Text.Contains("中段", StringComparison.Ordinal)));

        var updated = NativeNoteFlowDocument.ReplaceTextBlock(
            markdown,
            middle.Text,
            "中段（已编辑）");

        Assert.Contains("开头", updated);
        Assert.Contains("中段（已编辑）", updated);
        Assert.Contains("结尾", updated);
        Assert.Single(NativeNoteDocumentLayout.ReferencedMaterialIds(updated));
        Assert.Equal(
            [
                NativeNoteFlowBlockKind.Text,
                NativeNoteFlowBlockKind.Photo,
                NativeNoteFlowBlockKind.Text,
                NativeNoteFlowBlockKind.Text,
            ],
            NativeNoteFlowDocument.Create(updated, [photo])
                .Blocks.Select(block => block.Kind));
    }

    [Fact]
    public void EditingAChangedSegmentIsRejectedRatherThanOverwritingOtherText()
    {
        var markdown = "第一段\n\n第二段";

        var error = Assert.Throws<InvalidOperationException>(() =>
            NativeNoteFlowDocument.ReplaceTextBlock(markdown, "已经不存在的段落", "替换"));

        Assert.Contains("未覆盖", error.Message);
    }

    [Fact]
    public void EditedSegmentsAreComposedBackInDocumentOrder()
    {
        var photo = Material(LocalNoteMaterialKind.Photo, "照片.png");
        var marker = NativeNoteDocumentLayout.CreateMarker(photo.MaterialId);
        var markdown = $"开头\n\n{marker}\n\n中段\n\n{marker}\n\n结尾";
        var flow = NativeNoteFlowDocument.Create(markdown, [photo]);
        var replacements = flow.Blocks
            .Where(block => block.IsEditableText)
            .ToDictionary(
                block => block.BlockId!,
                block => block.Text.Contains("中段", StringComparison.Ordinal)
                    ? block.Text.Replace(
                        "中段",
                        "中段（已编辑）",
                        StringComparison.Ordinal)
                    : block.Text);

        var updated = flow.ReplaceTextBlocks(replacements);

        Assert.StartsWith("开头\n\n", updated, StringComparison.Ordinal);
        Assert.Contains("中段（已编辑）", updated);
        Assert.EndsWith("\n\n结尾", updated, StringComparison.Ordinal);
        Assert.Single(NativeNoteDocumentLayout.ReferencedMaterialIds(updated));
    }

    [Fact]
    public void InsertingMaterialUsesTheCaretInsideTheEditableTextBlock()
    {
        var photo = Material(LocalNoteMaterialKind.Photo, "照片.png");
        var markdown = "第一段\n\n第二段";
        var block = Assert.Single(
            NativeNoteFlowDocument.Create(markdown, []).Blocks,
            item => item.IsEditableText);
        var caretInsideFirstParagraph = "第一".Length;

        var updated = NativeNoteFlowDocument.InsertMaterialMarker(
            markdown,
            block.Text,
            caretInsideFirstParagraph,
            photo.MaterialId);

        Assert.StartsWith("第一\n\n", updated, StringComparison.Ordinal);
        var markerIndex = updated.IndexOf(
            NativeNoteDocumentLayout.CreateMarker(photo.MaterialId),
            StringComparison.Ordinal);
        Assert.Equal("第一\n\n".Length, markerIndex);
        var flow = NativeNoteFlowDocument.Create(updated, [photo]);
        Assert.Equal(
            [
                NativeNoteFlowBlockKind.Text,
                NativeNoteFlowBlockKind.Photo,
                NativeNoteFlowBlockKind.Text,
            ],
            flow.Blocks.Select(block => block.Kind));
    }

    [Fact]
    public void ServerSourceKeepsBlockOrderInTheSameEditableFlow()
    {
        var photo = Material(LocalNoteMaterialKind.Photo, "服务端照片.png");
        var ink = Material(LocalNoteMaterialKind.Handwriting, "服务端手写.png");
        var source = new LocalNoteServerSource(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "{}",
            "{}",
            "{}",
            string.Empty,
            [
                new LocalNoteServerSourceBlock(
                    "text-1",
                    "markdown",
                    """{"text":"服务端开头"}""",
                    []),
                new LocalNoteServerSourceBlock(
                    "photo-1",
                    "photo",
                    "{}",
                    [photo.MaterialId]),
                new LocalNoteServerSourceBlock(
                    "text-2",
                    "markdown",
                    """{"text":"照片说明"}""",
                    []),
                new LocalNoteServerSourceBlock(
                    "ink-1",
                    "ink",
                    "{}",
                    [ink.MaterialId]),
                new LocalNoteServerSourceBlock(
                    "text-3",
                    "markdown",
                    """{"text":"服务端结尾"}""",
                    []),
            ],
            []);

        var flow = NativeNoteFlowDocument.Create(
            string.Empty,
            [photo, ink],
            source);

        Assert.Equal(
            [
                NativeNoteFlowBlockKind.Text,
                NativeNoteFlowBlockKind.Photo,
                NativeNoteFlowBlockKind.Text,
                NativeNoteFlowBlockKind.Handwriting,
                NativeNoteFlowBlockKind.Text,
            ],
            flow.Blocks.Select(block => block.Kind));
        Assert.Equal(
            ["服务端开头", "照片说明", "服务端结尾"],
            flow.Blocks
                .Where(block => block.Kind == NativeNoteFlowBlockKind.Text)
                .Select(block => block.Text.Trim()));
    }

    private static LocalNoteMaterial Material(
        LocalNoteMaterialKind kind,
        string displayName)
        => new()
        {
            MaterialId = Guid.NewGuid(),
            Kind = kind,
            DisplayName = displayName,
            ContentSha256 = new string('a', 64),
            RelativePath = $"materials/{Guid.NewGuid():N}.bin",
            MediaType = kind == LocalNoteMaterialKind.Photo
                ? "image/png"
                : "image/png",
            Length = 128,
            AddedAt = DateTimeOffset.UtcNow,
            Width = 320,
            Height = 180,
        };
}
