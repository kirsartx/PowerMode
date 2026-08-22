using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ModeSwitchCoordinatorTests
{
    [Fact]
    public async Task TrySwitchAsync_ZeroExitWithCriticalMismatch_RollsBack()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        var before = backend.BalancedState();
        backend.Reads.Enqueue(backend.ReadResult(backend.MismatchedRemoteState()));
        backend.Restores.Enqueue(backend.Operation("restore", BackendOperationOutcome.Succeeded, before));
        backend.Reads.Enqueue(backend.ReadResult(before));
        var journal = new RecordingLastOperationStore();
        var coordinator = TestCoordinator.Create(backend, journal);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.Equal(ModeSwitchOutcome.Failed, result!.Outcome);
        Assert.True(result.Rollback!.Attempted);
        Assert.True(result.Rollback.Succeeded);
        Assert.Equal(
            [LastOperationStatus.Prepared, LastOperationStatus.Applying,
             LastOperationStatus.RolledBack],
            journal.Writes.Select(record => record.Status));
    }

    [Fact]
    public async Task TrySwitchAsync_OptionalBrightnessMismatchReturnsPartialWithoutRollback()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        backend.Reads.Enqueue(backend.ReadResult(backend.RemoteState(cpu: 31, brightness: 47)));
        var journal = new RecordingLastOperationStore();
        var history = new RecordingRecoveryHistory();
        var coordinator = TestCoordinator.Create(backend, journal, history);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.Equal(ModeSwitchOutcome.Partial, result!.Outcome);
        Assert.Null(result.Rollback);
        Assert.Equal(LastOperationStatus.Verified, journal.Writes[^1].Status);
        Assert.Equal(0, backend.RestoreCount);
        Assert.Equal(Requests.Remote().RuleId, history.Entries.Single().RuleId);
        Assert.Equal(Requests.Remote().RuleName, history.Entries.Single().RuleName);
    }

    [Fact]
    public async Task TrySwitchAsync_CriticalFailureWithSuccessfulRestoreReturnsFailed()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31, applyOutcome: BackendOperationOutcome.Failed);
        var before = backend.BalancedState();
        backend.Reads.Enqueue(backend.ReadResult(backend.MismatchedRemoteState()));
        backend.Restores.Enqueue(backend.Operation("restore", BackendOperationOutcome.Succeeded, before));
        backend.Reads.Enqueue(backend.ReadResult(before));
        var coordinator = TestCoordinator.Create(backend);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.Equal(ModeSwitchOutcome.Failed, result!.Outcome);
        Assert.True(result.Rollback!.Succeeded);
    }

    [Fact]
    public async Task TrySwitchAsync_FailedRestoreReturnsRollbackFailed()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        var before = backend.BalancedState();
        backend.Reads.Enqueue(backend.ReadResult(backend.MismatchedRemoteState()));
        backend.Restores.Enqueue(backend.Operation("restore", BackendOperationOutcome.Failed, before));
        var journal = new RecordingLastOperationStore();
        var coordinator = TestCoordinator.Create(backend, journal);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());
        Assert.Equal(LastOperationStatus.Uncertain, journal.Current!.Status);
        journal.Current = journal.Current with { Status = LastOperationStatus.Verified };

        Assert.Equal(ModeSwitchOutcome.RollbackFailed, result!.Outcome);
        Assert.False(result.Rollback!.Succeeded);
        Assert.Null(await coordinator.TrySwitchAsync(Requests.Saver()));
    }

    [Fact]
    public async Task TrySwitchAsync_TimeoutWithMismatchReturnsTimedOutAfterRollback()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31, applyOutcome: BackendOperationOutcome.TimedOut);
        var before = backend.BalancedState();
        backend.Reads.Enqueue(backend.ReadResult(backend.MismatchedRemoteState()));
        backend.Restores.Enqueue(backend.Operation("restore", BackendOperationOutcome.Succeeded, before));
        backend.Reads.Enqueue(backend.ReadResult(before));
        var coordinator = TestCoordinator.Create(backend);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.Equal(ModeSwitchOutcome.TimedOut, result!.Outcome);
        Assert.True(result.Rollback!.Succeeded);
    }

    [Fact]
    public async Task TrySwitchAsync_TimeoutWithMatchingRealityReturnsSucceeded()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31, applyOutcome: BackendOperationOutcome.TimedOut);
        backend.Reads.Enqueue(backend.ReadResult(backend.RemoteState(cpu: 31)));
        var coordinator = TestCoordinator.Create(backend);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.Equal(ModeSwitchOutcome.Succeeded, result!.Outcome);
        Assert.Null(result.Rollback);
    }

    [Fact]
    public async Task TrySwitchAsync_OnlyDeclaredExpectationsAreVerified()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        backend.Reads.Enqueue(backend.ReadResult(backend.RemoteState(cpu: 31, cpuMinimum: 99)));
        var coordinator = TestCoordinator.Create(backend);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.Equal(ModeSwitchOutcome.Succeeded, result!.Outcome);
    }

    [Fact]
    public async Task TrySwitchAsync_ConcurrentRequestIsRejectedNotQueued()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31, blockApply: true);
        var coordinator = TestCoordinator.Create(backend);
        var first = coordinator.TrySwitchAsync(Requests.Remote());
        await backend.ApplyStarted.Task;

        Assert.Null(await coordinator.TrySwitchAsync(Requests.Saver()));
        backend.CompleteApply();
        await first;
        Assert.Equal(1, backend.ApplyCount);
    }

    [Theory]
    [InlineData((int)LastOperationStatus.Prepared)]
    [InlineData((int)LastOperationStatus.Applying)]
    [InlineData((int)LastOperationStatus.Uncertain)]
    public async Task TrySwitchAsync_ColdStartUnresolvedJournalBlocksBeforeStateRead(
        int statusValue)
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        var journal = new RecordingLastOperationStore
        {
            Current = JournalRecord((LastOperationStatus)statusValue, backend.BalancedState())
        };
        var coordinator = TestCoordinator.Create(backend, journal);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.Null(result);
        Assert.Equal(0, backend.ReadCount);
        Assert.Equal(0, backend.ApplyCount);
        Assert.Empty(journal.Writes);
    }

    [Fact]
    public async Task TrySwitchAsync_JournalReadErrorLatchesPersistentRecoveryBeforeStateRead()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        var journal = new RecordingLastOperationStore { ReadError = "journal unreadable" };
        var coordinator = TestCoordinator.Create(backend, journal);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.NotNull(result);
        Assert.True(result.RequiresRecovery);
        Assert.Equal("journal unreadable", result.JournalError);
        var presentation = ModeSwitchResultPresentationPolicy.Create(
            result,
            ExperienceMode.Simple,
            isChinese: false);
        Assert.True(presentation.IsPersistent);
        Assert.Equal(
            ModeSwitchPresentationAction.OpenRecoveryCenter,
            presentation.Action);
        Assert.Equal(0, backend.ReadCount);
        Assert.Equal(0, backend.ApplyCount);
        Assert.Null(await coordinator.TrySwitchAsync(Requests.Saver()));
    }

    [Fact]
    public async Task TrySwitchAsync_PreMutationCancellationReturnsCancelled()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var journal = new RecordingLastOperationStore();
        var coordinator = TestCoordinator.Create(backend, journal);

        var result = await coordinator.TrySwitchAsync(Requests.Remote(), cancellation.Token);

        Assert.Equal(ModeSwitchOutcome.Cancelled, result!.Outcome);
        Assert.Equal(0, backend.ApplyCount);
        Assert.Empty(journal.Writes);
    }

    [Fact]
    public async Task TrySwitchAsync_HistoryDisabledStillWritesEveryJournalPhase()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        var journal = new RecordingLastOperationStore();
        var history = new RecordingRecoveryHistory();
        var coordinator = TestCoordinator.Create(backend, journal, history);
        var request = Requests.Remote() with { RecordHistory = false };

        var result = await coordinator.TrySwitchAsync(request);

        Assert.Equal(ModeSwitchOutcome.Succeeded, result!.Outcome);
        Assert.Equal(
            [LastOperationStatus.Prepared, LastOperationStatus.Applying,
             LastOperationStatus.Verified],
            journal.Writes.Select(record => record.Status));
        Assert.Empty(history.Entries);
    }

    [Fact]
    public async Task TrySwitchAsync_PreparedWriteFailureNeverMutatesBackend()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        var journal = new RecordingLastOperationStore
        {
            WriteResults = new Queue<LastOperationWriteResult>([
                new(false, false, "journal unavailable")])
        };
        var coordinator = TestCoordinator.Create(backend, journal);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.Equal(ModeSwitchOutcome.Failed, result!.Outcome);
        Assert.Equal(0, backend.ApplyCount);
    }

    [Fact]
    public async Task TrySwitchAsync_PreparedConflictRequiresRecoveryAndLatchesGate()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        var journal = new RecordingLastOperationStore
        {
            WriteResults = new Queue<LastOperationWriteResult>([
                new(false, true, "unresolved operation appeared")])
        };
        var coordinator = TestCoordinator.Create(backend, journal);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.True(result!.RequiresRecovery);
        Assert.Contains("unresolved", result.JournalError!);
        Assert.Equal(0, backend.ApplyCount);
        Assert.Null(await coordinator.TrySwitchAsync(Requests.Saver()));
    }

    [Fact]
    public async Task TrySwitchAsync_ApplyingWriteFailureLatchesRecoveryAndNeverMutatesBackend()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        var journal = new RecordingLastOperationStore
        {
            WriteResults = new Queue<LastOperationWriteResult>([
                new(true, false, null),
                new(false, false, "journal unavailable")])
        };
        var coordinator = TestCoordinator.Create(backend, journal);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.Equal(ModeSwitchOutcome.Failed, result!.Outcome);
        Assert.True(result.RequiresRecovery);
        Assert.Equal("journal unavailable", result.JournalError);
        Assert.Equal(0, backend.ApplyCount);
        Assert.Equal(LastOperationStatus.Prepared, journal.Writes[^1].Status);
        Assert.Null(await coordinator.TrySwitchAsync(Requests.Saver()));
    }

    [Fact]
    public async Task TrySwitchAsync_TerminalWriteFailureRequiresRecoveryAndBlocksNewSwitches()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        var journal = new RecordingLastOperationStore
        {
            WriteResults = new Queue<LastOperationWriteResult>([
                new(true, false, null),
                new(true, false, null),
                new(false, false, "terminal journal failure")])
        };
        var coordinator = TestCoordinator.Create(backend, journal);

        var result = await coordinator.TrySwitchAsync(Requests.Remote());

        Assert.True(result!.RequiresRecovery);
        Assert.Equal(LastOperationStatus.Applying, journal.Writes[^1].Status);
        Assert.Null(await coordinator.TrySwitchAsync(Requests.Saver()));
    }

    [Fact]
    public async Task RestoreBeforeStateAsync_UnconfirmedRestoreLatchesRecovery()
    {
        var backend = new ScenarioBackend();
        var before = backend.BalancedState();
        backend.Restores.Enqueue(backend.Operation(
            "restore",
            BackendOperationOutcome.Succeeded,
            before));
        backend.Reads.Enqueue(backend.ReadResult(before with { CpuMinimumDcPercent = 99 }));
        var journal = new RecordingLastOperationStore
        {
            Current = JournalRecord(LastOperationStatus.Applying, before)
        };
        var coordinator = TestCoordinator.Create(backend, journal);

        var restore = await coordinator.RestoreBeforeStateAsync(journal.Current.OperationId);
        Assert.Equal(LastOperationStatus.Uncertain, journal.Current!.Status);
        journal.Current = journal.Current with { Status = LastOperationStatus.Verified };

        Assert.False(restore.Succeeded);
        Assert.Null(await coordinator.TrySwitchAsync(Requests.Saver()));
    }

    [Fact]
    public async Task TrySwitchAsync_CustomBoostExpectationMatchesEngineContract()
    {
        var backend = new ScenarioBackend();
        var profile = new CustomPowerProfileSnapshot(
            "Quiet", 45, 35, 5, 65, 45, 300, 120, true);
        var request = new ModeSwitchRequest(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            PowerModeTarget.ForCustom(profile),
            null,
            false,
            "custom-test",
            "custom contract");
        var after = backend.CustomState(profile);
        backend.Reads.Enqueue(backend.ReadResult(backend.BalancedState()));
        backend.Applies.Enqueue(backend.Operation(
            "apply",
            BackendOperationOutcome.Succeeded,
            after,
            request.Target.Key,
            CustomExpectations(profile)));
        backend.Reads.Enqueue(backend.ReadResult(after));

        var result = await TestCoordinator.Create(backend).TrySwitchAsync(request);

        Assert.Equal(ModeSwitchOutcome.Succeeded, result!.Outcome);
        Assert.Null(result.Rollback);
    }

    [Fact]
    public async Task VerifyLastOperationAsync_ReadsRealityAndWritesVerified()
    {
        var backend = ScenarioBackend.ForRemote(cpu: 31);
        var journal = new RecordingLastOperationStore();
        var coordinator = TestCoordinator.Create(backend, journal);
        var switchResult = await coordinator.TrySwitchAsync(Requests.Remote());

        var verification = await coordinator.VerifyLastOperationAsync(switchResult!.OperationId);

        Assert.True(verification.MatchesCriticalExpectations);
        Assert.Equal(LastOperationStatus.Verified, journal.Writes[^1].Status);
    }

    [Fact]
    public async Task RestoreSnapshotAsync_WaitsForInFlightSwitchThenJournalsAndConfirmsExactSnapshot()
    {
        var backend = new ScenarioBackend(blockApply: true);
        var launchState = backend.BalancedState() with
        {
            CpuMinimumAcPercent = 7,
            CpuMinimumDcPercent = 4,
            ProcessorBoostModeAc = 2,
            ProcessorBoostModeDc = 1,
            WifiDisabled = true
        };
        var switchedState = backend.RemoteState(31);
        backend.Reads.Enqueue(backend.ReadResult(backend.BalancedState()));
        backend.Applies.Enqueue(backend.Operation(
            "apply",
            BackendOperationOutcome.Succeeded,
            switchedState,
            PowerModeTarget.ForPreset(PowerModePreset.Remote).Key,
            RemoteExpectations(31)));
        backend.Reads.Enqueue(backend.ReadResult(switchedState));
        backend.Reads.Enqueue(backend.ReadResult(switchedState));
        backend.Restores.Enqueue(backend.Operation(
            "restore",
            BackendOperationOutcome.Succeeded,
            launchState));
        backend.Reads.Enqueue(backend.ReadResult(launchState));
        var journal = new RecordingLastOperationStore();
        var coordinator = TestCoordinator.Create(backend, journal);
        var switchTask = coordinator.TrySwitchAsync(Requests.Remote());
        await backend.ApplyStarted.Task;

        var restoreTask = coordinator.RestoreSnapshotAsync(new SnapshotRestoreRequest(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            launchState,
            "shutdown",
            "restore exact launch state"));

        Assert.False(restoreTask.IsCompleted);
        Assert.Equal(0, backend.RestoreCount);

        backend.CompleteApply();
        Assert.Equal(ModeSwitchOutcome.Succeeded, (await switchTask)!.Outcome);
        var result = await restoreTask;

        Assert.Equal(ModeSwitchOutcome.Succeeded, result!.Outcome);
        Assert.Equal(launchState, result.AfterState);
        Assert.Equal(1, backend.RestoreCount);
        Assert.Equal(
            [
                LastOperationStatus.Prepared,
                LastOperationStatus.Applying,
                LastOperationStatus.Verified,
                LastOperationStatus.Prepared,
                LastOperationStatus.Applying,
                LastOperationStatus.Verified
            ],
            journal.Writes.Select(record => record.Status));
        Assert.Equal(launchState, journal.Writes[^1].Target.Snapshot);
    }

    private static class Requests
    {
        public static ModeSwitchRequest Remote() => new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PowerModeTarget.ForPreset(PowerModePreset.Remote),
            31,
            false,
            "manual",
            "test switch",
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Remote rule");

        public static ModeSwitchRequest Saver() => Remote() with
        {
            OperationId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Target = PowerModeTarget.ForPreset(PowerModePreset.Saver),
            CpuMaximumPercent = 29
        };
    }

    private static class TestCoordinator
    {
        public static ModeSwitchCoordinator Create(
            ScenarioBackend backend,
            RecordingLastOperationStore? journal = null,
            RecordingRecoveryHistory? history = null)
        {
            journal ??= new RecordingLastOperationStore();
            history ??= new RecordingRecoveryHistory();
            var coordinator = new ModeSwitchCoordinator(
                backend,
                journal,
                history,
                new FixedTimeProvider(DateTimeOffset.Parse("2026-08-22T12:00:00Z")));
            return coordinator;
        }
    }

    private static LastOperationRecord JournalRecord(
        LastOperationStatus status,
        PowerModeState beforeState) => new(
        1,
        Guid.Parse("99999999-9999-9999-9999-999999999999"),
        "test",
        "pending operation",
        PowerModeTarget.ForPreset(PowerModePreset.Remote),
        31,
        false,
        DateTimeOffset.Parse("2026-08-22T12:00:00Z"),
        beforeState,
        status);

    private sealed class ScenarioBackend : IPowerModeBackend
    {
        private readonly bool _blockApply;
        private readonly TaskCompletionSource<bool> _applyRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Queue<PowerModeStateResult> Reads { get; } = new();
        public Queue<BackendOperationResult> Applies { get; } = new();
        public Queue<BackendOperationResult> Restores { get; } = new();
        public TaskCompletionSource<bool> ApplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ApplyCount { get; private set; }
        public int RestoreCount { get; private set; }
        public int ReadCount { get; private set; }

        public ScenarioBackend(bool blockApply = false)
        {
            _blockApply = blockApply;
        }

        public static ScenarioBackend ForRemote(
            int cpu,
            BackendOperationOutcome applyOutcome = BackendOperationOutcome.Succeeded,
            bool blockApply = false)
        {
            var backend = new ScenarioBackend(blockApply);
            backend.Reads.Enqueue(backend.ReadResult(backend.BalancedState()));
            backend.Applies.Enqueue(backend.Operation(
                "apply",
                applyOutcome,
                backend.RemoteState(cpu),
                PowerModeTarget.ForPreset(PowerModePreset.Remote).Key,
                RemoteExpectations(cpu)));
            return backend;
        }

        public PowerModeStateResult ReadState(Guid operationId = default, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Reads.Count > 0
                ? Reads.Dequeue()
                : ReadResult(RemoteState(31));
        }

        public Task<PowerModeStateResult> ReadStateAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadState(operationId, cancellationToken));

        public async Task<BackendOperationResult> ApplyAsync(
            Guid operationId,
            PowerModeTarget target,
            int? cpuMaximumPercent = null,
            bool disableWifi = false,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            ApplyStarted.TrySetResult(true);
            if (_blockApply)
                await _applyRelease.Task.WaitAsync(cancellationToken);
            var operation = Applies.Count > 0
                ? Applies.Dequeue()
                : Operation("apply", BackendOperationOutcome.Succeeded, RemoteState(cpuMaximumPercent ?? 31), target.Key, RemoteExpectations(cpuMaximumPercent ?? 31));
            return operation with { OperationId = operationId, RequestedTargetKey = target.Key };
        }

        public Task<BackendOperationResult> RestoreAsync(
            Guid operationId,
            PowerModeState snapshot,
            CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            var operation = Restores.Count > 0
                ? Restores.Dequeue()
                : Operation("restore", BackendOperationOutcome.Succeeded, snapshot);
            return Task.FromResult(operation with { OperationId = operationId });
        }

        public void CompleteApply() => _applyRelease.TrySetResult(true);

        public PowerModeStateResult ReadResult(PowerModeState state) => new(
            state,
            Operation("status", BackendOperationOutcome.Succeeded, state));

        public PowerModeState BalancedState() => State(
            Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
            PowerModePreset.Balanced,
            100,
            100,
            5,
            5,
            100,
            100,
            0,
            0);

        public PowerModeState RemoteState(
            int cpu,
            int brightness = 50,
            int cpuMinimum = 3) => State(
            Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a"),
            PowerModePreset.Remote,
            cpu,
            cpu,
            cpuMinimum,
            cpuMinimum,
            brightness,
            brightness,
            60,
            60);

        public PowerModeState MismatchedRemoteState() => RemoteState(88);

        public PowerModeState CustomState(CustomPowerProfileSnapshot profile) => State(
            Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a"),
            PowerModePreset.Remote,
            profile.CpuMaximumAcPercent,
            profile.CpuMaximumDcPercent,
            profile.CpuMinimumPercent,
            profile.CpuMinimumPercent,
            profile.BrightnessAcPercent,
            profile.BrightnessDcPercent,
            profile.DisplayTimeoutAcSeconds,
            profile.DisplayTimeoutDcSeconds,
            boostAc: profile.DisableBoost ? 0 : null,
            boostDc: profile.DisableBoost ? 0 : null);

        public PowerModeState State(
            Guid scheme,
            PowerModePreset preset,
            int cpuAc,
            int cpuDc,
            int minimumAc,
            int minimumDc,
            int brightnessAc,
            int brightnessDc,
            int? displayAc,
            int? displayDc,
            int? cpuMinimumOverride = null,
            int? boostAc = null,
            int? boostDc = null) => new(
            scheme,
            preset.ToString(),
            preset,
            PowerSourceKind.Ac,
            null,
            cpuAc,
            cpuDc,
            cpuMinimumOverride ?? minimumAc,
            minimumDc,
            boostAc,
            boostDc,
            brightnessAc,
            brightnessDc,
            displayAc,
            displayDc,
            0,
            0,
            0,
            0,
            false);

        public BackendOperationResult Operation(
            string action,
            BackendOperationOutcome outcome,
            PowerModeState? after,
            string? targetKey = null,
            IReadOnlyList<VerificationExpectation>? expectations = null) => new(
            Guid.NewGuid(),
            action,
            targetKey,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(5),
            outcome,
            null,
            after,
            Array.Empty<BackendStepResult>(),
            expectations ?? Array.Empty<VerificationExpectation>(),
            outcome == BackendOperationOutcome.Failed ? "backend failure" : null);
    }

    private sealed class RecordingLastOperationStore : ILastOperationStore
    {
        public List<LastOperationRecord> Writes { get; } = [];
        public Queue<LastOperationWriteResult> WriteResults { get; set; } = new();
        public LastOperationRecord? Current { get; set; }
        public string? ReadError { get; set; }

        public Task<LastOperationReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LastOperationReadResult(Current, ReadError));

        public Task<LastOperationWriteResult> WriteAsync(
            LastOperationRecord record,
            Guid? expectedOperationId = null,
            CancellationToken cancellationToken = default)
        {
            var result = WriteResults.Count > 0
                ? WriteResults.Dequeue()
                : new LastOperationWriteResult(true, false, null);
            if (result.Succeeded)
            {
                Current = record;
                Writes.Add(record);
            }
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingRecoveryHistory : IRecoveryHistory
    {
        public List<SwitchHistoryEntry> Entries { get; } = [];

        public Task<IReadOnlyList<SwitchHistoryEntry>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SwitchHistoryEntry>>(Entries.TakeLast(maximumCount).Reverse().ToArray());

        public Task RecordAsync(SwitchHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static IReadOnlyList<VerificationExpectation> RemoteExpectations(int cpu) =>
    [
        Expect(PowerModeStateField.ActiveSchemeId, "a1841308-3541-4fab-bc81-f71556f20b4a", true),
        Expect(PowerModeStateField.CpuMaximumAcPercent, cpu.ToString(), true),
        Expect(PowerModeStateField.CpuMaximumDcPercent, cpu.ToString(), true),
        Expect(PowerModeStateField.BrightnessAcPercent, "50", false),
        Expect(PowerModeStateField.BrightnessDcPercent, "50", false),
        Expect(PowerModeStateField.DisplayTimeoutAcSeconds, "60", true),
        Expect(PowerModeStateField.DisplayTimeoutDcSeconds, "60", true),
        Expect(PowerModeStateField.SleepTimeoutAcSeconds, "0", true),
        Expect(PowerModeStateField.SleepTimeoutDcSeconds, "0", true),
        Expect(PowerModeStateField.HibernateTimeoutAcSeconds, "0", true),
        Expect(PowerModeStateField.HibernateTimeoutDcSeconds, "0", true)
    ];

    private static IReadOnlyList<VerificationExpectation> CustomExpectations(
        CustomPowerProfileSnapshot profile) =>
    [
        Expect(PowerModeStateField.ActiveSchemeId, "a1841308-3541-4fab-bc81-f71556f20b4a", true),
        Expect(PowerModeStateField.CpuMaximumAcPercent, profile.CpuMaximumAcPercent.ToString(), true),
        Expect(PowerModeStateField.CpuMaximumDcPercent, profile.CpuMaximumDcPercent.ToString(), true),
        Expect(PowerModeStateField.CpuMinimumAcPercent, profile.CpuMinimumPercent.ToString(), true),
        Expect(PowerModeStateField.CpuMinimumDcPercent, profile.CpuMinimumPercent.ToString(), true),
        Expect(PowerModeStateField.ProcessorBoostModeAc, "0", true),
        Expect(PowerModeStateField.ProcessorBoostModeDc, "0", true),
        Expect(PowerModeStateField.BrightnessAcPercent, profile.BrightnessAcPercent.ToString(), false),
        Expect(PowerModeStateField.BrightnessDcPercent, profile.BrightnessDcPercent.ToString(), false),
        Expect(PowerModeStateField.DisplayTimeoutAcSeconds, profile.DisplayTimeoutAcSeconds.ToString(), true),
        Expect(PowerModeStateField.DisplayTimeoutDcSeconds, profile.DisplayTimeoutDcSeconds.ToString(), true)
    ];

    private static VerificationExpectation Expect(
        PowerModeStateField field,
        string value,
        bool critical) => new(field, value, critical);
}
