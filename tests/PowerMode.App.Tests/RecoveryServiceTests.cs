using System.Text.Json;
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
    public async Task HistoryStore_RecordAsync_SameIdAppendsOnlyOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"powermode-recovery-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "switch-history.jsonl");
        var store = new HistoryStore(path);
        var entry = ModeSwitch("balanced", "high");

        try
        {
            await store.RecordAsync(entry);
            await store.RecordAsync(entry);

            Assert.Single(await store.GetRecentAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
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

        var resetTask = service.ResetDefaultsAsync(_ => Task.CompletedTask);

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
            (_, _) => Task.FromException<ConfigurationBackupInfo>(
                new IOException("backup failed")),
            _ => saved = true,
            (_, _) => Task.CompletedTask);
        var service = new RecoveryService(history, backend);

        var result = await service.ResetDefaultsAsync(_ => Task.CompletedTask);

        Assert.False(result.MutationSucceeded);
        Assert.Contains("backup failed", result.Error);
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
                return Task.FromResult(new ConfigurationBackupInfo(
                    "safety.json",
                    "safety.json",
                    DateTimeOffset.UtcNow,
                    10,
                    "ABC"));
            },
            settings =>
            {
                calls.Add("save-defaults");
                saved = settings;
            },
            (_, _) => Task.CompletedTask);
        var service = new RecoveryService(new InMemoryRecoveryHistory([]), backend);

        var result = await service.ResetDefaultsAsync(_ => Task.CompletedTask);

        Assert.True(result.Succeeded);
        Assert.Equal(["backup:before-reset-defaults", "save-defaults"], calls);
        Assert.NotNull(saved);
        Assert.Equal(ExperienceMode.Simple, saved.ExperienceMode);
        Assert.Equal("balanced", saved.LastMode);
    }

    [Fact]
    public async Task ResetDefaultsAsync_CancellationAfterSaveUsesNonCancelledStrictReload()
    {
        using var cancellation = new CancellationTokenSource();
        var history = new InMemoryRecoveryHistory([]);
        var backend = new FakeRecoveryBackend(afterSave: cancellation.Cancel);
        var service = new RecoveryService(history, backend);
        var reloadCount = 0;

        var result = await service.ResetDefaultsAsync(
            token =>
            {
                reloadCount++;
                Assert.False(token.IsCancellationRequested);
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.True(result.Succeeded);
        Assert.Equal(1, reloadCount);
        Assert.Single(history.RecordedEntries);
    }

    [Fact]
    public async Task ResetDefaultsAsync_StrictReloadFailureRollsBackAndReloadsSafetyBackup()
    {
        var history = new InMemoryRecoveryHistory([]);
        var backend = new FakeRecoveryBackend();
        var service = new RecoveryService(history, backend);
        var reloadCount = 0;

        var result = await service.ResetDefaultsAsync(
            _ =>
            {
                reloadCount++;
                return reloadCount == 1
                    ? Task.FromException(new JsonException("invalid saved settings"))
                    : Task.CompletedTask;
            });

        Assert.False(result.MutationSucceeded);
        Assert.False(result.AuditSucceeded);
        Assert.Equal(true, result.RollbackSucceeded);
        Assert.Contains("invalid saved settings", result.Error);
        Assert.Equal(
            [
                "backup:before-reset-defaults",
                "save-defaults",
                $"restore:{backend.SafetyBackup.Path}"
            ],
            backend.Calls);
        Assert.Equal(2, reloadCount);
        Assert.Empty(history.RecordedEntries);
    }

    [Fact]
    public async Task ResetDefaultsAsync_RollbackFailureReportsBothErrorsAndDoesNotAudit()
    {
        var history = new InMemoryRecoveryHistory([]);
        var backend = new FakeRecoveryBackend(
            restoreError: new IOException("rollback unavailable"));
        var service = new RecoveryService(history, backend);

        var result = await service.ResetDefaultsAsync(
            _ => Task.FromException(new JsonException("invalid saved settings")));

        Assert.False(result.MutationSucceeded);
        Assert.False(result.AuditSucceeded);
        Assert.Equal(false, result.RollbackSucceeded);
        Assert.Contains("invalid saved settings", result.Error);
        Assert.Contains("rollback unavailable", result.Error);
        Assert.Equal(
            [
                "backup:before-reset-defaults",
                "save-defaults",
                $"restore:{backend.SafetyBackup.Path}"
            ],
            backend.Calls);
        Assert.Empty(history.RecordedEntries);
    }

    [Fact]
    public async Task ResetDefaultsAsync_AuditSuccessAllowsNextUserIntent()
    {
        var history = new InMemoryRecoveryHistory([]);
        var backend = new FakeRecoveryBackend();
        var service = new RecoveryService(history, backend);
        var reloadCount = 0;

        var first = await service.ResetDefaultsAsync(_ =>
        {
            reloadCount++;
            return Task.CompletedTask;
        });
        var second = await service.ResetDefaultsAsync(_ =>
        {
            reloadCount++;
            return Task.CompletedTask;
        });

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(
            [
                "backup:before-reset-defaults",
                "save-defaults",
                "backup:before-reset-defaults",
                "save-defaults"
            ],
            backend.Calls);
        Assert.Equal(2, reloadCount);
        Assert.Equal(2, history.RecordedEntries.Count);
        Assert.NotEqual(history.RecordedEntries[0].Id, history.RecordedEntries[1].Id);
    }

    [Fact]
    public async Task ResetDefaultsAsync_AuditFailureRetryDoesNotRepeatMutation()
    {
        var history = new FailOnceRecoveryHistory([]);
        var backend = new FakeRecoveryBackend();
        var service = new RecoveryService(history, backend);
        var reloadCount = 0;

        var first = await service.ResetDefaultsAsync(_ =>
        {
            reloadCount++;
            return Task.CompletedTask;
        });
        var second = await service.ResetDefaultsAsync(_ =>
        {
            reloadCount++;
            return Task.CompletedTask;
        });
        var third = await service.ResetDefaultsAsync(_ =>
        {
            reloadCount++;
            return Task.CompletedTask;
        });

        Assert.True(first.MutationSucceeded);
        Assert.False(first.AuditSucceeded);
        Assert.True(second.Succeeded);
        Assert.True(third.Succeeded);
        Assert.Equal(
            [
                "backup:before-reset-defaults",
                "save-defaults",
                "backup:before-reset-defaults",
                "save-defaults"
            ],
            backend.Calls);
        Assert.Equal(2, reloadCount);
        Assert.Equal(history.AttemptedIds[0], history.AttemptedIds[1]);
        Assert.NotEqual(history.AttemptedIds[1], history.AttemptedIds[2]);
        Assert.Equal(2, history.RecordedEntries.Count);
        Assert.All(
            history.RecordedEntries,
            entry => Assert.Equal("configuration-reset", entry.OperationKind));
    }

    [Fact]
    public async Task RestoreConfigurationAsync_AuditSuccessAllowsNextUserIntent()
    {
        var history = new InMemoryRecoveryHistory([]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());
        var restoreCount = 0;
        var reloadCount = 0;
        var restoreResult = new ConfigurationRestoreResult(
            true,
            "settings.json",
            "settings.backup.json",
            null,
            null);

        Task<ConfigurationRestoreResult> Restore(CancellationToken _)
        {
            restoreCount++;
            return Task.FromResult(restoreResult);
        }

        Task Reload(CancellationToken _)
        {
            reloadCount++;
            return Task.CompletedTask;
        }

        var first = await service.RestoreConfigurationAsync(
            "settings.backup.json",
            Restore,
            Reload,
            (_, _) => Task.CompletedTask);
        var second = await service.RestoreConfigurationAsync(
            "settings.backup.json",
            Restore,
            Reload,
            (_, _) => Task.CompletedTask);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(2, restoreCount);
        Assert.Equal(2, reloadCount);
        Assert.Equal(2, history.RecordedEntries.Count);
        Assert.NotEqual(history.RecordedEntries[0].Id, history.RecordedEntries[1].Id);
    }

    [Fact]
    public async Task RestoreConfigurationAsync_AuditFailureRetryDoesNotRepeatMutation()
    {
        var history = new FailOnceRecoveryHistory([]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());
        var restoreCount = 0;
        var reloadCount = 0;
        var restoreResult = new ConfigurationRestoreResult(
            true,
            "settings.json",
            "settings.backup.json",
            null,
            null);

        Task<ConfigurationRestoreResult> Restore(CancellationToken _)
        {
            restoreCount++;
            return Task.FromResult(restoreResult);
        }

        Task Reload(CancellationToken _)
        {
            reloadCount++;
            return Task.CompletedTask;
        }

        var first = await service.RestoreConfigurationAsync(
            "settings.backup.json",
            Restore,
            Reload,
            (_, _) => Task.CompletedTask);
        var second = await service.RestoreConfigurationAsync(
            "settings.backup.json",
            Restore,
            Reload,
            (_, _) => Task.CompletedTask);
        var third = await service.RestoreConfigurationAsync(
            "settings.backup.json",
            Restore,
            Reload,
            (_, _) => Task.CompletedTask);

        Assert.True(first.MutationSucceeded);
        Assert.False(first.AuditSucceeded);
        Assert.True(second.Succeeded);
        Assert.True(third.Succeeded);
        Assert.Equal(2, restoreCount);
        Assert.Equal(2, reloadCount);
        Assert.Equal(history.AttemptedIds[0], history.AttemptedIds[1]);
        Assert.NotEqual(history.AttemptedIds[1], history.AttemptedIds[2]);
        Assert.Equal(2, history.RecordedEntries.Count);
        Assert.All(
            history.RecordedEntries,
            entry =>
            {
                Assert.Equal("configuration-restore", entry.OperationKind);
                Assert.Equal("settings.backup.json", entry.TargetMode);
            });
    }

    [Fact]
    public async Task RestoreConfigurationAsync_StrictReloadFailureRollsBackAndDoesNotAudit()
    {
        var history = new InMemoryRecoveryHistory([]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());
        var rollbackCount = 0;
        var restoreResult = new ConfigurationRestoreResult(
            true,
            "settings.json",
            "settings.backup.json",
            new ConfigurationBackupInfo(
                "safety.json",
                "safety.json",
                DateTimeOffset.UtcNow,
                10,
                "ABC"),
            null);

        var result = await service.RestoreConfigurationAsync(
            "settings.backup.json",
            _ => Task.FromResult(restoreResult),
            _ => Task.FromException(new JsonException("strict reload failed")),
            (_, _) =>
            {
                rollbackCount++;
                return Task.CompletedTask;
            });

        Assert.False(result.MutationSucceeded);
        Assert.False(result.AuditSucceeded);
        Assert.Contains("strict reload failed", result.Error);
        Assert.Equal(1, rollbackCount);
        Assert.Empty(history.RecordedEntries);
    }

    [Fact]
    public async Task RestoreConfigurationAsync_CancellationDuringStrictReloadStillRollsBack()
    {
        var history = new InMemoryRecoveryHistory([]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());
        using var cancellation = new CancellationTokenSource();
        var rollbackCount = 0;
        var restoreResult = new ConfigurationRestoreResult(
            true,
            "settings.json",
            "settings.backup.json",
            new ConfigurationBackupInfo(
                "safety.json",
                "safety.json",
                DateTimeOffset.UtcNow,
                10,
                "ABC"),
            null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RestoreConfigurationAsync(
                "settings.backup.json",
                _ => Task.FromResult(restoreResult),
                token =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                (_, rollbackToken) =>
                {
                    Assert.False(rollbackToken.IsCancellationRequested);
                    rollbackCount++;
                    return Task.CompletedTask;
                },
                cancellation.Token));

        Assert.Equal(1, rollbackCount);
        Assert.Empty(history.RecordedEntries);
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

        Assert.True(result.MutationSucceeded);
        Assert.True(result.AuditSucceeded);
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
    public async Task UndoLatestAsync_ConcurrentCallsExecuteMutationOnce()
    {
        var original = ModeSwitch("balanced", "high");
        var history = new InMemoryRecoveryHistory([original]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());
        var mutationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;

        Task<bool> ExecuteAsync(string mode, CancellationToken token)
        {
            Interlocked.Increment(ref executionCount);
            mutationEntered.TrySetResult();
            return WaitAndSucceedAsync(releaseMutation.Task, token);
        }

        var first = service.UndoLatestAsync(ExecuteAsync, "Undo latest mode switch");
        await mutationEntered.Task;
        var second = service.UndoLatestAsync(ExecuteAsync, "Undo latest mode switch");
        Assert.False(second.IsCompleted);

        releaseMutation.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, executionCount);
        Assert.Single(history.RecordedEntries, entry => entry.OperationKind == "mode-undo");
    }

    [Fact]
    public async Task WaitForIdleAsync_WaitsForRunningRecoveryMutation()
    {
        var original = ModeSwitch("balanced", "high");
        var service = new RecoveryService(
            new InMemoryRecoveryHistory([original]),
            new FakeRecoveryBackend());
        var mutationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var undo = service.UndoLatestAsync(
            async (_, token) =>
            {
                mutationEntered.TrySetResult();
                await releaseMutation.Task.WaitAsync(token);
                return true;
            },
            "Undo latest mode switch");
        await mutationEntered.Task;

        var idle = service.WaitForIdleAsync();

        Assert.False(idle.IsCompleted);
        releaseMutation.TrySetResult();
        await undo;
        await idle;
    }

    [Fact]
    public void RecoveryBackupSelector_CurrentHashFailureReturnsErrorWithoutCandidate()
    {
        var backup = new ConfigurationBackupInfo(
            "backup.json",
            "backup.json",
            DateTimeOffset.UtcNow,
            10,
            "ABC");

        var result = RecoveryBackupSelector.FindLatestDistinct(
            [backup],
            "settings.json",
            _ => throw new IOException("settings locked"));

        Assert.Null(result.Backup);
        Assert.Contains("settings locked", result.Error);
    }

    [Fact]
    public async Task UndoLatestAsync_ExecuteReturnsFalse_DoesNotRecordUndo()
    {
        var history = new InMemoryRecoveryHistory([ModeSwitch("balanced", "high")]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());

        var result = await service.UndoLatestAsync(
            (_, _) => Task.FromResult(false),
            "Undo latest mode switch");

        Assert.False(result.MutationSucceeded);
        Assert.False(result.AuditSucceeded);
        Assert.Empty(history.RecordedEntries);
    }

    [Fact]
    public async Task UndoLatestAsync_ExecuteThrows_DoesNotRecordUndo()
    {
        var history = new InMemoryRecoveryHistory([ModeSwitch("balanced", "high")]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());

        var result = await service.UndoLatestAsync(
            (_, _) => Task.FromException<bool>(new InvalidOperationException("mode failed")),
            "Undo latest mode switch");

        Assert.False(result.MutationSucceeded);
        Assert.False(result.AuditSucceeded);
        Assert.Contains("mode failed", result.Error);
        Assert.Empty(history.RecordedEntries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndoLatestAsync_AuditAppendFailure_RetryDoesNotRepeatMutation(
        bool failAfterWrite)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"powermode-recovery-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "switch-history.jsonl");
        Directory.CreateDirectory(directory);
        var original = ModeSwitch("balanced", "high");
        await new HistoryStore(path).RecordAsync(original);
        var appendAttempts = 0;
        var history = new HistoryStore(
            path,
            async (destination, contents, token) =>
            {
                appendAttempts++;
                if (failAfterWrite)
                    await File.AppendAllTextAsync(destination, contents, token);
                if (appendAttempts == 1)
                    throw new IOException(failAfterWrite ? "after write" : "before write");
                if (!failAfterWrite)
                    await File.AppendAllTextAsync(destination, contents, token);
            });
        var service = new RecoveryService(history, new FakeRecoveryBackend());
        var executionCount = 0;

        try
        {
            var first = await service.UndoLatestAsync(
                (_, _) =>
                {
                    executionCount++;
                    return Task.FromResult(true);
                },
                "Undo latest mode switch");
            var second = await service.UndoLatestAsync(
                (_, _) =>
                {
                    executionCount++;
                    return Task.FromResult(true);
                },
                "Undo latest mode switch");

            Assert.True(first.MutationSucceeded);
            Assert.False(first.AuditSucceeded);
            Assert.True(second.MutationSucceeded);
            Assert.True(second.AuditSucceeded);
            Assert.Equal(1, executionCount);
            Assert.Single(
                await history.GetRecentAsync(),
                entry => entry.OperationKind == "mode-undo");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    private static async Task<bool> WaitAndSucceedAsync(Task release, CancellationToken token)
    {
        await release.WaitAsync(token);
        return true;
    }

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
            var combined = RecordedEntries
                .Concat(entries)
                .Take(maximumCount)
                .ToArray();
            return Task.FromResult<IReadOnlyList<SwitchHistoryEntry>>(combined);
        }

        public Task RecordAsync(
            SwitchHistoryEntry entry,
            CancellationToken cancellationToken = default)
        {
            if (RecordedEntries.All(existing => existing.Id != entry.Id))
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

    private sealed class FailOnceRecoveryHistory(IReadOnlyList<SwitchHistoryEntry> entries)
        : IRecoveryHistory
    {
        private bool _failed;
        public List<Guid> AttemptedIds { get; } = [];
        public List<SwitchHistoryEntry> RecordedEntries { get; } = [];

        public Task<IReadOnlyList<SwitchHistoryEntry>> GetRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SwitchHistoryEntry>>(
                [.. RecordedEntries.Concat(entries).Take(maximumCount)]);

        public Task RecordAsync(
            SwitchHistoryEntry entry,
            CancellationToken cancellationToken = default)
        {
            AttemptedIds.Add(entry.Id);
            if (!_failed)
            {
                _failed = true;
                throw new IOException("audit unavailable");
            }
            if (RecordedEntries.All(existing => existing.Id != entry.Id))
                RecordedEntries.Add(entry);
            return Task.CompletedTask;
        }
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

    private sealed class FakeRecoveryBackend(
        bool pauseBackup = false,
        Action? afterSave = null,
        Exception? restoreError = null) : IRecoveryBackend
    {
        private readonly TaskCompletionSource<ConfigurationBackupInfo> _backupCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = [];
        public ConfigurationBackupInfo SafetyBackup { get; } = new(
            "reset-safety.json",
            "reset-safety.json",
            DateTimeOffset.UtcNow,
            10,
            "ABC");

        public Task<ConfigurationBackupInfo> CreateSafetyBackupAsync(
            string reason,
            CancellationToken token)
        {
            Calls.Add($"backup:{reason}");
            return pauseBackup
                ? _backupCompletion.Task
                : Task.FromResult(SafetyBackup);
        }

        public Task SaveDefaultSettingsAsync(CancellationToken token)
        {
            Calls.Add("save-defaults");
            afterSave?.Invoke();
            return Task.CompletedTask;
        }

        public Task RestoreSafetyBackupAsync(
            ConfigurationBackupInfo backup,
            CancellationToken token)
        {
            Calls.Add($"restore:{backup.Path}");
            return restoreError is null
                ? Task.CompletedTask
                : Task.FromException(restoreError);
        }

        public void CompleteBackup() => _backupCompletion.TrySetResult(SafetyBackup);
    }
}
