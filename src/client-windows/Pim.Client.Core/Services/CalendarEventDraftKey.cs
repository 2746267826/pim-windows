namespace Pim.Client.Core.Services;

/// <summary>
/// Keeps one idempotency key for one unchanged event draft. Retrying the same draft
/// reuses the key required by the server's Outlook writeback ledger; editing a field
/// starts a new draft and therefore a new key.
/// </summary>
public sealed class CalendarEventDraftKey
{
    private readonly object _sync = new();
    private DraftSnapshot? _snapshot;
    private Guid _operationId;

    public Guid Resolve(
        Guid calendarId,
        string title,
        DateTimeOffset start,
        DateTimeOffset end,
        bool isAllDay,
        string? location,
        string? description)
    {
        var snapshot = new DraftSnapshot(
            calendarId,
            title.Trim(),
            start,
            end,
            isAllDay,
            NormalizeOptional(location),
            NormalizeOptional(description));

        lock (_sync)
        {
            if (_snapshot != snapshot)
            {
                _snapshot = snapshot;
                _operationId = Guid.NewGuid();
            }

            return _operationId;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _snapshot = null;
            _operationId = Guid.Empty;
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record DraftSnapshot(
        Guid CalendarId,
        string Title,
        DateTimeOffset Start,
        DateTimeOffset End,
        bool IsAllDay,
        string? Location,
        string? Description);
}
