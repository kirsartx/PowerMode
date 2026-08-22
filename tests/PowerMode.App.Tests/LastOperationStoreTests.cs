using System.Text.Json;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class LastOperationStoreTests
{
    [Fact]
    public async Task WriteAsync_ReplacesExistingRecordAtomically()
    {
        using var directory = new TemporaryDirectory();
        var store = new LastOperationStore(
            Path.Combine(directory.Path, "last-operation.json"));
        await store.WriteAsync(JournalRecords.Prepared(
            Guid.Parse("44444444-4444-4444-4444-444444444444")));
        var replacement = JournalRecords.Verified(
            Guid.Parse("55555555-5555-5555-5555-555555555555"));

        var result = await store.WriteAsync(replacement);

        Assert.True(result.Succeeded);
        Assert.Equal(replacement, (await store.ReadAsync()).Record);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_ExpectedOperationConflictPreservesCurrentRecord()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "last-operation.json");
        var store = new LastOperationStore(path);
        var current = JournalRecords.Prepared(Guid.NewGuid());
        await store.WriteAsync(current);

        var result = await store.WriteAsync(
            JournalRecords.Verified(Guid.NewGuid()),
            expectedOperationId: Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.True(result.Conflict);
        Assert.Equal(current, (await store.ReadAsync()).Record);
    }

    [Fact]
    public async Task ReadAsync_MalformedJsonReturnsErrorWithoutDeletingFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "last-operation.json");
        const string malformed = "{ not json";
        await File.WriteAllTextAsync(path, malformed);

        var result = await new LastOperationStore(path).ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("JSON", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(malformed, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAsync_CancelledBeforeReplaceLeavesPreviousRecord()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "last-operation.json");
        var operations = new CancellingAtomicFileOperations();
        var store = new LastOperationStore(path, operations);
        var original = JournalRecords.Prepared(Guid.NewGuid());
        await store.WriteAsync(original);
        operations.CancelBeforeReplace = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync(JournalRecords.Verified(Guid.NewGuid())));

        Assert.Equal(original, (await store.ReadAsync()).Record);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    private static class JournalRecords
    {
        private static PowerModeState State => new(
            Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
            "Balanced",
            PowerModePreset.Balanced,
            PowerSourceKind.Ac,
            null,
            100,
            100,
            5,
            5,
            2,
            2,
            100,
            100,
            600,
            600,
            0,
            0,
            0,
            0,
            false);

        public static LastOperationRecord Prepared(Guid id) => new(
            1,
            id,
            "manual",
            "test",
            PowerModeTarget.ForPreset(PowerModePreset.Remote),
            32,
            false,
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
            State,
            LastOperationStatus.Prepared);

        public static LastOperationRecord Verified(Guid id) =>
            Prepared(id) with
            {
                Status = LastOperationStatus.Verified,
                Outcome = ModeSwitchOutcome.Succeeded
            };
    }

    private sealed class CancellingAtomicFileOperations : IAtomicFileOperations
    {
        private readonly BclAtomicFileOperations _inner = new();
        public bool CancelBeforeReplace { get; set; }

        public Task WriteThroughAsync(
            string path,
            ReadOnlyMemory<byte> contents,
            CancellationToken cancellationToken) =>
            _inner.WriteThroughAsync(path, contents, cancellationToken);

        public void Replace(string sourcePath, string destinationPath)
        {
            if (CancelBeforeReplace)
                throw new OperationCanceledException();
            _inner.Replace(sourcePath, destinationPath);
        }

        public void MoveNew(string sourcePath, string destinationPath) =>
            _inner.MoveNew(sourcePath, destinationPath);

        public void DeleteIfExists(string path) => _inner.DeleteIfExists(path);
    }
}
