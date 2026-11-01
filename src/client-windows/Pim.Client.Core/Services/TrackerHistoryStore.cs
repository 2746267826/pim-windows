using System.Text.Json;
using System.Text.Json.Serialization;
using Pim.Client.Core.Models;

namespace Pim.Client.Core.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackerActivityCategory
{
    Other,
    Development,
    Office,
    Communication,
    Browsing,
    Media,
    Idle,
}

public sealed record TrackerHistoryEntry
{
    public int SchemaVersion { get; init; } = 1;
    public long SessionId { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string[] Domains { get; init; } = [];
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public double DurationSeconds { get; init; }
    public bool IsIdle { get; init; }
    public bool IsMediaActive { get; init; }
    public TrackerActivityCategory Category { get; init; }
}

/// <summary>
/// Bounded, structured history of closed tracker sessions. It preserves the same
/// fields already collected for upload and never opens URLs in a browser.
/// </summary>
public sealed class TrackerHistoryStore
{
    private const int MaximumLineCharacters = 256 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<TrackerHistoryEntry> _entries = [];
    private readonly int _retentionDays;
    private readonly int _maxEntries;
    private readonly long _maxFileBytes;
    private bool _loaded;

    public TrackerHistoryStore(
        string? rootPath = null,
        int retentionDays = 30,
        int maxEntries = 10_000,
        long maxFileBytes = 64L * 1024 * 1024)
    {
        if (retentionDays is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        if (maxEntries is < 100 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxFileBytes is < 1024 * 1024 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));

        RootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PIM",
            "ComputerHistory");
        _retentionDays = retentionDays;
        _maxEntries = maxEntries;
        _maxFileBytes = maxFileBytes;
    }

    public string RootPath { get; }
    public string? LastError { get; private set; }
    private string HistoryPath => Path.Combine(RootPath, "sessions.jsonl");

