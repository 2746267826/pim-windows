using System.Net;
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
        Assert.Equal("正文", restored.Markdown);
        var restoredMaterial = Assert.Single(restored.Materials);
        Assert.Equal(Hash(fixture.PhotoBytes), restoredMaterial.ContentSha256);
        Assert.Equal(fixture.PhotoBytes, await File.ReadAllBytesAsync(
            Path.Combine(_root, restoredMaterial.RelativePath)));
        Assert.Equal(3, handler.MaterialRequests);
        Assert.All(handler.MaterialRequestsSeen, request => Assert.Equal(fixture.VersionId, request.VersionId));
        Assert.False(File.Exists(Path.Combine(_root, "cloud-bindings.json")));
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

    private sealed record RestoreFile(string Path, string Kind, byte[] Bytes, string? BlockId);

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
