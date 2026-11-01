using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.Client.Tests;

public sealed class TrackerHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-tracker-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ClosedSessionsPersistAndQueryByCategorySearchAndRange()
    {
        var now = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var store = new TrackerHistoryStore(_root);
        await store.AppendAsync(Session(
            1,
            "devenv",
            "PIM 开发",
            now.AddMinutes(-40),
            now.AddMinutes(-10),
            ["github.com"]));
        await store.AppendAsync(Session(
            2,
            "chrome",
            "项目资料",
            now.AddDays(-2),
            now.AddDays(-2).AddMinutes(5),
            ["learn.microsoft.com"]));

        var development = await store.QueryAsync(
            now.AddDays(-1),
            now.AddMinutes(1),
            TrackerActivityCategory.Development);
        var search = await store.QueryAsync(
            now.AddDays(-7),
            now.AddMinutes(1),
            search: "github");
        var outsideRange = await store.QueryAsync(
            now.AddDays(-20),
            now.AddDays(-10));

        var entry = Assert.Single(development);
        Assert.Equal("devenv", entry.AppName);
        Assert.Equal(1800, entry.DurationSeconds);
        Assert.Single(search);
        Assert.Empty(outsideRange);

        var reloaded = new TrackerHistoryStore(_root);
        var persisted = await reloaded.QueryAsync(
            now.AddDays(-7),
            now.AddMinutes(1));
        Assert.Equal(2, persisted.Count);
        Assert.Contains(
            persisted,
            item => item.Category == TrackerActivityCategory.Development
                    && item.Domains.Contains("github.com"));
    }

    [Fact]
    public async Task CorruptAndOversizedLinesDoNotHideValidHistory()
    {
        var now = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var store = new TrackerHistoryStore(_root);
        await store.AppendAsync(Session(
            10,
            "WINWORD.EXE",
            "周报.docx",
            now.AddMinutes(-20),
            now.AddMinutes(-5)));

        await File.AppendAllTextAsync(
            Path.Combine(_root, "sessions.jsonl"),
            "{broken-json}" + Environment.NewLine + new string('x', 300 * 1024) + Environment.NewLine);

        var reloaded = new TrackerHistoryStore(_root);
        var entries = await reloaded.QueryAsync(now.AddHours(-1), now.AddMinutes(1));

        var entry = Assert.Single(entries);
        Assert.Equal(TrackerActivityCategory.Office, entry.Category);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static TrackerSession Session(
        long id,
        string appName,
        string title,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string[]? domains = null)
        => new()
        {
            Id = id,
            DeviceId = "synthetic-device",
            AppName = appName,
            WindowTitle = title,
            StartedAt = startedAt,
            EndedAt = endedAt,
            DurationSecs = (endedAt - startedAt).TotalSeconds,
            PageVisits = (domains ?? [])
                .Select(domain => new TrackerPageVisit
                {
                    SessionId = id,
                    Domain = domain,
                    Url = "https://" + domain + "/",
                    StartedAt = startedAt,
                    EndedAt = endedAt,
                })
                .ToList(),
        };
}
