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
    public async Task FindLatestUndoableAsync_SkipsUnknownAndCustomPreviousModes()
    {
        var unknownPrevious = ModeSwitch("unknown", "high");
        unknownPrevious.Timestamp = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        var customPrevious = ModeSwitch("profile:Quiet", "balanced");
        customPrevious.Timestamp = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var eligible = ModeSwitch("remote", "saver");
        eligible.Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new RecoveryService(
            new InMemoryRecoveryHistory([unknownPrevious, customPrevious, eligible]),
            new FakeRecoveryBackend());

        var result = await service.FindLatestUndoableAsync();

        Assert.Equal(eligible.Id, result?.Id);
    }

    [Fact]
    public async Task FindLatestUndoableAsync_SelectsNewestTimestampFromOldestFirstHistory()
    {
        var oldest = ModeSwitch("balanced", "high");
        oldest.Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newest = ModeSwitch("high", "remote");
        newest.Timestamp = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var service = new RecoveryService(
            new InMemoryRecoveryHistory([oldest, newest]),
            new FakeRecoveryBackend());

        var result = await service.FindLatestUndoableAsync();

        Assert.Equal(newest.Id, result?.Id);
    }

    [Fact]
    public async Task FindLatestUndoableAsync_PreservesHistoryOrderWhenTimestampsMatch()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = ModeSwitch("balanced", "high");
        first.Timestamp = timestamp;
        var second = ModeSwitch("high", "remote");
        second.Timestamp = timestamp;
        var service = new RecoveryService(
            new InMemoryRecoveryHistory([first, second]),
            new FakeRecoveryBackend());

        var result = await service.FindLatestUndoableAsync();

        Assert.Equal(first.Id, result?.Id);
    }

    [Fact]
    public async Task FindLatestUndoableAsync_RequestsBoundedRecoveryHistoryWindow()
    {
        var history = new InMemoryRecoveryHistory([]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());

        await service.FindLatestUndoableAsync();

        Assert.Equal(RecoveryService.MaximumRecoveryHistoryEntries, history.LastMaximumCount);
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

    [Fact]
    public async Task ResetDefaultsAsync_ProductionBackupFailure_DoesNotSaveDefaults()
    {
        var saved = false;
        var history = new InMemoryRecoveryHistory([]);
        var backend = new ProductionRecoveryBackend(
            (_, _) => Task.FromException(new IOException("backup failed")),
            _ => saved = true);
        var service = new RecoveryService(history, backend);

        await Assert.ThrowsAsync<IOException>(() => service.ResetDefaultsAsync());

        Assert.False(saved);
        Assert.Empty(history.RecordedEntries);
    }

    [Fact]
    public async Task ResetDefaultsAsync_ProductionBackendSavesFreshDefaultsAfterSafetyBackup()
    {
        var calls = new List<string>();
        PowerModeSettings? saved = null;
        var backend = new ProductionRecoveryBackend(
            (reason, _) =>
            {
                calls.Add($"backup:{reason}");
                return Task.CompletedTask;
            },
            settings =>
            {
                calls.Add("save-defaults");
                saved = settings;
            });
        var service = new RecoveryService(new InMemoryRecoveryHistory([]), backend);

        await service.ResetDefaultsAsync();

        Assert.Equal(["backup:before-reset-defaults", "save-defaults"], calls);
        Assert.NotNull(saved);
        Assert.Equal(ExperienceMode.Simple, saved.ExperienceMode);
        Assert.Equal("balanced", saved.LastMode);
    }

    [Fact]
    public async Task ResetDefaultsAsync_DoesNotRecordResetBeforeCallerAppliesSettings()
    {
        var history = new InMemoryRecoveryHistory([]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());

        await service.ResetDefaultsAsync();

        Assert.Empty(history.RecordedEntries);
    }

    [Fact]
    public async Task RecordConfigurationResetAsync_UsesExplicitOperationKind()
    {
        var history = new InMemoryRecoveryHistory([]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());

        await service.RecordConfigurationResetAsync();

        var entry = Assert.Single(history.RecordedEntries);
        Assert.Equal("configuration-reset", entry.OperationKind);
        Assert.True(entry.Succeeded);
        Assert.Equal("recovery-center", entry.Trigger);
    }

    [Fact]
    public async Task RecordConfigurationRestoreAsync_UsesExplicitOperationKind()
    {
        var history = new InMemoryRecoveryHistory([]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());
        var result = new ConfigurationRestoreResult(
            true,
            "settings.json",
            "settings.backup.json",
            null,
            null);

        await service.RecordConfigurationRestoreAsync(result);

        var entry = Assert.Single(history.RecordedEntries);
        Assert.Equal("configuration-restore", entry.OperationKind);
        Assert.True(entry.Succeeded);
        Assert.Equal("settings.backup.json", entry.TargetMode);
        Assert.Equal("recovery-center", entry.Trigger);
    }

    [Fact]
    public async Task UndoLatestAsync_ExecutesPreviousModeAndRecordsExactlyOneLinkedUndo()
    {
        var original = ModeSwitch("balanced", "high");
        var history = new InMemoryRecoveryHistory([original]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());
        var executedModes = new List<string>();

        var result = await service.UndoLatestAsync(
            (mode, _) =>
            {
                executedModes.Add(mode);
                return Task.FromResult(true);
            },
            "Undo latest mode switch");

        Assert.True(result);
        Assert.Equal(["balanced"], executedModes);
        var undo = Assert.Single(history.RecordedEntries);
        Assert.Equal("mode-undo", undo.OperationKind);
        Assert.True(undo.IsUndo);
        Assert.True(undo.Succeeded);
        Assert.Equal(original.Id, undo.RelatedOperationId);
        Assert.Equal(original.TargetMode, undo.PreviousMode);
        Assert.Equal(original.PreviousMode, undo.TargetMode);
        Assert.Equal("recovery-center", undo.Trigger);
    }

    [Fact]
    public async Task UndoLatestAsync_InvokesModePipelineOnCallerSynchronizationContext()
    {
        var callerContext = new InlineSynchronizationContext();
        var originalContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(callerContext);
        try
        {
            var original = ModeSwitch("balanced", "high");
            var service = new RecoveryService(
                new AsynchronousRecoveryHistory([original]),
                new FakeRecoveryBackend());

            await service.UndoLatestAsync(
                (_, _) =>
                {
                    Assert.Same(callerContext, SynchronizationContext.Current);
                    return Task.FromResult(true);
                },
                "Undo latest mode switch");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }
    }

    [Theory]
    [InlineData(null, "balanced", "high", "balanced → high")]
    [InlineData("", "balanced", "high", "balanced → high")]
    [InlineData("mode-switch", "balanced", "high", "balanced → high")]
    [InlineData("mode-undo", "high", "balanced", "Undo · high → balanced")]
    [InlineData("configuration-restore", "", "", "Configuration restored")]
    [InlineData("configuration-reset", "", "", "Defaults reset")]
    [InlineData("future-operation", "", "", "future-operation")]
    public void RecoveryOperationFormatter_FormatsOldKnownAndUnknownKindsSafely(
        string? operationKind,
        string previousMode,
        string targetMode,
        string expected)
    {
        var entry = new SwitchHistoryEntry
        {
            OperationKind = operationKind!,
            PreviousMode = previousMode,
            TargetMode = targetMode
        };

        var result = RecoveryOperationFormatter.FormatOperation(entry, isChinese: false);

        Assert.Equal(expected, result);
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
        public int? LastMaximumCount { get; private set; }
        public List<SwitchHistoryEntry> RecordedEntries { get; } = [];

        public Task<IReadOnlyList<SwitchHistoryEntry>> GetRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            LastMaximumCount = maximumCount;
            return Task.FromResult<IReadOnlyList<SwitchHistoryEntry>>([.. entries.Take(maximumCount)]);
        }

        public Task RecordAsync(
            SwitchHistoryEntry entry,
            CancellationToken cancellationToken = default)
        {
            RecordedEntries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class AsynchronousRecoveryHistory(IReadOnlyList<SwitchHistoryEntry> entries)
        : IRecoveryHistory
    {
        public async Task<IReadOnlyList<SwitchHistoryEntry>> GetRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            return [.. entries.Take(maximumCount)];
        }

        public Task RecordAsync(
            SwitchHistoryEntry entry,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                callback(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
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
