using System.Net;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class NoteMaterialRestoreTests : IDisposable
{
    private const string AccountId = "11111111-1111-1111-1111-111111111111";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-note-restore-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RestoreDownloadsVerifiesAndImportsServerSchemaV1AsNewNote()
    {
        var fixture = await CreateFixtureAsync();
        var handler = new RestoreHandler(fixture);
        var api = CreateApi(handler);
        var store = new LocalNoteStore(_root);
        var service = CreateService(api, store, () =>
            new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));

        var result = await service.RestoreVersionAsync(
            fixture.VersionId,
            userConfirmedLocalCopy: true);

        Assert.NotEqual(fixture.NoteId, result.Import.NoteId);
        Assert.Equal(fixture.NoteId, result.Import.SourceNoteId);
        Assert.Equal(fixture.VersionId, result.Import.SourceVersionId);
        Assert.Equal("恢复材料", result.Import.Title);
        var restored = await store.LoadAsync(result.Import.NoteId);
        Assert.Equal(fixture.Markdown, restored.Markdown);
        var restoredMaterial = Assert.Single(restored.Materials);
        Assert.Equal(Hash(fixture.PhotoBytes), restoredMaterial.ContentSha256);
        Assert.Equal(fixture.PhotoBytes, await File.ReadAllBytesAsync(
            Path.Combine(_root, restoredMaterial.RelativePath)));
        Assert.Equal(3, handler.MaterialRequests);
        Assert.All(handler.MaterialRequestsSeen, request => Assert.Equal(fixture.VersionId, request.VersionId));
        Assert.False(File.Exists(Path.Combine(_root, "cloud-bindings.json")));
    }

    [Fact]
    public async Task ReadOnlyServerSourceHandwritingCannotBeDeletedLocally()
    {
        var fixture = await CreateMixedRecordingFixtureAsync();
        var store = new LocalNoteStore(_root);
        var service = CreateService(
            CreateApi(new RestoreHandler(fixture)),
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));
        var restored = await service.RestoreVersionAsync(
            fixture.VersionId,
            userConfirmedLocalCopy: true);
        var note = await store.LoadAsync(restored.Import.NoteId);
        Assert.True(note.IsServerSourceReadOnly);
        var preview = Assert.Single(note.Materials, material =>
            material.Kind == LocalNoteMaterialKind.Handwriting);
        var source = Assert.Single(note.Materials, material =>
            material.Kind == LocalNoteMaterialKind.HandwritingSource);

        var rejected = await Assert.ThrowsAsync<LocalNoteStoreException>(() =>
            store.RemoveHandwritingAsync(
                note.NoteId,
                preview.MaterialId,
                expectedCurrentVersionId: note.CurrentVersionId));

        Assert.Contains("只读", rejected.Message, StringComparison.Ordinal);
        var unchanged = await store.LoadAsync(note.NoteId);
        Assert.Equal(note.CurrentVersionId, unchanged.CurrentVersionId);
        Assert.Contains(unchanged.Materials, material => material.MaterialId == preview.MaterialId);
        Assert.Contains(unchanged.Materials, material => material.MaterialId == source.MaterialId);
    }

    [Fact]
    public async Task RestorePreservesInterleavedBlocksAndEveryRecordingSegmentThroughZipAndUploadBuild()
    {
        var fixture = await CreateMixedRecordingFixtureAsync();
        var store = new LocalNoteStore(_root);
        var service = CreateService(
            CreateApi(new RestoreHandler(fixture)),
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));

        var restoredResult = await service.RestoreVersionAsync(
            fixture.VersionId,
            userConfirmedLocalCopy: true);
        var restored = await store.LoadAsync(restoredResult.Import.NoteId);

        Assert.True(restored.IsServerSourceReadOnly);
        Assert.Equal(
            new[] { "markdown", "photo", "markdown", "ink", "markdown" },
            restored.ServerSource!.Blocks.Select(block => block.Kind));
        var audio = restored.Materials
            .Where(material => material.Kind == LocalNoteMaterialKind.RecordingSegment)
            .OrderBy(material => material.RecordingSegment)
            .ThenBy(material => material.RecordingChunkIndex)
            .ToArray();
        Assert.Equal(new[] { (1, 1, 5L), (1, 2, 7L), (2, 1, 11L) }, audio
            .Select(material => (
                material.RecordingSegment!.Value,
                material.RecordingChunkIndex!.Value,
                material.RecordingSamples!.Value)));
        var restoredInkSource = Assert.Single(restored.Materials, material =>
            material.Kind == LocalNoteMaterialKind.HandwritingSource);
        var restoredInkJson = await File.ReadAllTextAsync(
            Path.Combine(_root, restoredInkSource.RelativePath));
        var restoredInk = LocalInkDocumentCodec.Parse(restoredInkJson);
        AssertInkUnknownFields(restoredInkJson);
        var restoredStroke = Assert.Single(restoredInk.Strokes);
        Assert.Equal(Guid.Parse("44444444-4444-4444-8444-444444444444"), restoredStroke.Id);
        Assert.Equal("#b8373c", restoredStroke.Color);
        Assert.Equal(1.8, restoredStroke.Width);

        var zipPath = Path.Combine(_root, "roundtrip.pimnote.zip");
        await store.ExportAsync(restored.NoteId, restored.CurrentVersionId, zipPath);
        var importedRoot = Path.Combine(_root, "imported-library");
        var importedStore = new LocalNoteStore(importedRoot);
        var importedResult = await importedStore.ImportAsync(zipPath);
        var imported = await importedStore.LoadAsync(importedResult.NoteId);
        Assert.True(imported.IsServerSourceReadOnly);
        Assert.Equal(
            restored.ServerSource.Blocks.Select(block => (block.Id, block.Kind, block.RawJson)),
            imported.ServerSource!.Blocks.Select(block => (block.Id, block.Kind, block.RawJson)));
        var importedInkSource = Assert.Single(imported.Materials, material =>
            material.Kind == LocalNoteMaterialKind.HandwritingSource);
        var importedInkJson = await File.ReadAllTextAsync(
            Path.Combine(importedRoot, importedInkSource.RelativePath));
        var importedInk = LocalInkDocumentCodec.Parse(importedInkJson);
        AssertInkUnknownFields(importedInkJson);
        var importedStroke = Assert.Single(importedInk.Strokes);
        Assert.Equal(restoredStroke.Id, importedStroke.Id);
        Assert.Equal("#b8373c", importedStroke.Color);
        Assert.Equal(1.8, importedStroke.Width);

        var rebuilt = await new ServerNoteMaterialBundleBuilder(importedStore)
            .BuildAsync(imported.NoteId, imported.CurrentVersionId);
        using var input = JsonDocument.Parse(rebuilt.InputJson);
        var note = input.RootElement.GetProperty("note");
        Assert.Equal(
            new[] { "markdown", "photo", "markdown", "ink", "markdown" },
            note.GetProperty("blocks").EnumerateArray()
                .Select(block => block.GetProperty("kind").GetString()));
        var recording = Assert.Single(input.RootElement.GetProperty("recordings").EnumerateArray());
        Assert.Equal(
            new[] { (1, 1, 5L), (1, 2, 7L), (2, 1, 11L) },
            recording.GetProperty("chunks").EnumerateArray().Select(chunk => (
                chunk.GetProperty("segment").GetInt32(),
                chunk.GetProperty("index").GetInt32(),
                chunk.GetProperty("samples").GetInt64())));
        var rebuiltInk = note.GetProperty("blocks").EnumerateArray()
            .Single(block => block.GetProperty("kind").GetString() == "ink")
            .GetProperty("ink");
        Assert.Equal(
            "44444444-4444-4444-8444-444444444444",
            rebuiltInk.GetProperty("strokeIds")[0].GetString());
        Assert.Equal("#b8373c", rebuiltInk.GetProperty("strokeStyles")[0].GetProperty("color").GetString());
        Assert.Equal(1.8, rebuiltInk.GetProperty("strokeStyles")[0].GetProperty("width").GetDouble());
        Assert.Equal(
            "server",
            rebuiltInk.GetProperty("futureRoot").GetProperty("from").GetString());
        Assert.Equal(
            "red",
            rebuiltInk.GetProperty("strokeStyles")[0].GetProperty("futureStyle").GetString());
        Assert.Equal(
            "pen",
            rebuiltInk.GetProperty("strokes")[0].GetProperty("futureStroke").GetString());
        Assert.Equal(
            5,
            rebuiltInk.GetProperty("strokes")[0].GetProperty("points")[0]
                .GetProperty("futurePoint").GetInt32());
        Assert.Equal(3, rebuilt.Files.Count(file => file.Kind == "AudioChunk"));
    }

    [Fact]
    public async Task UploadRestoreFinishRoundTripKeepsEachAnchorOnceAndStableOrder()
    {
        var sourceStore = new LocalNoteStore(Path.Combine(_root, "upload-source"));
        var note = await sourceStore.CreateAsync("上传混排");
        var imageBytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        await using (var photo = new MemoryStream(imageBytes))
        {
            note = await sourceStore.AddMaterialsAsync(note.NoteId,
            [
                new LocalNoteMaterialInput(photo, "照片.png", LocalNoteMaterialKind.Photo, "image/png"),
            ]);
        }

        const string Ink =
            """{"schemaVersion":1,"width":320,"height":180,"strokes":[[{"x":10,"y":20,"pressure":0.5}]]}""";
        await using (var inkPreview = new MemoryStream(imageBytes))
        {
            note = await sourceStore.AddHandwritingAsync(
                note.NoteId,
                inkPreview,
                Ink,
                "手写",
                320,
                180);
        }

        var wave = SyntheticWave(5);
        await using (var recording = new MemoryStream(wave))
        {
            note = await sourceStore.AddMaterialsAsync(note.NoteId,
            [
                new LocalNoteMaterialInput(
                    recording,
                    "录音.wav",
                    LocalNoteMaterialKind.Recording,
                    "audio/wav"),
            ]);
        }

        var photoMaterial = note.Materials.Single(
            material => material.Kind == LocalNoteMaterialKind.Photo);
        var handwritingMaterial = note.Materials.Single(
            material => material.Kind == LocalNoteMaterialKind.Handwriting);
        var recordingMaterial = note.Materials.Single(
            material => material.Kind == LocalNoteMaterialKind.Recording);
        var markdown = string.Join(
            "\n\n",
            "开头",
            NativeNoteDocumentLayout.CreateMarker(photoMaterial.MaterialId),
            "照片重复锚点",
            NativeNoteDocumentLayout.CreateMarker(photoMaterial.MaterialId),
            "中间",
            NativeNoteDocumentLayout.CreateMarker(recordingMaterial.MaterialId),
            "录音重复锚点",
            NativeNoteDocumentLayout.CreateMarker(recordingMaterial.MaterialId),
            "录音之后",
            NativeNoteDocumentLayout.CreateMarker(handwritingMaterial.MaterialId),
            "手写重复锚点",
            NativeNoteDocumentLayout.CreateMarker(handwritingMaterial.MaterialId),
            "结尾");
        note = await sourceStore.SaveAsync(note.NoteId, note.Title, markdown);
        var bundle = await new ServerNoteMaterialBundleBuilder(sourceStore)
            .BuildAsync(note.NoteId, note.CurrentVersionId);

        using var input = JsonDocument.Parse(bundle.InputJson);
        var blocks = input.RootElement.GetProperty("note").GetProperty("blocks")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            ["markdown", "photo", "markdown", "ink", "markdown"],
            blocks.Select(block => block.GetProperty("kind").GetString()));
        var markdownBlockText = string.Join(
            "\n",
            blocks
                .Where(block => block.GetProperty("kind").GetString() == "markdown")
                .Select(block => block.GetProperty("text").GetString()));
        Assert.DoesNotContain(
            NativeNoteDocumentLayout.CreateMarker(photoMaterial.MaterialId),
            markdownBlockText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            NativeNoteDocumentLayout.CreateMarker(handwritingMaterial.MaterialId),
            markdownBlockText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            NativeNoteDocumentLayout.CreateMarker(recordingMaterial.MaterialId),
            markdownBlockText,
            StringComparison.Ordinal);
        var expectedRecordingMaterialId =
            Assert.Single(NativeNoteDocumentLayout.ReferencedMaterialIds(markdownBlockText));
        var expectedRecordingMarker =
            NativeNoteDocumentLayout.CreateMarker(expectedRecordingMaterialId);
        Assert.NotEqual(recordingMaterial.MaterialId, expectedRecordingMaterialId);
        Assert.Contains(expectedRecordingMarker, markdownBlockText, StringComparison.Ordinal);
        Assert.Equal(
            1,
            markdownBlockText.Split(expectedRecordingMarker, StringSplitOptions.None).Length - 1);
        Assert.Single(input.RootElement.GetProperty("recordings").EnumerateArray());

        var fixture = await CreateFixtureFromBundleAsync(bundle);
        var restoredStore = new LocalNoteStore(Path.Combine(_root, "restored"));
        var service = CreateService(
            CreateApi(new RestoreHandler(fixture)),
            restoredStore,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));
        var restoredImport = await service.RestoreVersionAsync(
            fixture.VersionId,
            userConfirmedLocalCopy: true);
        var restored = await restoredStore.LoadAsync(restoredImport.Import.NoteId);

        Assert.True(restored.IsServerSourceReadOnly);
        var restoredRecording = restored.Materials
            .Where(material => material.Kind == LocalNoteMaterialKind.RecordingSegment)
            .OrderBy(material => material.RecordingSegment)
            .ThenBy(material => material.RecordingChunkIndex)
            .First();
        Assert.Equal(expectedRecordingMaterialId, restoredRecording.MaterialId);
        Assert.Contains(expectedRecordingMarker, restored.Markdown, StringComparison.Ordinal);
        Assert.Equal(
            1,
            restored.Markdown.Split(expectedRecordingMarker, StringSplitOptions.None).Length - 1);
        var restoredLayout = NativeNoteDocumentLayout.ParseServerSource(
            restored.ServerSource!,
            restored.Materials);
        var renderedIds = restoredLayout.Segments
            .Where(segment => segment.Kind == NativeNoteDocumentSegmentKind.Material)
            .Select(segment => segment.MaterialId!.Value)
            .ToArray();
        Assert.Equal(3, renderedIds.Length);
        Assert.Equal(3, renderedIds.Distinct().Count());
        Assert.Contains(expectedRecordingMaterialId, renderedIds);
        Assert.Equal(
            photoMaterial.MaterialId,
            restored.Materials.Single(
                material => material.Kind == LocalNoteMaterialKind.Photo).MaterialId);
        Assert.Equal(
            [
                LocalNoteMaterialKind.Photo,
                LocalNoteMaterialKind.RecordingSegment,
                LocalNoteMaterialKind.Handwriting,
            ],
            restoredLayout.Segments
                .Where(segment => segment.Kind == NativeNoteDocumentSegmentKind.Material)
                .Select(segment => segment.Material!.Kind));

        var secondStore = new LocalNoteStore(Path.Combine(_root, "restored-again"));
        var secondService = CreateService(
            CreateApi(new RestoreHandler(fixture)),
            secondStore,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));
        var secondImport = await secondService.RestoreVersionAsync(
            fixture.VersionId,
            userConfirmedLocalCopy: true);
        var secondRestore = await secondStore.LoadAsync(secondImport.Import.NoteId);
        Assert.Equal(
            restored.Materials.Select(material =>
                (material.MaterialId, material.Kind, material.DisplayName)),
            secondRestore.Materials.Select(material =>
                (material.MaterialId, material.Kind, material.DisplayName)));

        var materialOrder = restored.Materials.Select(material => material.MaterialId).ToArray();
        var finished = await restoredStore.FinalizeAsync(
            restored.NoteId,
            restored.Title,
            restored.Markdown);
        Assert.Equal(
            materialOrder,
            finished.Materials.Select(material => material.MaterialId));
        var finishedVersion = await restoredStore.LoadVersionAsync(
            finished.NoteId,
            finished.CurrentVersionId);
        Assert.Equal(LocalNoteCheckpointReason.Finished, finishedVersion.Reason);
        Assert.True(finishedVersion.ServerSource is not null);
        Assert.Equal(
            restored.ServerSource!.Blocks.Select(block => (block.Id, block.Kind, block.RawJson)),
            finishedVersion.ServerSource!.Blocks.Select(block => (block.Id, block.Kind, block.RawJson)));

        var restoredFinished = await restoredStore.RestoreVersionAsync(
            finished.NoteId,
            finished.CurrentVersionId);
        Assert.Equal(restored.Markdown, restoredFinished.Markdown);
        Assert.Equal(
            materialOrder,
            restoredFinished.Materials.Select(material => material.MaterialId));
        var finishedLayout = NativeNoteDocumentLayout.ParseServerSource(
            restoredFinished.ServerSource!,
            restoredFinished.Materials);
        Assert.Equal(
            renderedIds,
            finishedLayout.Segments
                .Where(segment => segment.Kind == NativeNoteDocumentSegmentKind.Material)
                .Select(segment => segment.MaterialId!.Value));

        var rebuilt = await new ServerNoteMaterialBundleBuilder(restoredStore)
            .BuildAsync(finished.NoteId, finished.CurrentVersionId);
        using var rebuiltInput = JsonDocument.Parse(rebuilt.InputJson);
        var rebuiltMarkdownText = string.Join(
            "\n",
            rebuiltInput.RootElement.GetProperty("note").GetProperty("blocks")
                .EnumerateArray()
                .Where(block => block.GetProperty("kind").GetString() == "markdown")
                .Select(block => block.GetProperty("text").GetString()));
        Assert.Contains(expectedRecordingMarker, rebuiltMarkdownText, StringComparison.Ordinal);
        Assert.Equal(
            1,
            rebuiltMarkdownText.Split(expectedRecordingMarker, StringSplitOptions.None).Length - 1);
        Assert.Equal(
            blocks.Select(block => block.GetProperty("kind").GetString()),
            rebuiltInput.RootElement.GetProperty("note").GetProperty("blocks")
                .EnumerateArray()
                .Select(block => block.GetProperty("kind").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyMultiChunkSourceReuploadMapsEveryRecordingAnchorToStableFirstChunk(
        bool stableSourceIds)
    {
        var fixture = await CreateRecordingAnchorFixtureAsync();
        var parsed = ServerNoteMaterialRestoreParser.Parse(
            fixture.InputJson,
            fixture.ManifestJson,
            fixture.Markdown);
        var audioFiles = parsed.Files
            .Where(file => file.Kind == "AudioChunk")
            .OrderBy(file => file.Segment)
            .ThenBy(file => file.ChunkIndex)
            .ToArray();
        Assert.Equal(3, audioFiles.Length);
        var recordingId = Guid.Parse(audioFiles[0].RecordingId!);
        var stableIds = audioFiles
            .Select(file => ServerNoteMaterialStableId.ForRecordingChunk(
                recordingId,
                file.Segment!.Value,
                file.ChunkIndex!.Value,
                file.Samples!.Value))
            .ToArray();
        var sourceIds = stableSourceIds
            ? stableIds
            : CreateLegacyChunkIds(stableIds);
        var expectedFirstId = stableIds[0];
        var expectedFirstMarker = NativeNoteDocumentLayout.CreateMarker(expectedFirstId);

        var legacyStore = new LocalNoteStore(Path.Combine(_root, "legacy-source"));
        var legacyImport = await legacyStore.ImportServerRestoreAsync(
            await CreateLegacyServerPackageAsync(fixture, sourceIds));
        var legacy = await legacyStore.LoadAsync(legacyImport.NoteId);
        var legacyChunks = legacy.Materials
            .Where(material => material.Kind == LocalNoteMaterialKind.RecordingSegment)
            .ToArray();
        Assert.Equal(sourceIds, legacyChunks.Select(material => material.MaterialId));
        Assert.Equal(
            [(1, 1), (1, 2), (2, 1)],
            legacyChunks.Select(material => (
                material.RecordingSegment!.Value,
                material.RecordingChunkIndex!.Value)));
        var legacySourceChunk = Assert.Single(
            legacy.ServerSource!.Files.Where(file => file.Kind == "AudioChunk"),
            file => file.Segment == 1 && file.ChunkIndex == 1);
        Assert.Equal(sourceIds[0], legacySourceChunk.MaterialId);
        var legacyLayout = NativeNoteDocumentLayout.ParseServerSource(
            legacy.ServerSource,
            legacy.Materials);
        Assert.Equal(
            [stableSourceIds ? expectedFirstId : sourceIds[0]],
            legacyLayout.Segments
                .Where(segment => segment.Kind == NativeNoteDocumentSegmentKind.Material)
                .Select(segment => segment.MaterialId!.Value));

        var bundle = await new ServerNoteMaterialBundleBuilder(legacyStore)
            .BuildAsync(legacy.NoteId, legacy.CurrentVersionId);
        using var input = JsonDocument.Parse(bundle.InputJson);
        var markdownText = string.Join(
            "\n",
            input.RootElement.GetProperty("note").GetProperty("blocks")
                .EnumerateArray()
                .Where(block => block.GetProperty("kind").GetString() == "markdown")
                .Select(block => block.GetProperty("text").GetString()));
        Assert.Contains(expectedFirstMarker, markdownText, StringComparison.Ordinal);
        Assert.Equal(
            1,
            markdownText.Split(expectedFirstMarker, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(
            NativeNoteDocumentLayout.CreateMarker(recordingId),
            markdownText,
            StringComparison.Ordinal);
        foreach (var sourceId in sourceIds.Where(id => id != expectedFirstId))
        {
            Assert.DoesNotContain(
                NativeNoteDocumentLayout.CreateMarker(sourceId),
                markdownText,
                StringComparison.Ordinal);
        }

        string documentMarkdown;
        await using (var markdown = bundle.Files.Single(file => file.Kind == "Markdown").OpenRead())
        using (var reader = new StreamReader(markdown, Encoding.UTF8))
        {
            documentMarkdown = await reader.ReadToEndAsync();
        }
        Assert.Equal(markdownText, documentMarkdown);

        using var manifest = JsonDocument.Parse(bundle.ManifestJson);
        Assert.Equal(
            [(1, 1), (1, 2), (2, 1)],
            manifest.RootElement.GetProperty("files").EnumerateArray()
                .Where(file => file.GetProperty("kind").GetString() == "AudioChunk")
                .Select(file => (
                    file.GetProperty("segment").GetInt32(),
                    file.GetProperty("chunkIndex").GetInt32())));
        var recording = Assert.Single(input.RootElement.GetProperty("recordings").EnumerateArray());
        Assert.Equal(
            [(1, 1, 5L), (1, 2, 7L), (2, 1, 11L)],
            recording.GetProperty("chunks").EnumerateArray()
                .Select(chunk => (
                    chunk.GetProperty("segment").GetInt32(),
                    chunk.GetProperty("index").GetInt32(),
                    chunk.GetProperty("samples").GetInt64())));

        var restoredStore = new LocalNoteStore(Path.Combine(_root, "legacy-restored"));
        var service = CreateService(
            CreateApi(new RestoreHandler(await CreateFixtureFromBundleAsync(bundle))),
            restoredStore,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));
        var restoredImport = await service.RestoreVersionAsync(
            bundle.VersionId,
            userConfirmedLocalCopy: true);
        var restored = await restoredStore.LoadAsync(restoredImport.Import.NoteId);
        var restoredChunks = restored.Materials
            .Where(material => material.Kind == LocalNoteMaterialKind.RecordingSegment)
            .ToArray();
        Assert.Equal(stableIds, restoredChunks.Select(material => material.MaterialId));
        Assert.Equal(
            [(1, 1), (1, 2), (2, 1)],
            restoredChunks.Select(material => (
                material.RecordingSegment!.Value,
                material.RecordingChunkIndex!.Value)));
        Assert.Contains(expectedFirstMarker, restored.Markdown, StringComparison.Ordinal);
        Assert.Equal(
            1,
            restored.Markdown.Split(expectedFirstMarker, StringSplitOptions.None).Length - 1);
        var restoredLayout = NativeNoteDocumentLayout.ParseServerSource(
            restored.ServerSource!,
            restored.Materials);
        Assert.Equal(
            [expectedFirstId],
            restoredLayout.Segments
                .Where(segment => segment.Kind == NativeNoteDocumentSegmentKind.Material)
                .Select(segment => segment.MaterialId!.Value));
    }

    [Fact]
    public void SourceIntegrityValidatesLargeInterleavedPhotoSource()
    {
        const int photoCount = 1_000;
        var noteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var versionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var photoBytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1 };
        var photoHash = Hash(photoBytes);
        var blocks = new JsonArray();
        var markdownSections = new List<string>(photoCount * 2);
        var blockMaterialIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var index = 1; index <= photoCount; index++)
        {
            var markdownId = Guid.Parse($"10000000-0000-4000-8000-{index:x12}");
            var photoBlockId = Guid.Parse($"20000000-0000-4000-8000-{index:x12}");
            var assetId = Guid.Parse($"30000000-0000-4000-8000-{index:x12}");
            blocks.Add(new JsonObject
            {
                ["id"] = markdownId.ToString("D"),
                ["kind"] = "markdown",
                ["text"] = $"正文段落 {index}",
            });
            blocks.Add(new JsonObject
            {
                ["id"] = photoBlockId.ToString("D"),
                ["kind"] = "photo",
                ["assetId"] = assetId.ToString("D"),
                ["mimeType"] = "image/png",
                ["sha256"] = photoHash,
                ["width"] = 1,
                ["height"] = 1,
            });
            markdownSections.Add($"正文段落 {index}");
            markdownSections.Add($"![照片](assets/{assetId:D}.png)");
            blockMaterialIds.Add(photoBlockId.ToString("D"), assetId);
        }

        var title = "较大交错 source";
        var noteJson = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = noteId.ToString("D"),
            ["revision"] = 1,
            ["createdAtMillis"] = 1,
            ["title"] = title,
            ["blocks"] = blocks,
        }.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var markdown = string.Join("\n\n", markdownSections);
        var inputJson = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = versionId.ToString("D"),
            ["createdAtMillis"] = 1,
            ["note"] = JsonNode.Parse(noteJson),
            ["recordings"] = new JsonArray(),
        }.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var noteBytes = Encoding.UTF8.GetBytes(noteJson);
        var markdownBytes = Encoding.UTF8.GetBytes(markdown);
        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            versionId = versionId.ToString("D"),
            noteId = noteId.ToString("D"),
            revision = 1,
            inputSha256 = Hash(Encoding.UTF8.GetBytes(inputJson)),
            files = new[]
            {
                new
                {
                    path = "note.json",
                    kind = "Document",
                    sha256 = Hash(noteBytes),
                    sizeBytes = noteBytes.Length,
                    blockId = (string?)null,
                    recordingId = (string?)null,
                    segment = (int?)null,
                    chunkIndex = (int?)null,
                    samples = (long?)null,
                },
                new
                {
                    path = "document.md",
                    kind = "Markdown",
                    sha256 = Hash(markdownBytes),
                    sizeBytes = markdownBytes.Length,
                    blockId = (string?)null,
                    recordingId = (string?)null,
                    segment = (int?)null,
                    chunkIndex = (int?)null,
                    samples = (long?)null,
                },
            }.Concat(Enumerable.Range(1, photoCount).Select(index =>
            {
                var photoBlockId = $"20000000-0000-4000-8000-{index:x12}";
                var assetId = $"30000000-0000-4000-8000-{index:x12}";
                return new
                {
                    path = $"assets/{assetId}.png",
                    kind = "Photo",
                    sha256 = photoHash,
                    sizeBytes = photoBytes.Length,
                    blockId = (string?)photoBlockId,
                    recordingId = (string?)null,
                    segment = (int?)null,
                    chunkIndex = (int?)null,
                    samples = (long?)null,
                };
            })),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var parsed = ServerNoteMaterialRestoreParser.Parse(inputJson, manifestJson, markdown);
        var source = new LocalNoteServerSource(
            noteId,
            versionId,
            inputJson,
            manifestJson,
            noteJson,
            markdown,
            parsed.Blocks.Select(block => new LocalNoteServerSourceBlock(
                block.Id,
                block.Kind,
                block.RawJson,
                blockMaterialIds.TryGetValue(block.Id, out var materialId) ? [materialId] : [])).ToArray(),
            parsed.Files.Select(file => new LocalNoteServerSourceFile(
                file.Path,
                file.Kind,
                file.Sha256,
                file.SizeBytes,
                file.BlockId,
                file.RecordingId,
                file.Segment,
                file.ChunkIndex,
                file.Samples,
                file.BlockId is not null && blockMaterialIds.TryGetValue(file.BlockId, out var materialId)
                    ? materialId
                    : null,
                file.Kind switch
                {
                    "Document" => noteJson,
                    "Markdown" => markdown,
                    _ => null,
                })).ToArray());
        var materials = Enumerable.Range(1, photoCount).Select(index => new LocalNoteMaterial
        {
            MaterialId = Guid.Parse($"30000000-0000-4000-8000-{index:x12}"),
            Kind = LocalNoteMaterialKind.Photo,
            DisplayName = $"photo-{index:D4}.png",
            ContentSha256 = photoHash,
            RelativePath = $"materials/{index:D4}.png",
            MediaType = "image/png",
            Length = photoBytes.Length,
            AddedAt = DateTimeOffset.UnixEpoch,
            Width = 1,
            Height = 1,
        }).ToArray();

        ServerNoteSourceIntegrity.Validate(source, title, markdown, materials);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("text")]
    [InlineData("manifest")]
    public async Task ImportRejectsTamperedServerSourceBlocksAndKeepsExistingLibrary(string mutation)
    {
        var fixture = await CreateMixedRecordingFixtureAsync();
        var store = new LocalNoteStore(_root);
        var service = CreateService(
            CreateApi(new RestoreHandler(fixture)),
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));
        var restored = await service.RestoreVersionAsync(fixture.VersionId, userConfirmedLocalCopy: true);
        var original = await store.LoadAsync(restored.Import.NoteId);
        var version = await store.LoadVersionAsync(original.NoteId, original.CurrentVersionId);
        var source = version.ServerSource!;
        var tamperedBlocks = source.Blocks.ToArray();
        var tamperedManifestJson = source.ManifestJson;
        if (mutation == "manifest")
        {
            var manifest = JsonNode.Parse(tamperedManifestJson)!;
            manifest["files"]![0]!["sha256"] = new string('0', 64);
            tamperedManifestJson = manifest.ToJsonString();
        }
        else if (mutation == "order")
        {
            (tamperedBlocks[0], tamperedBlocks[2]) = (tamperedBlocks[2], tamperedBlocks[0]);
        }
        else
        {
            var rawBlock = JsonNode.Parse(tamperedBlocks[0].RawJson)!;
            rawBlock["text"] = "遭篡改的正文";
            tamperedBlocks[0] = tamperedBlocks[0] with { RawJson = rawBlock.ToJsonString() };
        }

        var tamperedSource = source with
        {
            Blocks = tamperedBlocks,
            ManifestJson = tamperedManifestJson,
        };
        var tamperedVersion = version with { ServerSource = tamperedSource };
        var zipPath = Path.Combine(_root, $"tampered-{mutation}.pimnote.zip");
        await store.ExportAsync(original.NoteId, original.CurrentVersionId, zipPath);
        var editedVersion = JsonSerializer.SerializeToNode(tamperedVersion, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });
        await RewriteZipEntryAsync(zipPath, "version.json", editedVersion!.ToJsonString());

        var existing = await store.CreateAsync("原有本机稿");
        await Assert.ThrowsAsync<LocalNoteStoreException>(() => store.ImportAsync(zipPath));
        var notes = await store.ListAsync();
        Assert.Contains(notes, note => note.NoteId == existing.NoteId && note.Title == "原有本机稿");
        var preserved = await store.LoadAsync(original.NoteId);
        Assert.Equal(source.Blocks.Select(block => block.RawJson), preserved.ServerSource!.Blocks.Select(block => block.RawJson));
    }

    [Fact]
    public async Task RestoreRejectsBadShaWithoutCreatingLocalNoteOrMaterial()
    {
        var fixture = await CreateFixtureAsync();
        var handler = new RestoreHandler(fixture) { ReturnWrongDigest = true };
        var store = new LocalNoteStore(_root);
        var existing = await store.CreateAsync("保留的本机笔记");
        var service = CreateService(
            CreateApi(handler),
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreVersionAsync(fixture.VersionId, userConfirmedLocalCopy: true));

        var notes = await store.ListAsync();
        Assert.Single(notes);
        Assert.Equal(existing.NoteId, notes[0].NoteId);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "materials"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RestoreRejectsActualBytesThatDoNotMatchVerifiedResponseAndManifestHash()
    {
        var fixture = await CreateFixtureAsync();
        var handler = new RestoreHandler(fixture) { CorruptFileIndex = 2 };
        var store = new LocalNoteStore(_root);
        var existing = await store.CreateAsync("必须保留");
        var service = CreateService(
            CreateApi(handler),
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreVersionAsync(fixture.VersionId, userConfirmedLocalCopy: true));

        var notes = await store.ListAsync();
        Assert.Single(notes);
        Assert.Equal(existing.NoteId, notes[0].NoteId);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "materials"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RestoreRejectsBudgetExceededBeforeDownloadingFiles()
    {
        var fixture = await CreateFixtureAsync();
        var handler = new RestoreHandler(fixture);
        var store = new LocalNoteStore(_root);
        var service = new CloudNoteMaterialService(
            CreateApi(handler),
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId),
            new LocalNoteImportLimits
            {
                MaxArchiveEntries = 8,
                MaxMetadataEntryBytes = 64 * 1024,
                MaxDocumentEntryBytes = 64 * 1024,
                MaxMaterialEntryBytes = 1024,
                MaxTotalUncompressedBytes = 16,
            });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreVersionAsync(fixture.VersionId, userConfirmedLocalCopy: true));

        Assert.Equal(0, handler.MaterialRequests);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task RestoreRejectsMetadataOverLimitBeforeDownloadingFiles()
    {
        var fixture = await CreateFixtureAsync();
        var handler = new RestoreHandler(fixture);
        var store = new LocalNoteStore(_root);
        var service = new CloudNoteMaterialService(
            CreateApi(handler),
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId),
            new LocalNoteImportLimits { MaxMetadataEntryBytes = 1 });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreVersionAsync(fixture.VersionId, userConfirmedLocalCopy: true));

        Assert.Equal(0, handler.MaterialRequests);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task RestoreRejectsAttachmentNameThatDoesNotMatchManifestPath()
    {
        var fixture = await CreateFixtureAsync();
        var handler = new RestoreHandler(fixture) { WrongFileName = true };
        var store = new LocalNoteStore(_root);
        var service = CreateService(
            CreateApi(handler),
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreVersionAsync(fixture.VersionId, userConfirmedLocalCopy: true));

        Assert.Empty(await store.ListAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "materials"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("server")]
    public async Task RestoreAccountOrServerSwitchDuringAwaitCancelsBeforeImportCommit(string switchKind)
    {
        var fixture = await CreateFixtureAsync();
        var handler = new RestoreHandler(fixture) { HoldFirstMaterial = true };
        var activeAccount = AccountId;
        var activeServer = "http://127.0.0.1:5858";
        var store = new LocalNoteStore(_root);
        var api = CreateApi(handler);
        var service = CreateService(api, store, () =>
            new CloudNoteAccountScope(activeServer, activeAccount));
        var task = service.RestoreVersionAsync(fixture.VersionId, userConfirmedLocalCopy: true);

        await handler.FirstMaterialRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (switchKind == "account")
        {
            activeAccount = "22222222-2222-2222-2222-222222222222";
            api.SetAccessToken("synthetic-new-owner-token");
        }
        else
        {
            activeServer = "https://other.example.test";
        }
        handler.ReleaseFirstMaterial.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Empty(await store.ListAsync());
        Assert.False(File.Exists(Path.Combine(_root, "cloud-bindings.json")));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("server")]
    public async Task RestoreRejectsSelectionLoadedUnderPreviousOwnerOrServer(string switchKind)
    {
        var fixture = await CreateFixtureAsync();
        var handler = new RestoreHandler(fixture);
        var activeAccount = AccountId;
        var activeServer = "http://127.0.0.1:5858";
        var api = CreateApi(handler);
        var store = new LocalNoteStore(_root);
        var service = CreateService(api, store, () =>
            new CloudNoteAccountScope(activeServer, activeAccount));
        var selectedScope = service.CurrentAccountScope!;
        var selectedGeneration = service.AccountGeneration;

        if (switchKind == "account")
        {
            activeAccount = "22222222-2222-2222-2222-222222222222";
            api.SetAccessToken("synthetic-new-owner-token");
        }
        else
        {
            activeServer = "https://other.example.test";
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreVersionAsync(
            fixture.VersionId,
            userConfirmedLocalCopy: true,
            expectedAccountScope: selectedScope,
            expectedAccountGeneration: selectedGeneration));

        Assert.Equal(0, handler.VersionRequests);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task RestoreRequiresExplicitConfirmation()
    {
        var fixture = await CreateFixtureAsync();
        var store = new LocalNoteStore(_root);
        var service = CreateService(
            CreateApi(new RestoreHandler(fixture)),
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreVersionAsync(fixture.VersionId, userConfirmedLocalCopy: false));

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task ServerSchemaParserRejectsDuplicatePathsAndMalformedShortHashes()
    {
        var fixture = await CreateFixtureAsync();
        var duplicatePath = JsonNode.Parse(fixture.ManifestJson)!;
        var duplicateFiles = duplicatePath["files"]!.AsArray();
        duplicateFiles[2]!["path"] = "document.md";
        var duplicateInput = JsonNode.Parse(fixture.InputJson)!.ToJsonString();
        var duplicateManifest = duplicatePath.ToJsonString();
        duplicatePath["inputSha256"] = Hash(Encoding.UTF8.GetBytes(duplicateInput));
        duplicateManifest = duplicatePath.ToJsonString();

        Assert.Throws<LocalNoteStoreException>(() =>
            ServerNoteMaterialRestoreParser.Parse(duplicateInput, duplicateManifest));

        var shortHash = JsonNode.Parse(fixture.ManifestJson)!;
        shortHash["files"]![2]!["sha256"] = "abc";
        Assert.Throws<LocalNoteStoreException>(() =>
            ServerNoteMaterialRestoreParser.Parse(
                fixture.InputJson,
                shortHash.ToJsonString()));
    }

    private async Task<LocalNoteServerRestorePackage> CreateLegacyServerPackageAsync(
        RestoreFixture fixture,
        IReadOnlyList<Guid> audioMaterialIds)
    {
        var parsed = ServerNoteMaterialRestoreParser.Parse(
            fixture.InputJson,
            fixture.ManifestJson,
            fixture.Markdown);
        if (parsed.Blocks.Any(block => block.Kind != "markdown")
            || parsed.Files.Any(file => file.Kind is "Photo" or "InkPreview"))
        {
            throw new InvalidOperationException("旧版录音 source 测试只支持 Markdown 与录音材料。");
        }

        var bytesByPath = fixture.Files.ToDictionary(file => file.Path, file => file.Bytes);
        var audioFiles = parsed.Files
            .Where(file => file.Kind == "AudioChunk")
            .OrderBy(file => file.Segment)
            .ThenBy(file => file.ChunkIndex)
            .ToArray();
        if (audioFiles.Length != audioMaterialIds.Count)
        {
            throw new InvalidOperationException("旧版录音 source 的分片与材料标识数量不一致。");
        }

        var staging = Path.Combine(_root, "legacy-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var materialIdsByPath = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var materials = new List<LocalNoteServerMaterialInput>(audioFiles.Length);
        for (var index = 0; index < audioFiles.Length; index++)
        {
            var file = audioFiles[index];
            var materialId = audioMaterialIds[index];
            var stagedPath = Path.Combine(staging, $"{index:D4}-{Path.GetFileName(file.Path)}");
            await File.WriteAllBytesAsync(stagedPath, bytesByPath[file.Path]);
            materialIdsByPath.Add(file.Path, materialId);
            materials.Add(new LocalNoteServerMaterialInput(
                materialId,
                LocalNoteMaterialKind.RecordingSegment,
                $"录音-{index + 1:D3}-s{file.Segment:D4}-c{file.ChunkIndex:D6}.wav",
                file.Sha256,
                "audio/wav",
                file.SizeBytes,
                stagedPath,
                ExpectedSamples: file.Samples,
                RecordingId: Guid.Parse(file.RecordingId!),
                RecordingSegment: file.Segment,
                RecordingChunkIndex: file.ChunkIndex));
        }

        var noteJson = Encoding.UTF8.GetString(
            bytesByPath[parsed.Files.Single(file => file.Kind == "Document").Path]);
        var recordingManifest = parsed.Files.Single(file => file.Kind == "RecordingManifest");
        var manifestJson = Encoding.UTF8.GetString(bytesByPath[recordingManifest.Path]);
        var serverSource = new LocalNoteServerSource(
            parsed.NoteId,
            parsed.VersionId,
            fixture.InputJson,
            fixture.ManifestJson,
            noteJson,
            parsed.Markdown,
            parsed.Blocks
                .Select(block => new LocalNoteServerSourceBlock(
                    block.Id,
                    block.Kind,
                    block.RawJson,
                    []))
                .ToArray(),
            parsed.Files
                .Select(file => new LocalNoteServerSourceFile(
                    file.Path,
                    file.Kind,
                    file.Sha256,
                    file.SizeBytes,
                    file.BlockId,
                    file.RecordingId,
                    file.Segment,
                    file.ChunkIndex,
                    file.Samples,
                    materialIdsByPath.GetValueOrDefault(file.Path),
                    file.Kind switch
                    {
                        "Document" => noteJson,
                        "Markdown" => parsed.Markdown,
                        "RecordingManifest" => manifestJson,
                        _ => null,
                    }))
                .ToArray());

        return new LocalNoteServerRestorePackage(
            parsed.NoteId,
            parsed.VersionId,
            parsed.Title,
            parsed.Markdown,
            materials,
            serverSource);
    }

    private static Guid[] CreateLegacyChunkIds(IReadOnlyList<Guid> stableIds)
    {
        var result = stableIds
            .Select((_, index) => Guid.Parse($"70000000-0000-4000-8000-{index + 1:x12}"))
            .ToArray();
        if (result.Intersect(stableIds).Any())
        {
            throw new InvalidOperationException("旧版录音测试标识与稳定标识发生碰撞。");
        }

        return result;
    }

    private static async Task<RestoreFixture> CreateRecordingAnchorFixtureAsync()
    {
        var noteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var versionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var recordingId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var markdown = string.Join(
            "\n\n",
            "开头",
            NativeNoteDocumentLayout.CreateMarker(recordingId),
            "旧录音锚点",
            NativeNoteDocumentLayout.CreateMarker(recordingId),
            "末尾");
        var noteJson = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = noteId.ToString("D"),
            ["revision"] = 7,
            ["createdAtMillis"] = 7,
            ["title"] = "旧版多分片录音",
            ["blocks"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "11111111-1111-4111-8111-111111111111",
                    ["kind"] = "markdown",
                    ["text"] = markdown,
                }),
        }.ToJsonString();
        var recordingJson = new JsonObject
        {
            ["id"] = recordingId.ToString("D"),
            ["createdAtMillis"] = 7,
            ["phase"] = "Completed",
            ["currentSegment"] = 2,
            ["chunks"] = new JsonArray(
                new JsonObject { ["segment"] = 1, ["index"] = 1, ["samples"] = 5 },
                new JsonObject { ["segment"] = 1, ["index"] = 2, ["samples"] = 7 },
                new JsonObject { ["segment"] = 2, ["index"] = 1, ["samples"] = 11 }),
            ["problem"] = null,
            ["schemaVersion"] = 1,
            ["noteId"] = noteId.ToString("D"),
        }.ToJsonString();
        var inputJson = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = versionId.ToString("D"),
            ["createdAtMillis"] = 7,
            ["note"] = JsonNode.Parse(noteJson),
            ["recordings"] = new JsonArray(JsonNode.Parse(recordingJson)),
        }.ToJsonString();
        var files = new List<RestoreFile>
        {
            new("note.json", "Document", Encoding.UTF8.GetBytes(noteJson), null),
            new("document.md", "Markdown", Encoding.UTF8.GetBytes(markdown), null),
            new(
                $"recordings/{recordingId:D}/manifest.json",
                "RecordingManifest",
                Encoding.UTF8.GetBytes(recordingJson),
                null,
                recordingId.ToString("D")),
        };
        files.AddRange(new[] { 5L, 7L, 11L }.Select((samples, index) =>
        {
            var segment = index == 2 ? 2 : 1;
            var chunk = index == 1 ? 2 : 1;
            return new RestoreFile(
                $"recordings/{recordingId:D}/s{segment:D4}-c{chunk:D6}.wav",
                "AudioChunk",
                SyntheticWave(samples),
                null,
                recordingId.ToString("D"),
                segment,
                chunk,
                samples);
        }));
        var manifestJson = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["versionId"] = versionId.ToString("D"),
            ["noteId"] = noteId.ToString("D"),
            ["revision"] = 7,
            ["inputSha256"] = Hash(Encoding.UTF8.GetBytes(inputJson)),
            ["files"] = new JsonArray(files.Select(file => (JsonNode)new JsonObject
            {
                ["path"] = file.Path,
                ["kind"] = file.Kind,
                ["sha256"] = Hash(file.Bytes),
                ["sizeBytes"] = file.Bytes.Length,
                ["blockId"] = file.BlockId,
                ["recordingId"] = file.RecordingId,
                ["segment"] = file.Segment,
                ["chunkIndex"] = file.ChunkIndex,
                ["samples"] = file.Samples,
            }).ToArray()),
        }.ToJsonString();
        var upload = new NoteMaterialUploadDto
        {
            BaseVersionId = null,
            CreatedAt = DateTimeOffset.UtcNow,
            CurrentVersionId = versionId.ToString("D"),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            Files = files.Select((file, index) => new NoteMaterialUploadFileDto
            {
                Index = index,
                Kind = file.Kind,
                PartCount = 0,
                Path = file.Path,
                Sha256 = Hash(file.Bytes),
                SizeBytes = file.Bytes.Length,
                UploadedParts = [],
            }).ToList(),
            HasConflict = false,
            IsCurrent = true,
            Limits = new NoteMaterialUploadLimitsDto
            {
                PartSizeBytes = 1_048_576,
                MaxMetadataCharacters = 8_388_608,
                MaxVersionBytes = 10_000_000,
                MaxUserReservedBytes = 10_000_000,
                MaxFiles = 20,
                MaxPendingVersions = 5,
                MaxVersionsPerUser = 100,
                UploadExpiryHours = 24,
            },
            NoteId = noteId.ToString("D"),
            Phase = "Uploaded",
            VersionId = versionId.ToString("D"),
        };
        var response = new NoteMaterialVersionDto
        {
            InputJson = inputJson,
            ManifestJson = manifestJson,
            Upload = upload,
        };
        return await Task.FromResult(new RestoreFixture(
            noteId,
            versionId,
            [],
            markdown,
            inputJson,
            manifestJson,
            response,
            files));
    }

    private static async Task<RestoreFixture> CreateFixtureFromBundleAsync(
        ServerNoteMaterialBundle bundle)
    {
        var files = new List<RestoreFile>(bundle.Files.Count);
        foreach (var file in bundle.Files)
        {
            await using var stream = file.OpenRead();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            files.Add(new RestoreFile(
                file.Path,
                file.Kind,
                buffer.ToArray(),
                file.BlockId,
                file.RecordingId,
                file.Segment,
                file.ChunkIndex,
                file.Samples));
        }

        var markdown = Encoding.UTF8.GetString(
            files.Single(file => file.Path == "document.md").Bytes);
        using var input = JsonDocument.Parse(bundle.InputJson);
        var noteId = Guid.Parse(
            input.RootElement.GetProperty("note").GetProperty("id").GetString()!);
        var upload = new NoteMaterialUploadDto
        {
            BaseVersionId = null,
            CreatedAt = DateTimeOffset.UtcNow,
            CurrentVersionId = bundle.VersionId.ToString("D"),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            Files = files.Select((file, index) => new NoteMaterialUploadFileDto
            {
                Index = index,
                Kind = file.Kind,
                PartCount = file.Bytes.Length == 0 ? 0 : 1,
                Path = file.Path,
                Sha256 = Hash(file.Bytes),
                SizeBytes = file.Bytes.Length,
                UploadedParts = [],
            }).ToList(),
            HasConflict = false,
            IsCurrent = true,
            Limits = new NoteMaterialUploadLimitsDto
            {
                PartSizeBytes = 1_048_576,
                MaxMetadataCharacters = 8_388_608,
                MaxVersionBytes = 64L * 1024 * 1024 * 1024,
                MaxUserReservedBytes = 64L * 1024 * 1024 * 1024,
                MaxFiles = 100_000,
                MaxPendingVersions = 20,
                MaxVersionsPerUser = 10_000,
                UploadExpiryHours = 24,
            },
            NoteId = noteId.ToString("D"),
            Phase = "Uploaded",
            VersionId = bundle.VersionId.ToString("D"),
        };
        var response = new NoteMaterialVersionDto
        {
            InputJson = bundle.InputJson,
            ManifestJson = bundle.ManifestJson,
            Upload = upload,
        };
        return new RestoreFixture(
            noteId,
            bundle.VersionId,
            files.FirstOrDefault(file => file.Kind == "Photo")?.Bytes ?? [],
            markdown,
            bundle.InputJson,
            bundle.ManifestJson,
            response,
            files);
    }

    private static async Task<RestoreFixture> CreateFixtureAsync()
    {
        var noteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var versionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var photoId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var blockId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var photoBytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        var noteJson = $$"""
            {"schemaVersion":1,"id":"{{noteId:D}}","revision":1,"createdAtMillis":1,
             "title":"恢复材料","blocks":[
               {"id":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee","kind":"markdown","text":"正文"},
               {"id":"{{blockId:D}}","kind":"photo","assetId":"{{photoId:D}}","mimeType":"image/png","sha256":"{{Hash(photoBytes)}}","width":1,"height":1}
             ]}
            """;
        var markdown = $"正文\n\n![照片](assets/{photoId:D}.png)";
        var input = $$"""
            {"schemaVersion":1,"id":"{{versionId:D}}","createdAtMillis":1,
             "note":{{noteJson}},"recordings":[]}
            """;
        var noteBytes = Encoding.UTF8.GetBytes(noteJson);
        var markdownBytes = Encoding.UTF8.GetBytes(markdown);
        var files = new[]
        {
            new RestoreFile("note.json", "Document", noteBytes, null),
            new RestoreFile("document.md", "Markdown", markdownBytes, null),
            new RestoreFile($"assets/{photoId:D}.png", "Photo", photoBytes, blockId.ToString("D")),
        };
        var inputSha = Hash(Encoding.UTF8.GetBytes(input));
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            versionId = versionId.ToString("D"),
            noteId = noteId.ToString("D"),
            revision = 1,
            inputSha256 = inputSha,
            files = files.Select(file => new
            {
                path = file.Path,
                kind = file.Kind,
                sha256 = Hash(file.Bytes),
                sizeBytes = file.Bytes.Length,
                blockId = file.BlockId,
                recordingId = (string?)null,
                segment = (int?)null,
                chunkIndex = (int?)null,
                samples = (long?)null,
            }),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var upload = new NoteMaterialUploadDto
        {
            BaseVersionId = null,
            CreatedAt = DateTimeOffset.UtcNow,
            CurrentVersionId = versionId.ToString("D"),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            Files = files.Select((file, index) => new NoteMaterialUploadFileDto
            {
                Index = index,
                Kind = file.Kind,
                PartCount = 0,
                Path = file.Path,
                Sha256 = Hash(file.Bytes),
                SizeBytes = file.Bytes.Length,
                UploadedParts = [],
            }).ToList(),
            HasConflict = false,
            IsCurrent = true,
            Limits = new NoteMaterialUploadLimitsDto
            {
                PartSizeBytes = 1_048_576,
                MaxMetadataCharacters = 8_388_608,
                MaxVersionBytes = 10_000_000,
                MaxUserReservedBytes = 10_000_000,
                MaxFiles = 10,
                MaxPendingVersions = 5,
                MaxVersionsPerUser = 100,
                UploadExpiryHours = 24,
            },
            NoteId = noteId.ToString("D"),
            Phase = "Uploaded",
            VersionId = versionId.ToString("D"),
        };
        var response = new NoteMaterialVersionDto
        {
            InputJson = input,
            ManifestJson = manifest,
            Upload = upload,
        };
        return await Task.FromResult(new RestoreFixture(
            noteId,
            versionId,
            photoBytes,
            markdown,
            input,
            manifest,
            response,
            files));
    }

    private static void AssertInkUnknownFields(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(
            "server",
            root.GetProperty("futureRoot").GetProperty("from").GetString());
        Assert.Equal(
            "red",
            root.GetProperty("strokeStyles")[0].GetProperty("futureStyle").GetString());
        Assert.Equal(
            "pen",
            root.GetProperty("strokes")[0].GetProperty("futureStroke").GetString());
        Assert.Equal(
            5,
            root.GetProperty("strokes")[0].GetProperty("points")[0]
                .GetProperty("futurePoint").GetInt32());
    }

    private static async Task<RestoreFixture> CreateMixedRecordingFixtureAsync()
    {
        var noteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var versionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var recordingId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var photoAssetId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var photoBlockId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var inkBlockId = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");
        var photoBytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1 };
        var inkPreview = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 2 };
        const string ink = """
            {
              "width": 320,
              "height": 180,
              "futureRoot": {"from": "server"},
              "strokeIds": ["44444444-4444-4444-8444-444444444444"],
              "strokeStyles": [{"color": "#b8373c", "width": 1.8, "futureStyle": "red"}],
              "strokes": [
                {
                  "color": "#b8373c",
                  "width": 1.8,
                  "futureStroke": "pen",
                  "points": [{"x": 10, "y": 20, "pressure": 0.5, "futurePoint": 5}]
                }
              ]
            }
            """;
        var inkHash = Hash(Encoding.UTF8.GetBytes(ink));
        var noteJson = $$"""
            {"schemaVersion":1,"id":"{{noteId:D}}","revision":7,"createdAtMillis":7,"title":"交错材料",
             "blocks":[
               {"id":"11111111-1111-4111-8111-111111111111","kind":"markdown","text":"开头"},
               {"id":"{{photoBlockId:D}}","kind":"photo","assetId":"{{photoAssetId:D}}","mimeType":"image/png","sha256":"{{Hash(photoBytes)}}","width":1,"height":1},
               {"id":"22222222-2222-4222-8222-222222222222","kind":"markdown","text":"照片之后"},
               {"id":"{{inkBlockId:D}}","kind":"ink","ink":{{ink}}},
               {"id":"33333333-3333-4333-8333-333333333333","kind":"markdown","text":"末尾"}
             ]}
            """;
        var photoPath = $"assets/{photoAssetId:D}.png";
        var inkPath = $"assets/{inkBlockId:D}-{inkHash}.png";
        var markdown = $"开头\n\n![照片]({photoPath})\n\n照片之后\n\n![手写卡片]({inkPath})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->\n\n末尾";
        var recordingJson = $$"""
            {"id":"{{recordingId:D}}","createdAtMillis":7,"phase":"Completed","currentSegment":2,
             "chunks":[{"segment":1,"index":1,"samples":5},{"segment":1,"index":2,"samples":7},{"segment":2,"index":1,"samples":11}],
             "problem":null,"schemaVersion":1,"noteId":"{{noteId:D}}"}
            """;
        var input = $$"""
            {"schemaVersion":1,"id":"{{versionId:D}}","createdAtMillis":7,
             "note":{{noteJson}},"recordings":[{{recordingJson}}]}
            """;
        var files = new List<RestoreFile>
        {
            new("note.json", "Document", Encoding.UTF8.GetBytes(noteJson), null),
            new("document.md", "Markdown", Encoding.UTF8.GetBytes(markdown), null),
            new(photoPath, "Photo", photoBytes, photoBlockId.ToString("D")),
            new(inkPath, "InkPreview", inkPreview, inkBlockId.ToString("D")),
            new(
                $"recordings/{recordingId:D}/manifest.json",
                "RecordingManifest",
                Encoding.UTF8.GetBytes(recordingJson),
                null,
                recordingId.ToString("D")),
        };
        files.AddRange(new[] { 5L, 7L, 11L }.Select((samples, index) =>
        {
            var segment = index == 2 ? 2 : 1;
            var chunk = index == 1 ? 2 : 1;
            var wav = SyntheticWave(samples);
            return new RestoreFile(
                $"recordings/{recordingId:D}/s{segment:D4}-c{chunk:D6}.wav",
                "AudioChunk",
                wav,
                null,
                recordingId.ToString("D"),
                segment,
                chunk,
                samples);
        }));
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            versionId = versionId.ToString("D"),
            noteId = noteId.ToString("D"),
            revision = 7,
            inputSha256 = Hash(Encoding.UTF8.GetBytes(input)),
            files = files.Select(file => new
            {
                path = file.Path,
                kind = file.Kind,
                sha256 = Hash(file.Bytes),
                sizeBytes = file.Bytes.Length,
                blockId = file.BlockId,
                recordingId = file.RecordingId,
                segment = file.Segment,
                chunkIndex = file.ChunkIndex,
                samples = file.Samples,
            }),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var upload = new NoteMaterialUploadDto
        {
            BaseVersionId = null,
            CreatedAt = DateTimeOffset.UtcNow,
            CurrentVersionId = versionId.ToString("D"),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            Files = files.Select((file, index) => new NoteMaterialUploadFileDto
            {
                Index = index,
                Kind = file.Kind,
                PartCount = 0,
                Path = file.Path,
                Sha256 = Hash(file.Bytes),
                SizeBytes = file.Bytes.Length,
                UploadedParts = [],
            }).ToList(),
            HasConflict = false,
            IsCurrent = true,
            Limits = new NoteMaterialUploadLimitsDto
            {
                PartSizeBytes = 1_048_576,
                MaxMetadataCharacters = 8_388_608,
                MaxVersionBytes = 10_000_000,
                MaxUserReservedBytes = 10_000_000,
                MaxFiles = 20,
                MaxPendingVersions = 5,
                MaxVersionsPerUser = 100,
                UploadExpiryHours = 24,
            },
            NoteId = noteId.ToString("D"),
            Phase = "Uploaded",
            VersionId = versionId.ToString("D"),
        };
        var response = new NoteMaterialVersionDto
        {
            InputJson = input,
            ManifestJson = manifest,
            Upload = upload,
        };
        return await Task.FromResult(new RestoreFixture(
            noteId,
            versionId,
            photoBytes,
            markdown,
            input,
            manifest,
            response,
            files));
    }

    private static async Task RewriteZipEntryAsync(string path, string entryName, string replacement)
    {
        var temporaryPath = path + ".rewrite";
        await using (var inputFile = File.OpenRead(path))
        using (var input = new System.IO.Compression.ZipArchive(
                   inputFile,
                   System.IO.Compression.ZipArchiveMode.Read,
                   leaveOpen: true))
        await using (var outputFile = File.Create(temporaryPath))
        using (var output = new System.IO.Compression.ZipArchive(
                   outputFile,
                   System.IO.Compression.ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                var target = output.CreateEntry(entry.FullName);
                await using var targetStream = target.Open();
                if (entry.FullName == entryName)
                {
                    await using var writer = new StreamWriter(targetStream, new UTF8Encoding(false), leaveOpen: true);
                    await writer.WriteAsync(replacement);
                }
                else
                {
                    await using var sourceStream = entry.Open();
                    await sourceStream.CopyToAsync(targetStream);
                }
            }
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static byte[] SyntheticWave(long sampleCount)
    {
        var dataBytes = checked((int)(sampleCount * 2));
        var wave = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), checked((uint)(wave.Length - 8)));
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), 16_000);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), 32_000);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), checked((uint)dataBytes));
        return wave;
    }

    private CloudNoteMaterialService CreateService(
        ApiClient api,
        LocalNoteStore store,
        Func<CloudNoteAccountScope?> accountProvider)
        => new(api, store, accountProvider);

    private static ApiClient CreateApi(HttpMessageHandler handler)
    {
        var api = new ApiClient(() => handler);
        api.SetAccessToken("synthetic-owner-token");
        return api;
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record RestoreFile(
        string Path,
        string Kind,
        byte[] Bytes,
        string? BlockId,
        string? RecordingId = null,
        int? Segment = null,
        int? ChunkIndex = null,
        long? Samples = null);

    private sealed record RestoreFixture(
        Guid NoteId,
        Guid VersionId,
        byte[] PhotoBytes,
        string Markdown,
        string InputJson,
        string ManifestJson,
        NoteMaterialVersionDto Response,
        IReadOnlyList<RestoreFile> Files);

    private sealed class RestoreHandler(RestoreFixture fixture) : HttpMessageHandler
    {
        private int _materialRequests;
        private int _versionRequests;

        public int MaterialRequests => _materialRequests;
        public int VersionRequests => _versionRequests;
        public List<(Guid VersionId, int Index)> MaterialRequestsSeen { get; } = [];
        public bool ReturnWrongDigest { get; set; }
        public int? CorruptFileIndex { get; set; }
        public bool WrongFileName { get; set; }
        public bool HoldFirstMaterial { get; set; }
        public TaskCompletionSource FirstMaterialRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstMaterial { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get
                && path == $"/api/v1/native-notes/versions/{fixture.VersionId:D}")
            {
                Interlocked.Increment(ref _versionRequests);
                return JsonResponse(new NoteMaterialVersionDtoApiResponse
                {
                    Code = 0,
                    Data = fixture.Response,
                    Message = "ok",
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }

            if (request.Method == HttpMethod.Get
                && path.StartsWith($"/api/v1/native-notes/versions/{fixture.VersionId:D}/files/", StringComparison.Ordinal))
            {
                var index = int.Parse(path[(path.LastIndexOf('/') + 1)..]);
                Interlocked.Increment(ref _materialRequests);
                MaterialRequestsSeen.Add((fixture.VersionId, index));
                if (HoldFirstMaterial && index == 0)
                {
                    FirstMaterialRequested.TrySetResult();
                    await ReleaseFirstMaterial.Task.WaitAsync(cancellationToken);
                }

                var file = fixture.Files[index];
                var body = file.Bytes;
                if (CorruptFileIndex == index)
                {
                    body = file.Bytes.ToArray();
                    body[^1] ^= 0x01;
                }
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "application/octet-stream");
                content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                {
                    FileName = WrongFileName ? "wrong-name.bin" : Path.GetFileName(file.Path),
                };
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content,
                };
                response.Headers.TryAddWithoutValidation("Cache-Control", "private, no-store");
                response.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
                response.Headers.TryAddWithoutValidation(
                    "X-Content-SHA256",
                    ReturnWrongDigest
                        ? new string('0', 64)
                        : Hash(file.Bytes));
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Encoding.UTF8,
                    "application/json"),
            };
    }
}
