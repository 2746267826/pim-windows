using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class CloudNoteMaterialServiceTests : IDisposable
{
    private const string AccountId = "11111111-1111-1111-1111-111111111111";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-cloud-note-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BundleUsesServerSchemaAndPinsEveryMixedMaterial()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("课堂记录");
        note = await store.SaveAsync(note.NoteId, "课堂记录", "# 板书\n正文");
        await using (var photo = new MemoryStream(OnePixelPng))
        {
            note = await store.AddMaterialsAsync(note.NoteId,
            [
                new LocalNoteMaterialInput(photo, "photo.png", LocalNoteMaterialKind.Photo, "image/png"),
            ]);
        }

        using (var inkPreview = new MemoryStream(OnePixelPng))
        {
            note = await store.AddHandwritingAsync(
                note.NoteId,
                inkPreview,
                """{"SchemaVersion":1,"Width":320,"Height":180,"Strokes":[[{"X":10,"Y":20,"Pressure":0.5},{"X":30,"Y":40,"Pressure":0.7}]]}""",
                "手写 1",
                320,
                180);
        }

        var wave = SyntheticWave(800);
        await using (var recording = new MemoryStream(wave))
        {
            note = await store.AddMaterialsAsync(note.NoteId,
            [
                new LocalNoteMaterialInput(
                    recording,
                    "录音 1.wav",
                    LocalNoteMaterialKind.Recording,
                    "audio/wav"),
            ]);
        }

        var bundle = await new ServerNoteMaterialBundleBuilder(store)
            .BuildAsync(note.NoteId, note.CurrentVersionId);
        using var input = JsonDocument.Parse(bundle.InputJson);
        using var manifest = JsonDocument.Parse(bundle.ManifestJson);
        using var serverNote = JsonDocument.Parse(
            input.RootElement.GetProperty("note").GetRawText());

        Assert.Equal(1, input.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(note.NoteId.ToString("D"), serverNote.RootElement.GetProperty("id").GetString());
        Assert.Equal("课堂记录", serverNote.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            new[] { "markdown", "photo", "ink" },
            serverNote.RootElement.GetProperty("blocks").EnumerateArray()
                .Select(block => block.GetProperty("kind").GetString()).ToArray());
        var ink = serverNote.RootElement.GetProperty("blocks")[2].GetProperty("ink");
        Assert.Equal(320, ink.GetProperty("width").GetDouble());
        Assert.Equal(2, ink.GetProperty("strokes")[0].GetProperty("points").GetArrayLength());

        var recordings = input.RootElement.GetProperty("recordings");
        Assert.Single(recordings.EnumerateArray());
        Assert.Equal(
            800,
            recordings[0].GetProperty("chunks")[0].GetProperty("samples").GetInt64());

        var files = manifest.RootElement.GetProperty("files");
        Assert.Equal(6, files.GetArrayLength());
        Assert.Equal("note.json", files[0].GetProperty("path").GetString());
        Assert.Equal("document.md", files[1].GetProperty("path").GetString());
        Assert.Equal("Photo", files[2].GetProperty("kind").GetString());
        Assert.Equal("InkPreview", files[3].GetProperty("kind").GetString());
        Assert.StartsWith("assets/", files[3].GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.EndsWith(".png", files[3].GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.Equal("RecordingManifest", files[4].GetProperty("kind").GetString());
        Assert.Equal("AudioChunk", files[5].GetProperty("kind").GetString());
        Assert.Contains("![手写卡片](assets/", bundle.Files[1].OpenReadText(), StringComparison.Ordinal);

        Assert.Equal(bundle.Files.Count, files.GetArrayLength());
        Assert.Equal(
            Hash(Encoding.UTF8.GetBytes(bundle.InputJson)),
            manifest.RootElement.GetProperty("inputSha256").GetString());
        foreach (var file in bundle.Files)
        {
            await using var stream = file.OpenRead();
            Assert.Equal(file.SizeBytes, stream.Length);
            Assert.Equal(file.Sha256, await HashAsync(stream));
        }
    }

    [Fact]
    public async Task UploadRecoversStableVersionSkipsPartsAndEstablishesHead()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("云端");
        note = await store.SaveAsync(note.NoteId, "云端", "第一版");
        var bundle = await new ServerNoteMaterialBundleBuilder(store)
            .BuildAsync(note.NoteId, note.CurrentVersionId);
        var handler = new NoteCloudHandler(bundle);
        var api = new ApiClient(() => handler);
        api.SetAccessToken("synthetic-test-token");
        var service = new CloudNoteMaterialService(
            api,
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));

        var first = await service.UploadVersionAsync(
            note.NoteId,
            note.CurrentVersionId,
            userConfirmedStorage: true);
        Assert.Null(first.Binding.BaseVersionId);
        Assert.Equal(note.CurrentVersionId.ToString("D"), first.Binding.CurrentVersionId?.ToString("D"));
        Assert.Equal("Uploaded", first.Upload.Phase);
        Assert.Equal(2, handler.PartUploads);
        Assert.All(handler.PartDigests, digest => Assert.Matches("^[0-9a-f]{64}$", digest));
        Assert.True(File.Exists(Path.Combine(_root, "cloud-bindings.json")));

        handler.Reset(bundle, uploadedParts: true);
        var retry = await service.UploadVersionAsync(
            note.NoteId,
            note.CurrentVersionId,
            userConfirmedStorage: true);
        Assert.Null(retry.Upload.BaseVersionId);
        Assert.Equal(0, handler.PartUploads);

        note = await store.SaveAsync(note.NoteId, "云端", "第二版");
        bundle = await new ServerNoteMaterialBundleBuilder(store)
            .BuildAsync(note.NoteId, note.CurrentVersionId);
        handler.Reset(bundle, uploadedParts: false);
        var second = await service.UploadVersionAsync(
            note.NoteId,
            note.CurrentVersionId,
            userConfirmedStorage: true);
        Assert.Equal(first.Binding.CurrentVersionId?.ToString("D"), second.Upload.BaseVersionId);
        Assert.Equal(2, handler.PartUploads);
    }

    [Fact]
    public async Task OwnerServerBindingIsPersistedSeparatelyFromNoteContent()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("绑定");
        var binding = new LocalNoteCloudBinding
        {
            NoteId = note.NoteId,
            ServerUrl = "https://pim.example.test",
            AccountId = AccountId,
            BaseVersionId = null,
            CurrentVersionId = note.CurrentVersionId,
            LastVersionId = note.CurrentVersionId,
            Phase = "Uploaded",
            HasConflict = false,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveCloudBindingAsync(binding);

        var loaded = await store.LoadCloudBindingAsync(
            note.NoteId,
            "https://pim.example.test",
            AccountId);
        Assert.Equal(binding, loaded);
        Assert.Null(await store.LoadCloudBindingAsync(
            note.NoteId,
            "https://other.example.test",
            AccountId));
        _ = await store.ExportAsync(
            note.NoteId,
            note.CurrentVersionId,
            Path.Combine(_root, "binding.zip"));
        Assert.DoesNotContain("cloud-bindings.json", File.ReadAllText(
            Path.Combine(_root, "notes", note.NoteId.ToString("N"), "note.json")));
    }

    [Fact]
    public async Task LostBindingRecoversTheOwnerServerHeadBeforeUploadingTheNextVersion()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("恢复");
        note = await store.SaveAsync(note.NoteId, "恢复", "第一版");
        var firstBundle = await new ServerNoteMaterialBundleBuilder(store)
            .BuildAsync(note.NoteId, note.CurrentVersionId);
        var handler = new NoteCloudHandler(firstBundle);
        var api = new ApiClient(() => handler);
        api.SetAccessToken("synthetic-test-token");
        var service = new CloudNoteMaterialService(
            api,
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));
        var first = await service.UploadVersionAsync(
            note.NoteId,
            note.CurrentVersionId,
            userConfirmedStorage: true);

        File.Delete(Path.Combine(_root, "cloud-bindings.json"));
        note = await store.SaveAsync(note.NoteId, "恢复", "第二版");
        var secondBundle = await new ServerNoteMaterialBundleBuilder(store)
            .BuildAsync(note.NoteId, note.CurrentVersionId);
        handler.Reset(secondBundle, uploadedParts: false);
        var second = await service.UploadVersionAsync(
            note.NoteId,
            note.CurrentVersionId,
            userConfirmedStorage: true);

        Assert.Equal(first.Upload.VersionId, second.Upload.BaseVersionId);
        Assert.Equal(note.CurrentVersionId.ToString("D"), second.Upload.VersionId);
        Assert.Equal(note.CurrentVersionId, second.Binding.CurrentVersionId);
    }

    [Fact]
    public async Task ResultRevisionSourcesAndConflictOperationsUseTheTypedContract()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("结果");
        var noteId = note.NoteId.ToString("D");
        const string ArtifactId = "88888888-8888-8888-8888-888888888888";
        const string HeadVersionId = "99999999-9999-9999-9999-999999999999";
        const string ConflictVersionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var artifactJson = $$"""
            {
              "artifactId": "{{ArtifactId}}",
              "headVersionId": "{{HeadVersionId}}",
              "kind": "CompleteRecord",
              "noteId": "{{noteId}}",
              "revision": "artifact-head-revision",
              "versions": [
                {
                  "author": "Model",
                  "contentSha256": "{{new string('a', 64)}}",
                  "createdAt": "2026-11-01T01:02:03Z",
                  "generationTaskId": "66666666-6666-6666-6666-666666666666",
                  "hasConflict": false,
                  "isHead": true,
                  "sourceVersionId": "22222222-2222-2222-2222-222222222222",
                  "versionId": "{{HeadVersionId}}"
                },
                {
                  "author": "User",
                  "contentSha256": "{{new string('b', 64)}}",
                  "createdAt": "2026-11-01T01:03:03Z",
                  "hasConflict": true,
                  "isHead": false,
                  "parentVersionId": "{{HeadVersionId}}",
                  "sourceVersionId": "22222222-2222-2222-2222-222222222222",
                  "versionId": "{{ConflictVersionId}}"
                }
              ]
            }
            """;
        var versionJson = $$"""
            {
              "artifactId": "{{ArtifactId}}",
              "author": "User",
              "contentMarkdown": "# 并发修订",
              "contentSha256": "{{new string('b', 64)}}",
              "createdAt": "2026-11-01T01:03:03Z",
              "hasConflict": true,
              "inputSha256": "{{new string('c', 64)}}",
              "isHead": false,
              "kind": "CompleteRecord",
              "manifestSha256": "{{new string('d', 64)}}",
              "observedHeadVersionId": "{{HeadVersionId}}",
              "parentVersionId": "{{HeadVersionId}}",
              "planVersion": "plan-v1",
              "promptVersion": "prompt-v1",
              "sourceVersionId": "22222222-2222-2222-2222-222222222222",
              "versionId": "{{ConflictVersionId}}"
            }
            """;
        var mutationJson = $$"""
            {
              "artifact": {{artifactJson}},
              "hasConflict": true,
              "isHead": false,
              "versionId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
            }
            """;
        var sourceJson = $$"""
            {
              "items": [
                {
                  "createdAt": "2026-11-01T01:00:00Z",
                  "hasConflict": false,
                  "isCurrent": true,
                  "noteId": "{{noteId}}",
                  "recordings": 1,
                  "sampleRate": 16000,
                  "title": "结果",
                  "totalSamples": 32000,
                  "versionId": "22222222-2222-2222-2222-222222222222"
                },
                {
                  "createdAt": "2026-11-01T00:59:00Z",
                  "hasConflict": true,
                  "isCurrent": false,
                  "noteId": "{{noteId}}",
                  "recordings": 1,
                  "sampleRate": 16000,
                  "title": "结果旧版",
                  "totalSamples": 16000,
                  "versionId": "33333333-3333-3333-3333-333333333333"
                }
              ],
              "offset": 0,
              "total": 3
            }
            """;
        var sourceLastJson = $$"""
            {
              "items": [
                {
                  "createdAt": "2026-11-01T00:58:00Z",
                  "hasConflict": false,
                  "isCurrent": false,
                  "noteId": "{{noteId}}",
                  "recordings": 0,
                  "sampleRate": 16000,
                  "title": "无录音材料",
                  "totalSamples": 0,
                  "versionId": "44444444-4444-4444-4444-444444444444"
                }
              ],
              "offset": 2,
              "total": 3
            }
            """;
        var handler = new CloudResultHandler(
            artifactJson,
            versionJson,
            mutationJson,
            sourceJson,
            sourceLastJson);
        var api = new ApiClient(() => handler);
        api.SetAccessToken("synthetic-test-token");
        var service = new CloudNoteMaterialService(
            api,
            store,
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));

        var sources = await service.GetMaterialSourcesAsync(pageSize: 2);
        Assert.Equal(3, sources.Count);
        Assert.Equal([0, 2], handler.SourceOffsets);
        Assert.True(sources[1].HasConflict);

        var artifacts = await service.GetDerivedArtifactsAsync(note.NoteId);
        var artifact = Assert.Single(artifacts);
        Assert.Equal(2, artifact.Versions.Count);
        Assert.True(artifact.Versions[1].HasConflict);

        var version = await service.GetDerivedVersionAsync(
            Guid.Parse(ArtifactId),
            Guid.Parse(ConflictVersionId));
        Assert.True(version.HasConflict);
        Assert.Equal("# 并发修订", version.ContentMarkdown);

        var saved = await service.SaveManualRevisionAsync(
            Guid.Parse(ArtifactId),
            Guid.Parse(HeadVersionId),
            "artifact-head-revision",
            "# 人工修订");
        Assert.True(saved.HasConflict);
        Assert.False(saved.IsHead);
        Assert.Equal("# 人工修订", handler.LastManualRevisionContent);
        Assert.Equal(Guid.Parse(HeadVersionId), handler.LastExpectedHeadVersionId);

        var selected = await service.SelectDerivedVersionAsync(
            Guid.Parse(ArtifactId),
            Guid.Parse(ConflictVersionId),
            "artifact-head-revision");
        Assert.Equal(HeadVersionId, selected.HeadVersionId);
        Assert.Equal(ConflictVersionId, handler.LastSelectedVersionId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static byte[] SyntheticWave(int sampleCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + sampleCount * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(32_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(sampleCount * 2);
        for (var index = 0; index < sampleCount; index++)
        {
            writer.Write((short)(index % 100));
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<string> HashAsync(Stream stream)
        => Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();

    private sealed class NoteCloudHandler(ServerNoteMaterialBundle initialBundle) : HttpMessageHandler
    {
        private ServerNoteMaterialBundle _bundle = initialBundle;
        private bool _uploadedParts;
        private NoteMaterialUploadDto? _current;
        private string? _baseVersionId;
        private readonly List<string> _digests = [];

        public int PartUploads { get; private set; }
        public IReadOnlyList<string> PartDigests => _digests;
        public int PartResponseCode { get; set; }

        public void Reset(ServerNoteMaterialBundle bundle, bool uploadedParts)
        {
            _bundle = bundle;
            _uploadedParts = uploadedParts;
            PartUploads = 0;
            _digests.Clear();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Post
                && uri.AbsolutePath == "/api/v1/native-notes/versions")
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal(_bundle.InputJson, document.RootElement.GetProperty("inputJson").GetString());
                Assert.Equal(_bundle.ManifestJson, document.RootElement.GetProperty("manifestJson").GetString());
                Assert.True(document.RootElement.GetProperty("storageConsent").GetBoolean());
                _baseVersionId = document.RootElement.GetProperty("baseVersionId").GetString();
                _current = UploadDto(phase: "Uploading");
                return JsonResponse(new NoteMaterialUploadDtoApiResponse
                {
                    Code = 0,
                    Data = _current,
                    Message = "ok",
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }

            if (request.Method == HttpMethod.Put && uri.AbsolutePath.Contains("/parts/", StringComparison.Ordinal))
            {
                var digest = QueryValue(uri.Query, "sha256");
                var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Assert.Equal(digest, Hash(body));
                _digests.Add(digest);
                PartUploads++;
                return JsonResponse(new StringApiResponse
                {
                    Code = PartResponseCode,
                    Data = digest,
                    Message = PartResponseCode == 0 ? "ok" : "分片未被服务器接受",
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }

            if (request.Method == HttpMethod.Post && uri.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal))
            {
                _uploadedParts = true;
                _current = UploadDto(phase: "Uploaded");
                return JsonResponse(new NoteMaterialUploadDtoApiResponse
                {
                    Code = 0,
                    Data = _current,
                    Message = "ok",
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }

            if (request.Method == HttpMethod.Get && uri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
            {
                return JsonResponse(new NoteMaterialUploadDtoIReadOnlyListApiResponse
                {
                    Code = 0,
                    Data = _current is null ? [] : [_current],
                    Message = "ok",
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private NoteMaterialUploadDto UploadDto(string phase)
        {
            return new NoteMaterialUploadDto
            {
                BaseVersionId = _baseVersionId,
                CreatedAt = DateTimeOffset.UtcNow,
                CurrentVersionId = phase == "Uploaded" ? _bundle.VersionId.ToString("D") : null,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Files = _bundle.Files.Select((file, index) => new NoteMaterialUploadFileDto
                {
                    Index = index,
                    Kind = file.Kind,
                    PartCount = 1,
                    Path = file.Path,
                    Sha256 = file.Sha256,
                    SizeBytes = file.SizeBytes,
                    UploadedParts = _uploadedParts ? [0] : [],
                }).ToList(),
                HasConflict = false,
                IsCurrent = phase == "Uploaded",
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
                NoteId = _bundle.NoteId.ToString("D"),
                Phase = phase,
                VersionId = _bundle.VersionId.ToString("D"),
            };
        }

        private static string QueryValue(string query, string name)
        {
            foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = item.Split('=', 2);
                if (parts.Length == 2
                    && string.Equals(Uri.UnescapeDataString(parts[0]), name, StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }

            throw new InvalidOperationException($"缺少 query {name}");
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

    private sealed class CloudResultHandler(
        string artifactJson,
        string versionJson,
        string mutationJson,
        string firstSourcesJson,
        string lastSourcesJson) : HttpMessageHandler
    {
        private readonly List<int> _sourceOffsets = [];

        public IReadOnlyList<int> SourceOffsets => _sourceOffsets;
        public string? LastManualRevisionContent { get; private set; }
        public Guid? LastExpectedHeadVersionId { get; private set; }
        public string? LastSelectedVersionId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Get
                && uri.AbsolutePath == "/api/v1/native-notes/asr-tasks/sources")
            {
                var offset = int.Parse(QueryValue(uri.Query, "offset"), System.Globalization.CultureInfo.InvariantCulture);
                _sourceOffsets.Add(offset);
                Assert.Equal("2", QueryValue(uri.Query, "limit"));
                return RawJson(offset == 0 ? firstSourcesJson : lastSourcesJson);
            }

            if (request.Method == HttpMethod.Get
                && uri.AbsolutePath == "/api/v1/native-notes/derived-artifacts")
            {
                Assert.Contains("noteId=", uri.Query, StringComparison.Ordinal);
                return RawJson($"[{artifactJson}]");
            }

            if (request.Method == HttpMethod.Get
                && uri.AbsolutePath.EndsWith(
                    "/derived-artifacts/88888888-8888-8888-8888-888888888888/versions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    StringComparison.Ordinal))
            {
                return RawJson(versionJson);
            }

            if (request.Method == HttpMethod.Post
                && uri.AbsolutePath.EndsWith("/manual-revisions", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                LastManualRevisionContent =
                    document.RootElement.GetProperty("contentMarkdown").GetString();
                LastExpectedHeadVersionId = Guid.Parse(
                    document.RootElement.GetProperty("expectedHeadVersionId").GetString()!);
                Assert.Equal("artifact-head-revision",
                    document.RootElement.GetProperty("expectedHeadRevision").GetString());
                Assert.NotEqual(Guid.Empty, Guid.Parse(
                    document.RootElement.GetProperty("revisionId").GetString()!));
                return RawJson(mutationJson);
            }

            if (request.Method == HttpMethod.Post
                && uri.AbsolutePath.EndsWith("/select", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                LastSelectedVersionId =
                    document.RootElement.GetProperty("versionId").GetString();
                Assert.Equal("artifact-head-revision",
                    document.RootElement.GetProperty("expectedHeadRevision").GetString());
                return RawJson(artifactJson);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage RawJson(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"code":0,"data":{{json}},"message":"ok","timestamp":"2026-11-01T00:00:00Z"}""",
                    Encoding.UTF8,
                    "application/json"),
            };

        private static string QueryValue(string query, string name)
        {
            foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = item.Split('=', 2);
                if (parts.Length == 2
                    && string.Equals(Uri.UnescapeDataString(parts[0]), name, StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }

            throw new InvalidOperationException($"缺少 query {name}");
        }
    }
}

internal static class ServerBundleTestExtensions
{
    public static string OpenReadText(this ServerNoteMaterialFile file)
    {
        using var stream = file.OpenRead();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
