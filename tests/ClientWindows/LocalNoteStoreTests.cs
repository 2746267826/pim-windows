using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class LocalNoteStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-local-note-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateSaveSearchAndImmutableVersionsRoundTrip()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("课堂记录");
        var saved = await store.SaveAsync(
            note.NoteId,
            "课堂记录",
            "# 第一课\n真实信号与材料。");

        var photoBytes = Encoding.UTF8.GetBytes("synthetic-photo-bytes");
        var withPhoto = await store.AddMaterialsAsync(note.NoteId,
        [
            new LocalNoteMaterialInput(
                new MemoryStream(photoBytes),
                "board.png",
                LocalNoteMaterialKind.Photo,
                "image/png"),
        ]);
        var final = await store.SaveAsync(note.NoteId, "课堂记录（修订）", "# 第一课\n正文已修订。");

        var results = await store.ListAsync("修订");
        Assert.Single(results);
        Assert.Equal(final.NoteId, results[0].NoteId);
        Assert.Equal(1, results[0].MaterialCount);
        Assert.Empty(await store.ListAsync("不存在"));

        var firstVersion = await store.LoadVersionAsync(note.NoteId, saved.CurrentVersionId);
        Assert.Equal("# 第一课\n真实信号与材料。", firstVersion.Markdown);
        Assert.Empty(firstVersion.MaterialIds);

        var photoVersion = await store.LoadVersionAsync(note.NoteId, withPhoto.CurrentVersionId);
        Assert.Single(photoVersion.MaterialIds);
        Assert.Equal("# 第一课\n真实信号与材料。", photoVersion.Markdown);

        var loaded = await store.LoadAsync(note.NoteId);
        Assert.Equal("课堂记录（修订）", loaded.Title);
        Assert.Equal(final.CurrentVersionId, loaded.CurrentVersionId);
        Assert.Single(loaded.Materials);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(photoBytes)).ToLowerInvariant(),
            loaded.Materials[0].ContentSha256);
    }

    [Fact]
    public async Task ContentAddressedMaterialIsSharedWithoutChangingOldSnapshot()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync();
        var bytes = Encoding.UTF8.GetBytes("same-photo");

        var first = await store.AddMaterialsAsync(note.NoteId,
        [
            new LocalNoteMaterialInput(new MemoryStream(bytes), "a.png", LocalNoteMaterialKind.Photo),
            new LocalNoteMaterialInput(new MemoryStream(bytes), "b.png", LocalNoteMaterialKind.Photo),
        ]);

        Assert.Equal(2, first.Materials.Count);
        Assert.Equal(first.Materials[0].ContentSha256, first.Materials[1].ContentSha256);
        Assert.Equal(first.Materials[0].RelativePath, first.Materials[1].RelativePath);
        var version = await store.LoadVersionAsync(note.NoteId, first.CurrentVersionId);
        Assert.Equal(2, version.MaterialIds.Count);
    }

    [Fact]
    public async Task HandwritingAndRecordingPreserveOriginalBytesAndPackagedManifest()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("混合材料");
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var ink = """{"strokes":[{"points":[[1,2,0.5],[3,4,0.7]]}]}""";
        var withInk = await store.AddHandwritingAsync(
            note.NoteId,
            new MemoryStream(pngBytes),
            ink,
            "手写 1",
            320,
            180);

        var wavBytes = CreateSyntheticWave(sampleCount: 1600);
        var withRecording = await store.AddMaterialsAsync(note.NoteId,
        [
            new LocalNoteMaterialInput(
                new MemoryStream(wavBytes),
                "录音 1.wav",
                LocalNoteMaterialKind.Recording,
                "audio/wav",
                DurationMilliseconds: 100),
        ]);

        Assert.Equal(3, withRecording.Materials.Count);
        var handwritingPng = withInk.Materials.Single(material => material.Kind == LocalNoteMaterialKind.Handwriting);
        var handwritingSource = withInk.Materials.Single(material => material.Kind == LocalNoteMaterialKind.HandwritingSource);
        Assert.Equal(pngBytes.Length, handwritingPng.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(pngBytes)).ToLowerInvariant(), handwritingPng.ContentSha256);
        Assert.Equal("application/vnd.pim.ink+json", handwritingSource.MediaType);

        var exportPath = Path.Combine(_root, "exports", "bundle.zip");
        var exported = await store.ExportAsync(note.NoteId, withRecording.CurrentVersionId, exportPath);
        Assert.True(File.Exists(exportPath));
        Assert.Equal(3, exported.Materials.Count);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(exportPath))).ToLowerInvariant(), exported.Sha256);

        using var archive = ZipFile.OpenRead(exportPath);
        Assert.NotNull(archive.GetEntry("note.md"));
        Assert.NotNull(archive.GetEntry("version.json"));
        Assert.NotNull(archive.GetEntry("bundle-manifest.json"));
        Assert.Equal(3, archive.Entries.Count(entry => entry.FullName.StartsWith("materials/", StringComparison.Ordinal)));
        var recordingEntry = archive.Entries.Single(entry => entry.FullName.Contains("录音 1", StringComparison.Ordinal));
        await using var recordingStream = recordingEntry.Open();
        using var memory = new MemoryStream();
        await recordingStream.CopyToAsync(memory);
        Assert.Equal(wavBytes, memory.ToArray());
    }

    [Fact]
    public async Task MissingMaterialFailsExportInsteadOfDroppingData()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync();
        var withPhoto = await store.AddMaterialsAsync(note.NoteId,
        [
            new LocalNoteMaterialInput(
                new MemoryStream(Encoding.UTF8.GetBytes("photo")),
                "a.png",
                LocalNoteMaterialKind.Photo),
        ]);
        var materialPath = Path.Combine(_root, withPhoto.Materials.Single().RelativePath);
        File.Delete(materialPath);

        await Assert.ThrowsAsync<LocalNoteStoreException>(() => store.ExportAsync(
            note.NoteId,
            withPhoto.CurrentVersionId,
            Path.Combine(_root, "exports", "broken.zip")));
    }

    [Fact]
    public async Task HistoricalVersionExportAndPackageImportPreserveFixedTextAndMaterials()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("原始标题");
        var photoBytes = Encoding.UTF8.GetBytes("historical-photo");
        var withPhoto = await store.AddMaterialsAsync(note.NoteId,
        [
            new LocalNoteMaterialInput(
                new MemoryStream(photoBytes),
                "history.png",
                LocalNoteMaterialKind.Photo,
                "image/png",
                Width: 1280,
                Height: 720),
        ]);
        var historical = await store.SaveAsync(note.NoteId, "历史标题", "历史固定正文");
        await store.SaveAsync(note.NoteId, "当前标题", "当前正文");

        var exportPath = Path.Combine(_root, "exports", "historical.pimnote.zip");
        var exported = await store.ExportAsync(note.NoteId, historical.CurrentVersionId, exportPath);
        using (var archive = ZipFile.OpenRead(exportPath))
        using (var reader = new StreamReader(archive.GetEntry("note.md")!.Open(), Encoding.UTF8))
        {
            Assert.Equal("历史固定正文", await reader.ReadToEndAsync());
        }

        Assert.Equal(historical.CurrentVersionId, exported.VersionId);
        var importRoot = Path.Combine(_root, "imported-library");
        var imported = await new LocalNoteStore(importRoot).ImportAsync(exportPath);

        Assert.Equal("历史标题", imported.Title);
        Assert.Single(imported.Materials);
        Assert.Equal(1280, imported.Materials[0].Width);
        Assert.Equal(720, imported.Materials[0].Height);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(photoBytes)).ToLowerInvariant(),
            imported.Materials[0].ContentSha256);
        var importedStore = new LocalNoteStore(importRoot);
        var importedNote = await importedStore.LoadAsync(imported.NoteId);
        Assert.Equal("历史固定正文", importedNote.Markdown);
        var importedVersion = await importedStore.LoadVersionAsync(
            imported.NoteId,
            importedNote.CurrentVersionId);
        Assert.Equal(LocalNoteCheckpointReason.ImportedPackage, importedVersion.Reason);
        Assert.Single(importedVersion.MaterialIds);

        var sourceVersionId = importedNote.CurrentVersionId;
        var restored = await importedStore.RestoreVersionAsync(
            imported.NoteId,
            sourceVersionId);
        Assert.Equal("历史固定正文", restored.Markdown);
        Assert.NotEqual(sourceVersionId, restored.CurrentVersionId);
        Assert.Equal(
            "历史固定正文",
            (await importedStore.LoadVersionAsync(imported.NoteId, sourceVersionId)).Markdown);
    }

    [Fact]
    public async Task RestoringEarlierVersionKeepsLaterVersionMaterialsExportable()
    {
        var store = new LocalNoteStore(_root);
        var initial = await store.CreateAsync("版本材料");
        var withPhoto = await store.AddMaterialsAsync(initial.NoteId,
        [
            new LocalNoteMaterialInput(
                new MemoryStream(Encoding.UTF8.GetBytes("fixed-photo")),
                "fixed.png",
                LocalNoteMaterialKind.Photo,
                "image/png"),
        ]);

        var restored = await store.RestoreVersionAsync(initial.NoteId, initial.CurrentVersionId);
        Assert.Empty(restored.Materials);

        var generatedVersionPath = Path.Combine(_root, "exports", "later-version.pimnote.zip");
        var exported = await store.ExportAsync(initial.NoteId, withPhoto.CurrentVersionId, generatedVersionPath);
        Assert.Equal(withPhoto.CurrentVersionId, exported.VersionId);
        Assert.Single(exported.Materials);

        using var archive = ZipFile.OpenRead(generatedVersionPath);
        Assert.Single(archive.Entries, entry =>
            entry.FullName.StartsWith("materials/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportRejectsTamperedMaterialWithoutCreatingPartialNote()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("防篡改");
        var withPhoto = await store.AddMaterialsAsync(note.NoteId,
        [
            new LocalNoteMaterialInput(
                new MemoryStream(Encoding.UTF8.GetBytes("original")),
                "photo.png",
                LocalNoteMaterialKind.Photo,
                "image/png"),
        ]);
        var exportPath = Path.Combine(_root, "exports", "tampered.pimnote.zip");
        await store.ExportAsync(note.NoteId, withPhoto.CurrentVersionId, exportPath);

        using (var archive = ZipFile.Open(exportPath, ZipArchiveMode.Update))
        {
            var material = archive.Entries.Single(entry =>
                entry.FullName.StartsWith("materials/", StringComparison.Ordinal));
            material.Delete();
            var replacement = archive.CreateEntry(material.FullName);
            await using var stream = replacement.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("tampered"));
        }

        var importRoot = Path.Combine(_root, "tamper-target");
        var importStore = new LocalNoteStore(importRoot);
        await Assert.ThrowsAsync<LocalNoteStoreException>(() => importStore.ImportAsync(exportPath));
        Assert.Empty(await importStore.ListAsync());
    }

    [Fact]
    public async Task EmptyMaterialAndUnknownNoteFailClearly()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync();

        await Assert.ThrowsAsync<LocalNoteStoreException>(() => store.AddMaterialsAsync(note.NoteId,
        [
            new LocalNoteMaterialInput(
                new MemoryStream(),
                "empty.png",
                LocalNoteMaterialKind.Photo),
        ]));
        await Assert.ThrowsAsync<LocalNoteStoreException>(() => store.LoadAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task CorruptedFixedVersionIsRejectedInsteadOfLoadedSilently()
    {
        var store = new LocalNoteStore(_root);
        var note = await store.CreateAsync("完整性");
        var versionPath = Path.Combine(
            _root,
            "notes",
            note.NoteId.ToString("N"),
            "versions",
            note.CurrentVersionId.ToString("N") + ".json");
        var version = JsonNode.Parse(await File.ReadAllTextAsync(versionPath))!;
        version["documentSha256"] = new string('0', 64);
        await File.WriteAllTextAsync(versionPath, version.ToJsonString());

        await Assert.ThrowsAsync<LocalNoteStoreException>(
            () => store.LoadVersionAsync(note.NoteId, note.CurrentVersionId));
        await Assert.ThrowsAsync<LocalNoteStoreException>(
            () => store.LoadAsync(note.NoteId));
    }

    [Fact]
    public void WaveWriterFinalizesHeaderDurationAndPeakFromActualPcm()
    {
        var path = Path.Combine(_root, "recording.wav");
        using var writer = new WavePcmWriter(path, sampleRate: 16_000, channels: 1, bitsPerSample: 16);
        var samples = new short[] { 0, 8192, -16384, 32767 };
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        writer.Append(bytes);
        writer.Append([1]);
        var result = writer.Complete();

        Assert.Equal(8, result.DataBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(0.25), result.Duration);
        Assert.Equal(1, result.PeakLevel);
        var wave = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal(wave.Length - 8, BitConverter.ToInt32(wave, 4));
        Assert.Equal(8, BitConverter.ToInt32(wave, 40));
        Assert.Equal(bytes, wave[44..]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static byte[] CreateSyntheticWave(int sampleCount)
    {
        const int sampleRate = 16000;
        const short channels = 1;
        const short bits = 16;
        var dataLength = sampleCount * channels * bits / 8;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bits / 8);
        writer.Write((short)(channels * bits / 8));
        writer.Write(bits);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = (short)(Math.Sin(index * 2 * Math.PI * 440 / sampleRate) * 8000);
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
