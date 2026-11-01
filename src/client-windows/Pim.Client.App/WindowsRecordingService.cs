using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

public sealed record WindowsRecordingState(
    bool IsRecording,
    Guid? NoteId,
    TimeSpan Elapsed,
    double PeakLevel,
    string? Error);

public sealed record WindowsRecordingResult(
    Guid NoteId,
    string SourcePath,
    WavePcmResult Wave,
    LocalNoteDocument Note);

public interface IAudioCaptureBackend : IDisposable
{
    event Action<byte[]>? DataAvailable;

    void Start();

    void Stop();
}

public interface IAudioCaptureBackendFactory
{
    IAudioCaptureBackend Create(int sampleRate, short channels, short bitsPerSample);
}

public sealed class WindowsRecordingService : IAsyncDisposable
{
    private const int SampleRate = 16_000;
    private const short Channels = 1;
    private const short BitsPerSample = 16;
    private readonly LocalNoteStore _store;
    private readonly IAudioCaptureBackendFactory _captureFactory;
    private readonly string _recordingsRoot;
    private readonly object _sync = new();
    private readonly Stopwatch _elapsed = new();
    private IAudioCaptureBackend? _capture;
    private WavePcmWriter? _writer;
    private Guid? _noteId;
    private bool _stopping;
    private string? _lastError;

    public WindowsRecordingService(LocalNoteStore store)
        : this(
            store,
            new WinMmAudioCaptureFactory(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PIM",
                "Recordings"))
    {
    }

