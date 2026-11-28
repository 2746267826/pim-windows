using System.Text;

namespace Pim.Client.Core.Services.Notes;

public enum NativeNoteFlowBlockKind
{
    Text,
    Photo,
    Handwriting,
    Recording,
    UnresolvedMaterial,
}

public sealed record NativeNoteFlowBlock(
    NativeNoteFlowBlockKind Kind,
    string? BlockId,
    string Text,
    Guid? MaterialId,
    LocalNoteMaterial? Material)
{
    public bool IsEditableText => Kind == NativeNoteFlowBlockKind.Text;
}

/// <summary>
/// A note is one continuous flow: editable Markdown text blocks and inline
/// photo / handwriting / recording blocks occupy the same ordered document.
/// Material markers remain the persisted positional authority, so editing a
/// text block rewrites only that segment and never turns materials into a
/// separate list.
/// </summary>
public sealed record NativeNoteFlowDocument(
    IReadOnlyList<NativeNoteFlowBlock> Blocks,
    IReadOnlyList<LocalNoteMaterial> UnplacedMaterials,
    string Markdown)
{
    public static NativeNoteFlowDocument Create(
        string? markdown,
        IReadOnlyList<LocalNoteMaterial> materials,
        LocalNoteServerSource? serverSource = null)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var layout = serverSource is null
            ? NativeNoteDocumentLayout.Parse(markdown, materials)
            : NativeNoteDocumentLayout.ParseServerSource(serverSource, materials);
        var source = serverSource is null
            ? markdown ?? string.Empty
            : BuildServerMarkdown(serverSource, materials);
        var blocks = new List<NativeNoteFlowBlock>();
        var textIndex = 0;
        foreach (var segment in layout.Segments)
        {
            blocks.Add(segment.Kind switch
            {
                NativeNoteDocumentSegmentKind.Markdown => new NativeNoteFlowBlock(
                    NativeNoteFlowBlockKind.Text,
                    $"text:{textIndex++}",
                    segment.Text,
                    null,
                    null),
                NativeNoteDocumentSegmentKind.Material when segment.Material is not null
                    => MaterialBlock(segment.Material, segment.Text),
                NativeNoteDocumentSegmentKind.UnresolvedMaterial => new NativeNoteFlowBlock(
                    NativeNoteFlowBlockKind.UnresolvedMaterial,
                    $"unresolved:{segment.MaterialId?.ToString("N") ?? segment.Text}",
                    segment.Text,
                    segment.MaterialId,
                    null),
                _ => throw new InvalidDataException("笔记连续文档包含未知分段。"),
            });
        }

        if (blocks.Count == 0)
        {
            blocks.Add(new NativeNoteFlowBlock(
                NativeNoteFlowBlockKind.Text,
                "text:0",
                string.Empty,
                null,
                null));
        }

        return new NativeNoteFlowDocument(blocks, layout.UnplacedMaterials, source);
    }

    public static string ReplaceTextBlock(string markdown, string? blockText, string replacement)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(replacement);
        var text = blockText ?? string.Empty;
        var index = markdown.IndexOf(text, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException("正文已变化；未覆盖当前内容，请重新打开该段后再编辑。");
        }

        return string.Concat(
            markdown.AsSpan(0, index),
            replacement,
            markdown.AsSpan(index + text.Length));
    }

    public static string InsertMaterialMarker(
        string markdown,
        string? textBlockText,
        int textIndex,
        Guid materialId)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var text = textBlockText ?? string.Empty;
        if (textIndex < 0 || textIndex > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(textIndex));
        }

        var blockStart = markdown.IndexOf(text, StringComparison.Ordinal);
        if (blockStart < 0)
        {
            throw new InvalidOperationException("正文已变化；材料未插入。");
        }

        var insertion = NativeNoteDocumentLayout.InsertMarkers(
            text,
            textIndex,
            [materialId]);
        return ReplaceTextBlock(markdown, text, insertion.Markdown);
    }

    public string ReplaceTextBlocks(IReadOnlyDictionary<string, string> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        var builder = new StringBuilder(Markdown.Length);
        foreach (var block in Blocks)
        {
            if (block.Kind == NativeNoteFlowBlockKind.Text
                && block.BlockId is { } blockId
                && replacements.TryGetValue(blockId, out var replacement))
            {
                builder.Append(replacement);
            }
            else
            {
                builder.Append(block.Text);
            }
        }

        return builder.ToString();
    }

    public string InsertMaterialMarker(
        string blockId,
        int textIndex,
        Guid materialId)
    {
        ArgumentNullException.ThrowIfNull(blockId);
        var targetIndex = Blocks.ToList().FindIndex(block =>
            block.IsEditableText
            && string.Equals(block.BlockId, blockId, StringComparison.Ordinal));
        if (targetIndex < 0)
        {
            throw new InvalidOperationException("找不到当前正文段落；材料未插入。");
        }

        var target = Blocks[targetIndex];
        if (textIndex < 0 || textIndex > target.Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(textIndex));
        }

        var insertion = NativeNoteDocumentLayout.InsertMarkers(
            target.Text,
            textIndex,
            [materialId]);
        var before = string.Concat(Blocks.Take(targetIndex).Select(block => block.Text));
        var after = string.Concat(Blocks.Skip(targetIndex + 1).Select(block => block.Text));
        return string.Concat(before, insertion.Markdown, after);
    }

    public string AppendMaterialMarker(Guid materialId)
    {
        var targetIndex = Blocks.ToList().FindLastIndex(block => block.IsEditableText);
        var marker = NativeNoteDocumentLayout.InsertMarkers(
            string.Empty,
            0,
            [materialId]).Markdown;
        if (targetIndex < 0)
        {
            return string.Concat(Markdown, "\n\n", marker);
        }

        var target = Blocks[targetIndex];
        var insertion = NativeNoteDocumentLayout.InsertMarkers(
            target.Text,
            target.Text.Length,
            [materialId]);
        var before = string.Concat(Blocks.Take(targetIndex).Select(block => block.Text));
        var after = string.Concat(Blocks.Skip(targetIndex + 1).Select(block => block.Text));
        return string.Concat(before, insertion.Markdown, after);
    }

    public static string ReplaceMaterialMarker(
        string markdown,
        Guid oldMaterialId,
        Guid newMaterialId)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var oldMarker = NativeNoteDocumentLayout.CreateMarker(oldMaterialId);
        var newMarker = NativeNoteDocumentLayout.CreateMarker(newMaterialId);
        return markdown.Replace(oldMarker, newMarker, StringComparison.OrdinalIgnoreCase);
    }

    public static NativeNoteFlowBlockKind KindFor(LocalNoteMaterial material)
        => material.Kind switch
        {
            LocalNoteMaterialKind.Photo => NativeNoteFlowBlockKind.Photo,
            LocalNoteMaterialKind.Handwriting
                or LocalNoteMaterialKind.HandwritingSource => NativeNoteFlowBlockKind.Handwriting,
            LocalNoteMaterialKind.Recording
                or LocalNoteMaterialKind.RecordingSegment => NativeNoteFlowBlockKind.Recording,
            _ => NativeNoteFlowBlockKind.UnresolvedMaterial,
        };

    private static NativeNoteFlowBlock MaterialBlock(
        LocalNoteMaterial material,
        string marker)
        => new(
            KindFor(material),
            $"material:{material.MaterialId:N}",
            marker,
            material.MaterialId,
            material);

    private static string BuildServerMarkdown(
        LocalNoteServerSource source,
        IReadOnlyList<LocalNoteMaterial> materials)
    {
        var blockBackedMaterialIds = source.Blocks
            .Where(block => block.Kind is "photo" or "ink")
            .SelectMany(block => block.MaterialIds)
            .ToHashSet();
        var builder = new StringBuilder();
        foreach (var block in source.Blocks)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            if (block.Kind == "markdown")
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(block.RawJson);
                    builder.Append(document.RootElement.TryGetProperty("text", out var text)
                        && text.ValueKind == System.Text.Json.JsonValueKind.String
                        ? StripMarkers(text.GetString() ?? string.Empty, blockBackedMaterialIds)
                        : StripMarkers(block.RawJson, blockBackedMaterialIds));
                }
                catch (System.Text.Json.JsonException)
                {
                    builder.Append(StripMarkers(block.RawJson, blockBackedMaterialIds));
                }

                continue;
            }

            var materialId = block.Kind == "ink"
                ? block.MaterialIds.FirstOrDefault(id => materials.Any(material =>
                    material.MaterialId == id
                    && material.Kind == LocalNoteMaterialKind.Handwriting))
                : block.MaterialIds.FirstOrDefault(id => materials.Any(material =>
                    material.MaterialId == id
                    && material.Kind == LocalNoteMaterialKind.Photo));
            if (materialId != Guid.Empty)
            {
                builder.Append(NativeNoteDocumentLayout.CreateMarker(materialId));
            }
        }

        return builder.ToString();
    }

    private static string StripMarkers(string text, IReadOnlySet<Guid> blockBackedMaterialIds)
    {
        if (blockBackedMaterialIds.Count == 0)
        {
            return text;
        }

        var result = text;
        foreach (var materialId in blockBackedMaterialIds)
        {
            result = result.Replace(
                NativeNoteDocumentLayout.CreateMarker(materialId),
                string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}
