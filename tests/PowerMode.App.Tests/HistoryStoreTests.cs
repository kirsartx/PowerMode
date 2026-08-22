using System.Text;
using System.Text.Json;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class HistoryStoreTests
{
    [Fact]
    public async Task RecordAsync_BuildsIndexOnce_ThenAppendsWithoutRescanning()
    {
        using var directory = new TemporaryDirectory();
        var io = new HistoryTestIo();
        var store = io.CreateStore(Path.Combine(directory.Path, "history.jsonl"));

        await store.RecordAsync(Entry(1));
        await store.RecordAsync(Entry(2));

        Assert.Equal(1, io.ReadCalls);
        Assert.Equal(2, io.AppendCalls);
    }

    [Fact]
    public async Task RecordAsync_DuplicateIdDoesNotAppend()
    {
        using var directory = new TemporaryDirectory();
        var io = new HistoryTestIo();
        var store = io.CreateStore(Path.Combine(directory.Path, "history.jsonl"));
        var entry = Entry(1);

        await store.RecordAsync(entry);
        await store.RecordAsync(entry);

        Assert.Equal(1, io.AppendCalls);
        Assert.Single(await store.GetRecentAsync());
    }

    [Fact]
    public async Task RecordAsync_CompactsAtMaximumEntries_AndMalformedLinesDoNotConsumeSlots()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.jsonl");
        await File.WriteAllTextAsync(path, "not json\n");
        var io = new HistoryTestIo();
        var store = io.CreateStore(path);

        for (var index = 1; index <= HistoryStore.MaximumEntries + 1; index++)
        {
            await store.RecordAsync(Entry(index));
        }

        var lines = await File.ReadAllLinesAsync(path);
        var recent = await store.GetRecentAsync(HistoryStore.MaximumEntries + 10);

        Assert.Equal(HistoryStore.MaximumEntries, lines.Length);
        Assert.Equal(HistoryStore.MaximumEntries, recent.Count);
        Assert.Equal(2001, recent[0].IdVersion());
        Assert.Equal(2, recent[^1].IdVersion());
        Assert.Equal(1, io.ReadCalls);
        Assert.True(io.RewriteCalls >= 1);
    }

    [Fact]
    public async Task RecordAsync_AppendFailureInvalidatesIndex_AndRetryRebuildsWithoutDuplicate()
    {
        using var directory = new TemporaryDirectory();
        var io = new HistoryTestIo { ThrowBeforeAppend = true };
        var store = io.CreateStore(Path.Combine(directory.Path, "history.jsonl"));
        var entry = Entry(1);

        await Assert.ThrowsAsync<IOException>(() => store.RecordAsync(entry));
        io.ThrowBeforeAppend = false;
        await store.RecordAsync(entry);

        Assert.Equal(2, io.ReadCalls);
        Assert.Equal(1, io.AppendCalls);
        Assert.Single(await store.GetRecentAsync());
    }

    [Fact]
    public async Task RecordAsync_AfterWriteFailureInvalidatesIndex_AndRetryDoesNotDuplicate()
    {
        using var directory = new TemporaryDirectory();
        var io = new HistoryTestIo { ThrowAfterAppend = true };
        var store = io.CreateStore(Path.Combine(directory.Path, "history.jsonl"));
        var entry = Entry(1);

        await Assert.ThrowsAsync<IOException>(() => store.RecordAsync(entry));
        io.ThrowAfterAppend = false;
        await store.RecordAsync(entry);

        Assert.Equal(2, io.ReadCalls);
        Assert.Equal(1, io.AppendCalls);
        Assert.Single(await store.GetRecentAsync());
    }

    [Fact]
    public async Task ClearAsync_ResetsCachedIdsAndCount()
    {
        using var directory = new TemporaryDirectory();
        var io = new HistoryTestIo();
        var store = io.CreateStore(Path.Combine(directory.Path, "history.jsonl"));
        var entry = Entry(1);

        await store.RecordAsync(entry);
        await store.ClearAsync();
        await store.RecordAsync(entry);

        Assert.Equal(2, io.ReadCalls);
        Assert.Equal(2, io.AppendCalls);
        Assert.Single(await store.GetRecentAsync());
    }

    [Fact]
    public async Task TrimAsync_CompactionFailurePreservesFileAndInvalidatesCache()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.jsonl");
        await File.WriteAllLinesAsync(
            path,
            Enumerable.Range(1, HistoryStore.MaximumEntries + 1)
                .Select(index => JsonSerializer.Serialize(Entry(index))));

        var original = await File.ReadAllBytesAsync(path);
        var io = new HistoryTestIo { ThrowOnRewrite = true };
        var store = io.CreateStore(path);

        await Assert.ThrowsAsync<IOException>(() => store.TrimAsync());

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        var recent = await store.GetRecentAsync(1);
        Assert.Equal(2001, recent[0].IdVersion());
        Assert.Equal(2, io.ReadCalls);
    }

    [Fact]
    public async Task TwoStoreInstancesShareIndexStateForCanonicalPath()
    {
        using var directory = new TemporaryDirectory();
        var relative = Path.Combine(directory.Path, ".", "history.jsonl");
        var io = new HistoryTestIo();
        var first = io.CreateStore(relative);
        var second = io.CreateStore(Path.GetFullPath(relative));

        await first.RecordAsync(Entry(1));
        await second.RecordAsync(Entry(2));

        Assert.Equal(1, io.ReadCalls);
        Assert.Equal(2, io.AppendCalls);
    }

    private static SwitchHistoryEntry Entry(int version) => new()
    {
        Id = Guid.Parse($"00000000-0000-0000-0000-{version:000000000000}"),
        Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(version),
        PreviousMode = "balanced",
        TargetMode = $"mode-{version}",
        Succeeded = true
    };

    private sealed class HistoryTestIo
    {
        public int ReadCalls { get; private set; }
        public int AppendCalls { get; private set; }
        public int RewriteCalls { get; private set; }
        public bool ThrowBeforeAppend { get; set; }
        public bool ThrowAfterAppend { get; set; }
        public bool ThrowOnRewrite { get; set; }

        public HistoryStore CreateStore(string path) => new(
            path,
            AppendAsync,
            ReadAsync,
            RewriteAsync);

        private async Task<IReadOnlyList<SwitchHistoryEntry>> ReadAsync(
            string path,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            if (!File.Exists(path))
                return [];

            var entries = new List<SwitchHistoryEntry>();
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<SwitchHistoryEntry>(line);
                    if (entry is not null)
                        entries.Add(entry);
                }
                catch (JsonException)
                {
                }
            }
            return entries;
        }

        private async Task AppendAsync(
            string path,
            string contents,
            CancellationToken cancellationToken)
        {
            if (ThrowBeforeAppend)
                throw new IOException("append before write");
            AppendCalls++;
            await File.AppendAllTextAsync(path, contents, cancellationToken);
            if (ThrowAfterAppend)
                throw new IOException("append after write");
        }

        private async Task RewriteAsync(
            string path,
            string contents,
            CancellationToken cancellationToken)
        {
            RewriteCalls++;
            if (ThrowOnRewrite)
                throw new IOException("rewrite failed");
            var temporaryPath = path + ".history-test.tmp";
            await File.WriteAllTextAsync(temporaryPath, contents, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
    }
}

file static class SwitchHistoryEntryTestExtensions
{
    public static int IdVersion(this SwitchHistoryEntry entry) =>
        entry.Id == Guid.Empty ? 0 : int.Parse(entry.TargetMode[5..]);
}
