using System.Text;
using System.Text.Json;
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

    [Fact]
    public async Task QueryPrunesExpiredEntriesAndRewritesTheDisk()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var store = new TrackerHistoryStore(_root, retentionDays: 30, timeProvider: clock);
        await store.AppendAsync(Session(
            20,
            "devenv",
            "过期记录",
            clock.GetUtcNow().AddDays(-40),
            clock.GetUtcNow().AddDays(-40).AddMinutes(5)));

        clock.Advance(TimeSpan.FromDays(31));
        var entries = await store.QueryAsync(
            clock.GetUtcNow().AddDays(-60),
            clock.GetUtcNow().AddMinutes(1));

        Assert.Empty(entries);
        Assert.Empty(await File.ReadAllLinesAsync(Path.Combine(_root, "sessions.jsonl")));
    }

    [Fact]
    public async Task AppendAfterRetentionCutoffPrunesOldDiskRowsBeforeAddingNewOnes()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var store = new TrackerHistoryStore(_root, retentionDays: 30, timeProvider: clock);
        await store.AppendAsync(Session(
            21,
            "devenv",
            "旧会话",
            clock.GetUtcNow().AddMinutes(-20),
            clock.GetUtcNow().AddMinutes(-10)));

        clock.Advance(TimeSpan.FromDays(31));
        await store.AppendAsync(Session(
            22,
            "chrome",
            "新会话",
            clock.GetUtcNow().AddMinutes(-5),
            clock.GetUtcNow()));

        var entries = await store.QueryAsync(
            clock.GetUtcNow().AddDays(-60),
            clock.GetUtcNow().AddMinutes(1));
        var persisted = Assert.Single(entries);
        Assert.Equal("新会话", persisted.WindowTitle);
        var diskEntries = (await File.ReadAllLinesAsync(Path.Combine(_root, "sessions.jsonl")))
            .Select(line => JsonSerializer.Deserialize<TrackerHistoryEntry>(line)!)
            .ToArray();
        var diskEntry = Assert.Single(diskEntries);
        Assert.Equal(22, diskEntry.SessionId);
        Assert.Equal("新会话", diskEntry.WindowTitle);
    }

    [Fact]
    public async Task EntryBudgetPrunesBothMemoryAndDiskOnLoad()
    {
        var now = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(_root);
        var lines = Enumerable.Range(1, 105)
            .Select(index => JsonSerializer.Serialize(TrackerHistoryStore.CreateEntry(Session(
                index,
                "devenv",
                "记录 " + index,
                now.AddMinutes(index),
                now.AddMinutes(index + 1)))))
            .ToArray();
        await File.WriteAllLinesAsync(Path.Combine(_root, "sessions.jsonl"), lines);

        var store = new TrackerHistoryStore(
            _root,
            retentionDays: 30,
            maxEntries: 100,
            timeProvider: new MutableTimeProvider(now));
        var entries = await store.QueryAsync(now.AddDays(-1), now.AddDays(1), limit: 500);

        Assert.Equal(100, entries.Count);
        Assert.Equal(100, (await File.ReadAllLinesAsync(
            Path.Combine(_root, "sessions.jsonl"))).Length);
        Assert.DoesNotContain(entries, entry => entry.WindowTitle == "记录 1");
        Assert.Contains(entries, entry => entry.WindowTitle == "记录 105");
    }

    [Fact]
    public async Task BoundedSnapshotDoesNotReadRowsAppendedAfterItStarts()
    {
        await using var stream = new GrowingAfterFirstReadStream(
            Encoding.UTF8.GetBytes("first\n"),
            Encoding.UTF8.GetBytes("second\n"));
        var lines = new List<string>();
        await foreach (var line in TrackerHistoryStore.ReadBoundedSnapshotLinesAsync(
                           stream,
                           maxBytes: 4096,
                           CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(["first"], lines);
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

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class GrowingAfterFirstReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly byte[] _appended;
        private bool _grew;

        public GrowingAfterFirstReadStream(byte[] initialBytes, byte[] appendedBytes)
        {
            _inner = new MemoryStream();
            _inner.Write(initialBytes);
            _inner.Position = 0;
            _appended = appendedBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            if (read > 0 && !_grew)
            {
                var position = _inner.Position;
                _inner.Position = _inner.Length;
                _inner.Write(_appended);
                _inner.Position = position;
                _grew = true;
            }

            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ReadSpan(buffer.Span));

        private int ReadSpan(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            if (read > 0 && !_grew)
            {
                var position = _inner.Position;
                _inner.Position = _inner.Length;
                _inner.Write(_appended);
                _inner.Position = position;
                _grew = true;
            }

            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