    public async Task AppendAsync(TrackerSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var entry = CreateEntry(session);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadLockedAsync(cancellationToken);
            _entries.Add(entry);
            PruneLocked(DateTimeOffset.UtcNow);
            Directory.CreateDirectory(RootPath);
            await File.AppendAllTextAsync(
                HistoryPath,
                JsonSerializer.Serialize(entry) + Environment.NewLine,
                cancellationToken);
            if (new FileInfo(HistoryPath).Length > _maxFileBytes)
            {
                await RewriteLockedAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TrackerHistoryEntry>> QueryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TrackerActivityCategory? category = null,
        string? search = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
            throw new ArgumentException("查询结束时间不能早于开始时间。", nameof(to));
        if (limit is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadLockedAsync(cancellationToken);
            var needle = search?.Trim();
            return _entries
                .Where(entry => entry.StartedAt < to && entry.EndedAt > from)
                .Where(entry => category is null || entry.Category == category)
                .Where(entry => string.IsNullOrEmpty(needle)
                    || entry.AppName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || entry.WindowTitle.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || entry.Domains.Any(domain =>
                        domain.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(entry => entry.StartedAt)
                .Take(limit)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public static TrackerHistoryEntry CreateEntry(TrackerSession session)
    {
        var endedAt = session.EndedAt ?? DateTimeOffset.UtcNow;
        var duration = session.DurationSecs
            ?? Math.Max(0, (endedAt - session.StartedAt).TotalSeconds);
        var domains = session.PageVisits
            .Select(visit => visit.Domain)
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        return new TrackerHistoryEntry
        {
            SessionId = session.Id,
            DeviceId = session.DeviceId,
            AppName = session.AppName,
            WindowTitle = session.WindowTitle ?? string.Empty,
            Domains = domains,
            StartedAt = session.StartedAt,
            EndedAt = endedAt,
            DurationSeconds = Math.Max(0, duration),
            IsIdle = session.IsIdle,
            IsMediaActive = session.IsMediaActive,
            Category = Categorize(session.AppName, domains, session.IsIdle, session.IsMediaActive),
        };
    }

    public static TrackerActivityCategory Categorize(
        string? appName,
        IEnumerable<string>? domains = null,
        bool isIdle = false,
        bool isMediaActive = false)
    {
        if (isIdle)
            return TrackerActivityCategory.Idle;
        if (isMediaActive)
            return TrackerActivityCategory.Media;

        var app = Path.GetFileNameWithoutExtension(appName ?? string.Empty).ToLowerInvariant();
        if (Matches(app, "code", "code-insiders", "devenv", "rider", "idea64", "pycharm64",
                "webstorm64", "sublime_text", "notepad++", "powershell", "pwsh", "windowsterminal",
                "cmd", "git-bash", "docker desktop", "postman"))
            return TrackerActivityCategory.Development;
        if (Matches(app, "winword", "excel", "powerpnt", "onenote", "outlook", "wps",
                "notepad", "acrord32"))
            return TrackerActivityCategory.Office;
        if (Matches(app, "teams", "ms-teams", "slack", "discord", "wechat", "weixin", "qq",
                "zoom", "telegram"))
            return TrackerActivityCategory.Communication;
        if (Matches(app, "vlc", "potplayermini64", "spotify", "musicbee", "mpc-hc64"))
            return TrackerActivityCategory.Media;

        var domainList = domains?.Where(domain => !string.IsNullOrWhiteSpace(domain)).ToArray() ?? [];
        if (domainList.Length > 0)
        {
            if (domainList.Any(IsDevelopmentDomain))
                return TrackerActivityCategory.Development;
            if (domainList.Any(IsCommunicationDomain))
                return TrackerActivityCategory.Communication;
            if (domainList.Any(IsMediaDomain))
                return TrackerActivityCategory.Media;
            return TrackerActivityCategory.Browsing;
        }

        return TrackerActivityCategory.Other;
    }

    private async Task LoadLockedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return;

        _loaded = true;
        if (!File.Exists(HistoryPath))
            return;

        try
        {
            await foreach (var line in ReadBoundedTailLinesAsync(HistoryPath, cancellationToken))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<TrackerHistoryEntry>(line);
                    if (entry is { SchemaVersion: 1 }
                        && !string.IsNullOrWhiteSpace(entry.AppName)
                        && entry.EndedAt >= entry.StartedAt)
                    {
                        _entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // A truncated tail must not hide the remaining valid history.
                }
            }
        }
        catch (IOException ex)
        {
            LastError = ex.Message;
        }

        PruneLocked(DateTimeOffset.UtcNow);
    }

    private async IAsyncEnumerable<string> ReadBoundedTailLinesAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var start = Math.Max(0, stream.Length - _maxFileBytes);
        stream.Position = start;
        using var reader = new StreamReader(stream);
        if (start > 0)
        {
            _ = await reader.ReadLineAsync(cancellationToken);
        }

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length is > 0 and <= MaximumLineCharacters)
            {
                yield return line;
            }
        }
    }

    private void PruneLocked(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-_retentionDays);
        _entries.RemoveAll(entry => entry.StartedAt < cutoff);
        if (_entries.Count <= _maxEntries)
            return;

        _entries.Sort((left, right) => left.StartedAt.CompareTo(right.StartedAt));
        _entries.RemoveRange(0, _entries.Count - _maxEntries);
    }

    private async Task RewriteLockedAsync(CancellationToken cancellationToken)
    {
        PruneLocked(DateTimeOffset.UtcNow);
        _entries.Sort((left, right) => left.StartedAt.CompareTo(right.StartedAt));
        if (_entries.Count > _maxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - _maxEntries);
        }

        var temporaryPath = HistoryPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous))
        await using (var writer = new StreamWriter(stream))
        {
            foreach (var entry in _entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(entry).AsMemory(),
                    cancellationToken);
            }
        }

        File.Move(temporaryPath, HistoryPath, overwrite: true);
    }

    private static bool Matches(string app, params string[] names)
        => names.Any(name => string.Equals(app, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsDevelopmentDomain(string domain)
        => ContainsAny(domain, "github.", "gitlab.", "stackoverflow.", "stackexchange.",
            "developer.", "docs.", "learn.microsoft.", "npmjs.", "nuget.org", "localhost",
            "127.0.0.1", "openai.com", "azure.com", "cloud.google.");

    private static bool IsCommunicationDomain(string domain)
        => ContainsAny(domain, "outlook.", "mail.", "slack.com", "discord.com", "teams.",
            "zoom.us", "telegram.", "web.wechat.com");

    private static bool IsMediaDomain(string domain)
        => ContainsAny(domain, "youtube.", "youtu.be", "bilibili.com", "netflix.com",
            "spotify.com", "music.", "twitch.tv");

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
