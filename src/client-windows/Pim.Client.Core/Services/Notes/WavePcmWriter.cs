using System.Text;

namespace Pim.Client.Core.Services.Notes;

public sealed record WavePcmResult(long DataBytes, TimeSpan Duration, double PeakLevel);

public sealed class WavePcmWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly int _sampleRate;
    private readonly short _channels;
    private readonly short _bitsPerSample;
    private long _dataBytes;
    private double _peakLevel;
    private bool _completed;

    public WavePcmWriter(
        string path,
        int sampleRate = 16_000,
        short channels = 1,
        short bitsPerSample = 16)
    {
        if (sampleRate <= 0 || sampleRate > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (bitsPerSample != 16)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "当前录音只写入 16 位 PCM。");
        }

        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _sampleRate = sampleRate;
        _channels = channels;
        _bitsPerSample = bitsPerSample;
        _stream = new FileStream(
            Path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            64 * 1024,
            FileOptions.WriteThrough);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        WriteHeader(dataBytes: 0);
    }

    public string Path { get; }

    public long DataBytes
    {
        get
        {
            lock (_sync)
            {
                return _dataBytes;
            }
        }
    }

    public double PeakLevel
    {
        get
        {
            lock (_sync)
            {
                return _peakLevel;
            }
        }
    }

    public void Append(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            var blockAlign = _channels * (_bitsPerSample / 8);
            var alignedLength = pcm.Length - pcm.Length % blockAlign;
            if (alignedLength <= 0)
            {
                return;
            }

            _stream.Seek(0, SeekOrigin.End);
            _stream.Write(pcm[..alignedLength]);
            _dataBytes += alignedLength;
            _peakLevel = Math.Max(_peakLevel, CalculatePeak(pcm[..alignedLength]));
        }
    }

    public WavePcmResult Complete()
    {
        lock (_sync)
        {
            if (_completed)
            {
                return BuildResult();
            }

            _stream.Flush(flushToDisk: true);
            _stream.Seek(0, SeekOrigin.Begin);
            WriteHeader(_dataBytes);
            _stream.Flush(flushToDisk: true);
            _completed = true;
            _writer.Dispose();
            _stream.Dispose();
            return BuildResult();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            try
            {
                _stream.Flush(flushToDisk: true);
                _stream.Seek(0, SeekOrigin.Begin);
                WriteHeader(_dataBytes);
                _stream.Flush(flushToDisk: true);
            }
            finally
            {
                _completed = true;
                _writer.Dispose();
                _stream.Dispose();
            }
        }
    }

    private WavePcmResult BuildResult()
    {
        var blockAlign = _channels * (_bitsPerSample / 8);
        var duration = _sampleRate <= 0 || blockAlign <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)_dataBytes / blockAlign / _sampleRate);
        return new WavePcmResult(_dataBytes, duration, _peakLevel);
    }

    private void WriteHeader(long dataBytes)
    {
        var blockAlign = (short)(_channels * (_bitsPerSample / 8));
        var byteRate = _sampleRate * blockAlign;
        _writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        _writer.Write((uint)(36 + dataBytes));
        _writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        _writer.Write(Encoding.ASCII.GetBytes("fmt "));
        _writer.Write(16u);
        _writer.Write((short)1);
        _writer.Write(_channels);
        _writer.Write(_sampleRate);
        _writer.Write(byteRate);
        _writer.Write(blockAlign);
        _writer.Write(_bitsPerSample);
        _writer.Write(Encoding.ASCII.GetBytes("data"));
        _writer.Write((uint)dataBytes);
    }

    private static double CalculatePeak(ReadOnlySpan<byte> pcm)
    {
        var peak = 0;
        for (var offset = 0; offset + 1 < pcm.Length; offset += 2)
        {
            var sample = BitConverter.ToInt16(pcm[offset..(offset + 2)]);
            var absolute = sample == short.MinValue ? short.MaxValue : Math.Abs((int)sample);
            peak = Math.Max(peak, absolute);
        }

        return Math.Clamp(peak / (double)short.MaxValue, 0, 1);
    }
}
