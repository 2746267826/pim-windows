namespace Pim.Client.Core.Services;

/// <summary>
/// R04: normalize the repeatable task status query before it reaches the HTTP boundary.
/// The server treats the values as an OR set; blank-only input means no filter,
/// duplicates are ignored, and any invalid or mixed blank/valid input rejects the whole request.
/// </summary>
public static class TaskStatusQuery
{
    public static IReadOnlyList<string> AllowedStatuses { get; } =
        ["NEEDS-ACTION", "IN-PROCESS", "COMPLETED", "CANCELLED"];

    /// <summary>
    /// Preserves the legacy single-value parameter while adding the repeatable form.
    /// Supplying both forms is a conflict, matching the MCP status/statuses rule.
    /// </summary>
    public static IReadOnlyList<string> Normalize(
        string? status,
        IEnumerable<string?>? statuses)
    {
        if (!string.IsNullOrWhiteSpace(status) && statuses is not null)
        {
            throw new ArgumentException(
                "任务状态筛选不能同时使用单值 status 与多值 statuses。",
                nameof(statuses));
        }

        if (statuses is not null)
        {
            return Normalize(statuses);
        }

        return Normalize(new[] { status });
    }

    /// <summary>
    /// Returns the exact values to send as repeated <c>status</c> query parameters.
    /// Empty or blank-only input returns an empty set, which the caller omits entirely.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? statuses)
    {
        if (statuses is null)
        {
            return Array.Empty<string>();
        }

        var values = statuses as IReadOnlyCollection<string?> ?? statuses.ToArray();
        if (values.Count == 0)
        {
            return Array.Empty<string>();
        }

        var nonBlank = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        if (nonBlank.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (nonBlank.Length != values.Count)
        {
            throw new ArgumentException(
                $"任务状态筛选不能混用空值与有效状态；允许值：{string.Join(", ", AllowedStatuses)}。",
                nameof(statuses));
        }

        foreach (var status in nonBlank)
        {
            if (!AllowedStatuses.Contains(status, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"任务状态 '{status}' 无效；允许值：{string.Join(", ", AllowedStatuses)}。",
                    nameof(statuses));
            }
        }

        return nonBlank.Distinct(StringComparer.Ordinal).ToArray();
    }
}
