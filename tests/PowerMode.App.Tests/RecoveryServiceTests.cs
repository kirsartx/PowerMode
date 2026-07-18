using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class RecoveryServiceTests
{
    [Fact]
    public async Task HistoryStore_OldJsonWithoutRecoveryMetadata_DefaultsToModeSwitch()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"powermode-recovery-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "switch-history.jsonl");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path,
            """{"Id":"2427e5d2-8d27-42de-a0b3-93a304a017ed","PreviousMode":"balanced","TargetMode":"high","Succeeded":true}""");

        try
        {
            var entry = Assert.Single(await new HistoryStore(path).GetRecentAsync());

            Assert.Equal("mode-switch", entry.OperationKind);
            Assert.Null(entry.RelatedOperationId);
            Assert.False(entry.IsUndo);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FindLatestUndoableAsync_SkipsFailedCustomAndAlreadyUndoneOperations()
    {
        var eligible = ModeSwitch("balanced", "high");
        var alreadyUndone = ModeSwitch("remote", "saver");
        var undo = new SwitchHistoryEntry
        {
            Succeeded = true,
            OperationKind = "mode-undo",
            IsUndo = true,
            RelatedOperationId = alreadyUndone.Id
        };
        var custom = ModeSwitch("high", "profile:Quiet");
        var failed = ModeSwitch("high", "remote");
        failed.Succeeded = false;
        var history = new InMemoryRecoveryHistory([failed, custom, undo, alreadyUndone, eligible]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());

        var result = await service.FindLatestUndoableAsync();

        Assert.Equal(eligible.Id, result?.Id);
    }

    [Fact]
    public async Task FindLatestUndoableAsync_DoesNotTreatFailedUndoAsCompleted()
    {
        var original = ModeSwitch("balanced", "high");
        var failedUndo = new SwitchHistoryEntry
        {
            Succeeded = false,
            OperationKind = "mode-undo",
            IsUndo = true,
            RelatedOperationId = original.Id
        };
        var service = new RecoveryService(
            new InMemoryRecoveryHistory([failedUndo, original]),
            new FakeRecoveryBackend());

        var result = await service.FindLatestUndoableAsync();

        Assert.Equal(original.Id, result?.Id);
    }

    [Fact]
    public async Task FindLatestUndoableAsync_RequiresConsistentUndoMetadata()
    {
        var original = ModeSwitch("balanced", "high");
        var unrelatedMutation = new SwitchHistoryEntry
        {
            Succeeded = true,
            OperationKind = "configuration-reset",
            IsUndo = true,
            RelatedOperationId = original.Id
        };
        var service = new RecoveryService(
            new InMemoryRecoveryHistory([unrelatedMutation, original]),
            new FakeRecoveryBackend());

        var result = await service.FindLatestUndoableAsync();

        Assert.Equal(original.Id, result?.Id);
    }

    [Fact]
    public async Task FindLatestUndoableAsync_ReturnsNullWhenNoEligibleOperationExists()
    {
        var custom = ModeSwitch("balanced", "profile:Quiet");
        var failed = ModeSwitch("balanced", "high");
        failed.Succeeded = false;
        var service = new RecoveryService(
            new InMemoryRecoveryHistory([custom, failed]),
            new FakeRecoveryBackend());

        var result = await service.FindLatestUndoableAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ResetDefaultsAsync_AwaitsSafetyBackupBeforeSaving()
    {
        var backend = new FakeRecoveryBackend(pauseBackup: true);
        var service = new RecoveryService(new InMemoryRecoveryHistory([]), backend);

        var resetTask = service.ResetDefaultsAsync();

        Assert.Equal(["backup:before-reset-defaults"], backend.Calls);
        Assert.False(resetTask.IsCompleted);

        backend.CompleteBackup();
        await resetTask;

        Assert.Equal(["backup:before-reset-defaults", "save-defaults"], backend.Calls);
    }

    private static SwitchHistoryEntry ModeSwitch(string previousMode, string targetMode) =>
        new()
        {
            PreviousMode = previousMode,
            TargetMode = targetMode,
            Succeeded = true,
            OperationKind = "mode-switch"
        };

    private sealed class InMemoryRecoveryHistory(IReadOnlyList<SwitchHistoryEntry> entries)
        : IRecoveryHistory
    {
        public Task<IReadOnlyList<SwitchHistoryEntry>> GetRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SwitchHistoryEntry>>([.. entries.Take(maximumCount)]);

        public Task RecordAsync(
            SwitchHistoryEntry entry,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeRecoveryBackend(bool pauseBackup = false) : IRecoveryBackend
    {
        private readonly TaskCompletionSource _backupCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = [];

        public Task CreateSafetyBackupAsync(string reason, CancellationToken token)
        {
            Calls.Add($"backup:{reason}");
            return pauseBackup ? _backupCompletion.Task : Task.CompletedTask;
        }

        public Task SaveDefaultSettingsAsync(CancellationToken token)
        {
            Calls.Add("save-defaults");
            return Task.CompletedTask;
        }

        public void CompleteBackup() => _backupCompletion.TrySetResult();
    }
}