    internal WindowsRecordingService(
        LocalNoteStore store,
        IAudioCaptureBackendFactory captureFactory,
        string recordingsRoot)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _recordingsRoot = Path.GetFullPath(recordingsRoot);
    }

    public event EventHandler<WindowsRecordingState>? StateChanged;

    public bool IsRecording
    {
        get
        {
            lock (_sync)
            {
                return _capture is not null;
            }
        }
    }

    public Guid? CurrentNoteId
    {
        get
        {
            lock (_sync)
            {
                return _noteId;
            }
        }
    }

    public async Task StartAsync(Guid noteId, CancellationToken cancellationToken = default)
    {
        if (noteId == Guid.Empty)
        {
            throw new ArgumentException("录音必须绑定到一篇本机笔记。", nameof(noteId));
        }

        lock (_sync)
        {
            if (_capture is not null)
            {
                throw new InvalidOperationException("已有录音正在进行；没有启动第二个采集设备。");
            }
        }

        await _store.LoadAsync(noteId, cancellationToken);
        Directory.CreateDirectory(_recordingsRoot);
        var path = Path.Combine(
            _recordingsRoot,
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{noteId:N}-{Guid.NewGuid():N}.wav");
        var writer = new WavePcmWriter(path, SampleRate, Channels, BitsPerSample);
        IAudioCaptureBackend? capture = null;
        try
        {
            capture = _captureFactory.Create(SampleRate, Channels, BitsPerSample);
            capture.DataAvailable += data =>
            {
                writer.Append(data);
                PublishState();
            };
            capture.Start();
            lock (_sync)
            {
                _writer = writer;
                _capture = capture;
                _noteId = noteId;
                _stopping = false;
                _lastError = null;
                _elapsed.Restart();
            }

            PublishState();
        }
        catch
        {
            capture?.Dispose();
            writer.Dispose();
            try
            {
                File.Delete(path);
            }
            catch
            {
            }

            throw;
        }
    }

    public async Task<WindowsRecordingResult?> StopAsync(CancellationToken cancellationToken = default)
    {
        IAudioCaptureBackend? capture;
        WavePcmWriter? writer;
        Guid noteId;
        lock (_sync)
        {
            if (_capture is null || _writer is null || _noteId is null)
            {
                return null;
            }

            if (_stopping)
            {
                throw new InvalidOperationException("录音正在停止。");
            }

            _stopping = true;
            capture = _capture;
            writer = _writer;
            noteId = _noteId.Value;
            _elapsed.Stop();
        }

        try
        {
            Exception? captureFailure = null;
            try
            {
                capture.Stop();
            }
            catch (Exception ex)
            {
                captureFailure = ex;
            }
            finally
            {
                capture.Dispose();
            }

            var wave = writer.Complete();
            writer.Dispose();
            if (wave.DataBytes == 0)
            {
                ResetState();
                TryDelete(writer.Path);
                if (captureFailure is not null)
                {
                    throw new IOException("录音采集失败，且没有收到可用音频。", captureFailure);
                }

                return null;
            }

            LocalNoteDocument note;
            await using (var source = new FileStream(
                writer.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                note = await _store.AddMaterialsAsync(noteId,
                [
                    new LocalNoteMaterialInput(
                        source,
                        Path.GetFileName(writer.Path),
                        LocalNoteMaterialKind.Recording,
                        "audio/wav",
                        DurationMilliseconds: (long)wave.Duration.TotalMilliseconds),
                ], cancellationToken);
            }

            var result = new WindowsRecordingResult(noteId, writer.Path, wave, note);
            TryDelete(writer.Path);
            ResetState();
            if (captureFailure is not null)
            {
                lock (_sync)
                {
                    _lastError = "录音文件已保存，但采集设备在停止时报告异常。";
                }

                PublishState();
            }

            return result;
        }
        catch (Exception ex)
        {
            try
            {
                writer.Dispose();
            }
            catch
            {
            }

            lock (_sync)
            {
                _lastError = "录音停止后未能写入本机笔记库；原始 WAV 已保留。";
            }

            ResetState(keepError: true);
            PublishState();
            throw new IOException(
                $"{_lastError}\n原始文件：{writer.Path}\n{ex.Message}",
                ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRecording)
        {
            try
            {
                await StopAsync();
            }
            catch
            {
            }
        }
    }

    private void ResetState(bool keepError = false)
    {
        lock (_sync)
        {
            _capture = null;
            _writer = null;
            _noteId = null;
            _stopping = false;
            _elapsed.Reset();
            if (!keepError)
            {
                _lastError = null;
            }
        }
    }

    private void PublishState()
    {
        WindowsRecordingState state;
        lock (_sync)
        {
            state = new WindowsRecordingState(
                _capture is not null,
                _noteId,
                _elapsed.Elapsed,
                _writer?.PeakLevel ?? 0,
                _lastError);
        }

        StateChanged?.Invoke(this, state);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}

internal sealed class WinMmAudioCaptureFactory : IAudioCaptureBackendFactory
{
    public IAudioCaptureBackend Create(int sampleRate, short channels, short bitsPerSample) =>
        new WaveInAudioCapture(sampleRate, channels, bitsPerSample);
}

internal sealed class WaveInAudioCapture : IAudioCaptureBackend
{
    private const int WaveMapper = -1;
    private const int WaveFormatPcm = 1;
    private const int CallbackFunction = 0x00030000;
    private const int MmSysErrNoError = 0;
    private const int WhdrDone = 0x00000001;
    private const int BufferCount = 4;

    private readonly object _sync = new();
    private readonly int _sampleRate;
    private readonly short _channels;
    private readonly short _bitsPerSample;
    private readonly List<BufferState> _buffers = [];
    private readonly WaveInCallback _callback;
    private IntPtr _handle;
    private bool _started;
    private bool _stopping;
    private Exception? _callbackError;

    public WaveInAudioCapture(int sampleRate, short channels, short bitsPerSample)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _bitsPerSample = bitsPerSample;
        _callback = OnWaveIn;
    }

    public event Action<byte[]>? DataAvailable;

    public void Start()
    {
        if (waveInGetNumDevs() == 0)
        {
            throw new InvalidOperationException("未检测到录音输入设备。请连接麦克风并检查 Windows 隐私权限。");
        }

        var format = new WaveFormatEx
        {
            wFormatTag = WaveFormatPcm,
            nChannels = _channels,
            nSamplesPerSec = _sampleRate,
            nAvgBytesPerSec = _sampleRate * _channels * _bitsPerSample / 8,
            nBlockAlign = (short)(_channels * _bitsPerSample / 8),
            wBitsPerSample = _bitsPerSample,
            cbSize = 0,
        };
        var callbackPointer = Marshal.GetFunctionPointerForDelegate(_callback);
        var openResult = waveInOpen(
            out _handle,
            WaveMapper,
            ref format,
            callbackPointer,
            IntPtr.Zero,
            CallbackFunction);
        if (openResult != MmSysErrNoError)
        {
            throw new InvalidOperationException($"无法打开录音输入设备（WinMM {openResult}）。");
        }

        try
        {
            var bytesPerBuffer = _sampleRate * _channels * _bitsPerSample / 8 / 4;
            for (var index = 0; index < BufferCount; index++)
            {
                var data = Marshal.AllocHGlobal(bytesPerBuffer);
                var header = new WaveHeader
                {
                    lpData = data,
                    dwBufferLength = (uint)bytesPerBuffer,
                };
                var headerPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                Marshal.StructureToPtr(header, headerPointer, fDeleteOld: false);
                var buffer = new BufferState(data, headerPointer, bytesPerBuffer);
                _buffers.Add(buffer);
                var prepareResult = waveInPrepareHeader(_handle, headerPointer, (uint)Marshal.SizeOf<WaveHeader>());
                if (prepareResult != MmSysErrNoError)
                {
                    throw new InvalidOperationException($"无法准备录音缓冲区（WinMM {prepareResult}）。");
                }

                var addResult = waveInAddBuffer(_handle, headerPointer, (uint)Marshal.SizeOf<WaveHeader>());
                if (addResult != MmSysErrNoError)
                {
                    throw new InvalidOperationException($"无法投递录音缓冲区（WinMM {addResult}）。");
                }
            }

            var startResult = waveInStart(_handle);
            if (startResult != MmSysErrNoError)
            {
                throw new InvalidOperationException($"无法启动录音输入（WinMM {startResult}）。");
            }

            _started = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Stop()
    {
        IntPtr handle;
        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            _stopping = true;
            handle = _handle;
        }

        if (handle != IntPtr.Zero)
        {
            waveInStop(handle);
            waveInReset(handle);
        }

        if (_callbackError is not null)
        {
            throw new InvalidOperationException("录音采集线程失败。", _callbackError);
        }
    }

    public void Dispose()
    {
        IntPtr handle;
        bool started;
        lock (_sync)
        {
            _stopping = true;
            handle = _handle;
            started = _started;
        }

        if (handle != IntPtr.Zero && started)
        {
            waveInStop(handle);
            waveInReset(handle);
        }

        lock (_sync)
        {
            if (handle != IntPtr.Zero)
            {
                foreach (var buffer in _buffers)
                {
                    waveInUnprepareHeader(handle, buffer.HeaderPointer, (uint)Marshal.SizeOf<WaveHeader>());
                    Marshal.FreeHGlobal(buffer.HeaderPointer);
                    Marshal.FreeHGlobal(buffer.DataPointer);
                }

                waveInClose(handle);
                _handle = IntPtr.Zero;
            }

            _buffers.Clear();
            _started = false;
        }
    }

    private void OnWaveIn(IntPtr handle, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2)
    {
        if (message != 0x03C8)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var header = Marshal.PtrToStructure<WaveHeader>(parameter1);
                if (header.dwBytesRecorded > 0 && header.lpData != IntPtr.Zero)
                {
                    var bytes = new byte[header.dwBytesRecorded];
                    Marshal.Copy(header.lpData, bytes, 0, bytes.Length);
                    DataAvailable?.Invoke(bytes);
                }

                if (!_stopping && _handle != IntPtr.Zero)
                {
                    header.dwBytesRecorded = 0;
                    header.dwFlags &= ~(uint)WhdrDone;
                    Marshal.StructureToPtr(header, parameter1, fDeleteOld: false);
                    var addResult = waveInAddBuffer(_handle, parameter1, (uint)Marshal.SizeOf<WaveHeader>());
                    if (addResult != MmSysErrNoError)
                    {
                        _callbackError = new InvalidOperationException($"重新投递录音缓冲区失败（WinMM {addResult}）。");
                    }
                }
            }
            catch (Exception ex)
            {
                _callbackError = ex;
            }
        }
    }

    private sealed record BufferState(IntPtr DataPointer, IntPtr HeaderPointer, int Length);

    private delegate void WaveInCallback(
        IntPtr handle,
        uint message,
        IntPtr instance,
        IntPtr parameter1,
        IntPtr parameter2);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveInGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(
        out IntPtr handle,
        int deviceId,
        ref WaveFormatEx format,
        IntPtr callback,
        IntPtr instance,
        int flags);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveInStop(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr handle);
}
