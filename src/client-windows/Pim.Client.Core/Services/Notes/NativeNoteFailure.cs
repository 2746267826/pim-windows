using System.Text;

namespace Pim.Client.Core.Services.Notes;

/// <summary>
/// Produces one stable, traceable description for a note failure. The summary
/// is shown in the compact in-place notice; the details are kept behind the
/// "错误详情" expander and the same summary is written to the daemon log with
/// the full exception.
/// </summary>
public static class NativeNoteFailure
{
    public static string Describe(
        string operation,
        Exception exception,
        Guid? noteId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message.Trim();
        var note = noteId is { } id && id != Guid.Empty
            ? $" note={id:D}"
            : string.Empty;
        return $"{operation.Trim()} [{exception.GetType().FullName}]{note}: {message}";
    }

    public static string Details(
        string operation,
        Exception exception,
        Guid? noteId = null,
        DateTimeOffset? occurredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);
        var builder = new StringBuilder();
        builder.AppendLine(Describe(operation, exception, noteId));
        builder.Append("发生时间：")
            .AppendLine((occurredAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz"));
        builder.AppendLine("异常堆栈：");
        builder.Append(exception);
        return builder.ToString().TrimEnd();
    }
}
