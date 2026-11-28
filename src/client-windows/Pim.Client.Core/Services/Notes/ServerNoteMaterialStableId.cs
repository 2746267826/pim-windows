using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pim.Client.Core.Services.Notes;

/// <summary>
/// Deterministic identities shared by the upload bundle and server restore so
/// Markdown material anchors resolve to the same local material after restore.
/// </summary>
internal static class ServerNoteMaterialStableId
{
    public static Guid ForRecordingChunk(
        Guid recordingId,
        int segment,
        int chunkIndex,
        long samples)
    {
        if (recordingId == Guid.Empty)
        {
            throw new ArgumentException("录音标识不能为空。", nameof(recordingId));
        }
        if (segment <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segment), "录音段号必须为正数。");
        }
        if (chunkIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex), "录音分片序号必须为正数。");
        }
        if (samples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), "录音样本数必须为正数。");
        }

        return Create(
            "server-recording-chunk",
            recordingId.ToString("D"),
            segment.ToString(CultureInfo.InvariantCulture),
            chunkIndex.ToString(CultureInfo.InvariantCulture),
            samples.ToString(CultureInfo.InvariantCulture));
    }

    public static Guid Create(string purpose, params string[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(parts);
        var input = purpose + ":" + string.Join(":", parts);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var bytes = digest.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var id = new Guid(bytes);
        return id == Guid.Empty
            ? new Guid("00000000-0000-0000-0000-000000000001")
            : id;
    }
}
