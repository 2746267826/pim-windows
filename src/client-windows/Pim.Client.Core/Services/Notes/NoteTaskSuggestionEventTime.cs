using System.Globalization;
using System.Text.RegularExpressions;

namespace Pim.Client.Core.Services.Notes;

public static partial class NoteTaskSuggestionEventTime
{
    private static readonly string[] TimeFormats = ["HH:mm", "H:mm"];

    public static bool IsIanaTimeZone(string? timeZoneId)
        => TryResolveTimeZone(timeZoneId, out _);

    public static string RequireIanaTimeZone(string? timeZoneId)
    {
        var normalized = NormalizeTimeZoneId(timeZoneId);
        if (normalized is null)
            throw new ArgumentException("请先填写日程的 IANA 时区。", nameof(timeZoneId));
        if (!IsIanaTimeZone(normalized))
            throw new ArgumentException("日程时区必须是有效的 IANA 时区。", nameof(timeZoneId));
        return normalized;
    }

    public static string? NormalizeTimeZoneId(string? timeZoneId)
    {
        var normalized = timeZoneId?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    public static bool TryFromWallClock(
        DateTime date,
        string? timeText,
        string? timeZoneId,
        out DateTimeOffset value,
        out string error)
    {
        value = default;
        error = "";
        if (date == default)
        {
            error = "请选择日期。";
            return false;
        }

        var normalizedTime = timeText?.Trim();
        if (string.IsNullOrEmpty(normalizedTime)
            || !DateTime.TryParseExact(
                normalizedTime,
                TimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedTime))
        {
            error = "时间格式必须为 HH:mm。";
            return false;
        }

        if (!TryResolveTimeZone(timeZoneId, out var timeZone))
        {
            error = "日程时区必须是有效的 IANA 时区。";
            return false;
        }

        var wallClock = DateTime.SpecifyKind(
            date.Date.Add(parsedTime.TimeOfDay),
            DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(wallClock))
        {
            error = "该时间在所选 IANA 时区不存在（夏令时跳变）。";
            return false;
        }
        if (timeZone.IsAmbiguousTime(wallClock))
        {
            error = "该时间在所选 IANA 时区重复（夏令时回拨），无法唯一换算。";
            return false;
        }

        value = new DateTimeOffset(wallClock, timeZone.GetUtcOffset(wallClock))
            .ToUniversalTime();
        return true;
    }

    public static bool TryFromWallClockOrPreserve(
        DateTime date,
        string? timeText,
        string? timeZoneId,
        DateTimeOffset? preserved,
        out DateTimeOffset value,
        out string error)
    {
        if (TryToWallClock(
                preserved,
                timeZoneId,
                out var preservedDate,
                out var preservedTime,
                out _)
            && preservedDate == date.Date
            && string.Equals(preservedTime, timeText?.Trim(), StringComparison.Ordinal))
        {
            value = preserved!.Value;
            error = "";
            return true;
        }

        return TryFromWallClock(date, timeText, timeZoneId, out value, out error);
    }

    public static bool TryToWallClock(
        DateTimeOffset? instant,
        string? timeZoneId,
        out DateTime date,
        out string time,
        out string error)
    {
        date = default;
        time = "";
        error = "";
        if (instant is null)
        {
            error = "缺少时间。";
            return false;
        }
        if (!TryResolveTimeZone(timeZoneId, out var timeZone))
        {
            error = "日程时区必须是有效的 IANA 时区。";
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(instant.Value, timeZone);
        date = local.Date;
        time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return true;
    }

    public static (DateTime Date, string Time) ToUtcWallClock(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return (utc.Date, utc.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    public static string FormatRange(
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? timeZoneId)
    {
        if (start is null || end is null)
            return "开始与结束时间未完整。";

        var normalizedZone = NormalizeTimeZoneId(timeZoneId);
        if (normalizedZone is null)
        {
            var utcStart = ToUtcWallClock(start.Value);
            var utcEnd = ToUtcWallClock(end.Value);
            return $"{FormatRangeParts(utcStart, utcEnd)} UTC（时区未知）";
        }
        if (!IsIanaTimeZone(normalizedZone))
            return "时区无效；请核对 IANA 时区后再确认创建日程。";
        if (!TryToWallClock(start, normalizedZone, out var startDate, out var startTime, out _)
            || !TryToWallClock(end, normalizedZone, out var endDate, out var endTime, out _))
        {
            return "日程时间无效；请重新选择开始和结束时间。";
        }

        var range = FormatRangeParts((startDate, startTime), (endDate, endTime));
        return $"{range} · {normalizedZone}";
    }

    private static string FormatRangeParts(
        (DateTime Date, string Time) start,
        (DateTime Date, string Time) end)
        => start.Date == end.Date
            ? $"{start.Date:yyyy-MM-dd} {start.Time}–{end.Time}"
            : $"{start.Date:yyyy-MM-dd} {start.Time} – {end.Date:yyyy-MM-dd} {end.Time}";

    private static bool TryResolveTimeZone(string? timeZoneId, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        var normalized = NormalizeTimeZoneId(timeZoneId);
        if (normalized is null
            || normalized.Any(char.IsWhiteSpace)
            || FixedOffsetPattern().IsMatch(normalized)
            || NamedOffsetPattern().IsMatch(normalized))
        {
            return false;
        }

        try
        {
            var found = TimeZoneInfo.FindSystemTimeZoneById(normalized);
            if (found.HasIanaId)
            {
                timeZone = found;
                return true;
            }
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        if (!OperatingSystem.IsWindows()
            || !TimeZoneInfo.TryConvertIanaIdToWindowsId(normalized, out var windowsId))
        {
            return false;
        }

        try
        {
            var found = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            timeZone = found;
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^[+-]\d{2}:?\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex FixedOffsetPattern();

    [GeneratedRegex(
        @"^(?:UTC|GMT)[+-]\d{1,2}(?::?\d{2})?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedOffsetPattern();
}
