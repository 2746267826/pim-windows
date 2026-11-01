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
        Assert.Equal(52, material.Length);
        Assert.False(service.IsRecording);
        Assert.False(File.Exists(result.SourcePath));
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
