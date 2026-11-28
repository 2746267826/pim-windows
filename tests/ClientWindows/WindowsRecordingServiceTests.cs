using System.IO;
using Pim.Client.App;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class WindowsRecordingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-recording-service-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingInputDeviceFailsClearlyWithoutStayingActive()
    {
        var store = new LocalNoteStore(Path.Combine(_root, "notes"));
        var note = await store.CreateAsync("空设备");
        var service = new WindowsRecordingService(
            store,
            new FakeCaptureFactory(() => new ThrowingCapture()),
            Path.Combine(_root, "recordings"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(note.NoteId));

        Assert.Contains("未检测到录音输入设备", exception.Message);
        Assert.False(service.IsRecording);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "recordings")));
    }

    [Fact]
    public async Task SyntheticCapturePersistsOriginalPcmAsNoteMaterial()
    {
        var store = new LocalNoteStore(Path.Combine(_root, "notes"));
        var note = await store.CreateAsync("合成录音");
        var capture = new SyntheticCapture();
        var service = new WindowsRecordingService(
            store,
            new FakeCaptureFactory(() => capture),
            Path.Combine(_root, "recordings"));

        await service.StartAsync(note.NoteId);
        Assert.True(service.IsRecording);
        capture.Emit([0, 0, 0, 32, 0, 128, 0, 0]);

        var result = await service.StopAsync();

        Assert.NotNull(result);
        Assert.Equal(note.NoteId, result.NoteId);
        Assert.Equal(8, result.Wave.DataBytes);
        Assert.True(result.Wave.PeakLevel > 0.49);
        var material = Assert.Single(result.Note.Materials);
        Assert.Equal(LocalNoteMaterialKind.Recording, material.Kind);
        Assert.Equal("audio/wav", material.MediaType);
        Assert.NotNull(material.RecordingId);
        Assert.Equal(1, material.RecordingSegment);
        Assert.Equal(1, material.RecordingChunkIndex);
        Assert.Equal(52, material.Length);
        Assert.False(service.IsRecording);
        Assert.False(File.Exists(result.SourcePath));
    }

    [Fact]
    public async Task NotificationAndStateHoldNoSessionIdsOrPaths()
    {
        var store = new LocalNoteStore(Path.Combine(_root, "notes"));
        var note = await store.CreateAsync("隐私");
        var service = new WindowsRecordingService(
            store,
            new FakeCaptureFactory(() => new SyntheticCapture()),
            Path.Combine(_root, "recordings"));
        var states = new List<WindowsRecordingState>();
        service.StateChanged += (_, state) => states.Add(state);

        await service.StartAsync(note.NoteId);
        Assert.True(service.Suspend());
        service.Resume();
        await service.StopAsync();

        var text = string.Join("\n", states.Select(state =>
            $"{state.NoteId}|{state.RecordingId}|{state.SegmentNumber}|{state.Error}"));
        Assert.DoesNotContain(_root, text, StringComparison.Ordinal);
        Assert.DoesNotContain(".wav", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuspendKeepsRecordingBoundAndResumeContinuesSameSegment()
    {
        var store = new LocalNoteStore(Path.Combine(_root, "notes"));
        var note = await store.CreateAsync("睡眠恢复");
        var captures = new SyntheticCaptureFactory();
        var service = new WindowsRecordingService(
            store,
            captures,
            Path.Combine(_root, "recordings"));

        await service.StartAsync(note.NoteId);
        var first = captures.Last;
        first.Emit([0, 0, 0, 32]);

        Assert.True(service.Suspend());
        Assert.True(service.IsRecording);
        Assert.Equal(note.NoteId, service.CurrentNoteId);
        Assert.Contains("睡眠", service.CurrentState.Error);

        service.Resume();

        Assert.Equal(2, captures.CreatedCount);
        var resumed = captures.Last;
        resumed.Emit([0, 0, 0, 64, 0, 128, 0, 0]);
        var result = await service.StopAsync();

        Assert.NotNull(result);
        Assert.Equal(12, result.Wave.DataBytes);
        Assert.Single(result.Note.Materials);
        Assert.Equal(1, result.Note.Materials[0].RecordingSegment);
        Assert.False(service.IsRecording);
    }

    [Fact]
    public async Task ResumeFailureKeepsCapturedAudioAndStopsDeterministically()
    {
        var store = new LocalNoteStore(Path.Combine(_root, "notes"));
        var note = await store.CreateAsync("恢复失败");
        var captures = new SyntheticCaptureFactory(failAfter: 1);
        var service = new WindowsRecordingService(
            store,
            captures,
            Path.Combine(_root, "recordings"));

        await service.StartAsync(note.NoteId);
        captures.Last.Emit([0, 0, 0, 32]);
        Assert.True(service.Suspend());

        service.Resume();

        Assert.True(service.IsRecording);
        Assert.Contains("唤醒后无法继续录音", service.CurrentState.Error);
        var result = await service.StopAsync();

        Assert.NotNull(result);
        Assert.Equal(4, result.Wave.DataBytes);
        Assert.Single(result.Note.Materials);
    }

    [Fact]
    public async Task FinishThenRecordAgainKeepsOldSegmentAndAddsNewSegmentToSameNote()
    {
        var store = new LocalNoteStore(Path.Combine(_root, "notes"));
        var note = await store.CreateAsync("补录");
        var capture = new SyntheticCapture();
        var service = new WindowsRecordingService(
            store,
            new FakeCaptureFactory(() => capture),
            Path.Combine(_root, "recordings"));

        await service.StartAsync(note.NoteId);
        capture.Emit([0, 0, 0, 32]);
        var first = await service.StopAsync();
        Assert.NotNull(first);
        var firstMaterialId = Assert.Single(first.Note.Materials).MaterialId;
        var finished = await store.FinalizeAsync(note.NoteId, first.Note.Title, first.Note.Markdown);

        await service.StartAsync(note.NoteId);
        Assert.Equal(2, service.CurrentState.SegmentNumber);
        capture.Emit([0, 0, 0, 64]);
        var second = await service.StopAsync();

        Assert.NotNull(second);
        Assert.Equal(2, second.Note.Materials.Count);
        Assert.Contains(second.Note.Materials, material => material.MaterialId == firstMaterialId);
        var finishedAgain = await store.FinalizeAsync(note.NoteId, second.Note.Title, second.Note.Markdown);
        var version = await store.LoadVersionAsync(note.NoteId, finishedAgain.CurrentVersionId);
        Assert.Equal(LocalNoteCheckpointReason.Finished, version.Reason);
        Assert.Equal(
            second.Note.Materials.Select(material => material.MaterialId),
            version.MaterialIds);
        Assert.NotEqual(finished.CurrentVersionId, finishedAgain.CurrentVersionId);
    }

    [Fact]
    public async Task StopSavesToStartBoundNoteEvenWhenAnotherNoteIsActive()
    {
        var store = new LocalNoteStore(Path.Combine(_root, "notes"));
        var first = await store.CreateAsync("第一页");
        var second = await store.CreateAsync("第二页");
        var capture = new SyntheticCapture();
        var service = new WindowsRecordingService(
            store,
            new FakeCaptureFactory(() => capture),
            Path.Combine(_root, "recordings"));

        await service.StartAsync(first.NoteId);
        capture.Emit([0, 0, 0, 32]);
        var result = await service.StopAsync();

        Assert.NotNull(result);
        Assert.True(result.IsBoundToAnotherNote(second.NoteId));
        Assert.Contains(result.Note.Materials, material => material.Kind == LocalNoteMaterialKind.Recording);
        Assert.All(
            (await store.LoadAsync(second.NoteId)).Materials,
            material => Assert.NotEqual(LocalNoteMaterialKind.Recording, material.Kind));
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

    private sealed class FakeCaptureFactory(Func<IAudioCaptureBackend> create)
        : IAudioCaptureBackendFactory
    {
        public IAudioCaptureBackend Create(int sampleRate, short channels, short bitsPerSample) => create();
    }

    private sealed class SyntheticCaptureFactory(int? failAfter = null)
        : IAudioCaptureBackendFactory
    {
        public int CreatedCount { get; private set; }

        public SyntheticCapture Last { get; private set; } = null!;

        public IAudioCaptureBackend Create(int sampleRate, short channels, short bitsPerSample)
        {
            CreatedCount++;
            if (failAfter is { } threshold && CreatedCount > threshold)
            {
                throw new InvalidOperationException("唤醒后没有可用的录音设备。");
            }

            Last = new SyntheticCapture();
            return Last;
        }
    }

    private sealed class ThrowingCapture : IAudioCaptureBackend
    {
        public event Action<byte[]>? DataAvailable
        {
            add { }
            remove { }
        }

        public void Start() => throw new InvalidOperationException(
            "未检测到录音输入设备。请连接麦克风并检查 Windows 隐私权限。");

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class SyntheticCapture : IAudioCaptureBackend
    {
        public event Action<byte[]>? DataAvailable;

        public void Start()
        {
        }

        public void Emit(byte[] pcm) => DataAvailable?.Invoke(pcm);

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
