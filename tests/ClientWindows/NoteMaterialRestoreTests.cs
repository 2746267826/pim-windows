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
        Assert.Equal(3, rebuilt.Files.Count(file => file.Kind == "AudioChunk"));
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
        const string ink = "{\"width\":320,\"height\":180,\"strokes\":[{\"width\":2,\"points\":[{\"x\":10,\"y\":20,\"pressure\":0.5}]}]}";
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
