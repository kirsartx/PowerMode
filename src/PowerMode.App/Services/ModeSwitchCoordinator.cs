using System.Globalization;

namespace PowerModeWinUI;

internal sealed record LastOperationVerificationResult(
    Guid OperationId,
    bool MatchesCriticalExpectations,
    PowerModeState? CurrentState,
    IReadOnlyList<PowerModeStateField> MismatchedFields,
    string? Error);

internal interface IModeSwitchCoordinator
{
    Task<ModeSwitchResult?> TrySwitchAsync(
        ModeSwitchRequest request,
        CancellationToken cancellationToken = default);

    Task<ModeSwitchResult?> RestoreSnapshotAsync(
        SnapshotRestoreRequest request,
        CancellationToken cancellationToken = default);

    Task<LastOperationVerificationResult> VerifyLastOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<RecoveryActionResult> RestoreBeforeStateAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}

internal sealed class ModeSwitchCoordinator : IModeSwitchCoordinator
{
    private const int JournalSchemaVersion = 1;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(90);
    private static readonly Guid SaverScheme =
        Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid BalancedScheme =
        Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighScheme =
        Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private readonly IPowerModeBackend _backend;
    private readonly ILastOperationStore _lastOperationStore;
    private readonly IRecoveryHistory _history;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private volatile bool _recoveryRequired;

    public ModeSwitchCoordinator(
        IPowerModeBackend backend,
        ILastOperationStore lastOperationStore,
        IRecoveryHistory history,
        TimeProvider timeProvider)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lastOperationStore = lastOperationStore
            ?? throw new ArgumentNullException(nameof(lastOperationStore));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ModeSwitchResult?> TrySwitchAsync(
        ModeSwitchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_recoveryRequired || !await _operationGate.WaitAsync(0).ConfigureAwait(false))
            return null;

        try
        {
            if (_recoveryRequired)
                return null;
            if (cancellationToken.IsCancellationRequested)
                return CancelledResult(request.OperationId);

            LastOperationReadResult journal;
            try
            {
                journal = await _lastOperationStore.ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CancelledResult(request.OperationId);
            }
            catch (Exception exception)
            {
                _recoveryRequired = true;
                return RecoveryRequiredResult(
                    request.OperationId,
                    exception.Message);
            }

            if (!journal.Succeeded)
            {
                _recoveryRequired = true;
                return RecoveryRequiredResult(
                    request.OperationId,
                    journal.Error ?? "The operation journal could not be read.");
            }

            if (journal.Record is { } record && IsRecoveryPending(record.Status))
            {
                _recoveryRequired = true;
                return null;
            }

            return await TrySwitchCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ModeSwitchResult?> RestoreSnapshotAsync(
        SnapshotRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Snapshot);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recoveryRequired)
                return null;
            if (cancellationToken.IsCancellationRequested)
                return CancelledResult(request.OperationId);

            LastOperationReadResult journal;
            try
            {
                journal = await _lastOperationStore.ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CancelledResult(request.OperationId);
            }
            catch (Exception exception)
            {
                _recoveryRequired = true;
                return RecoveryRequiredResult(request.OperationId, exception.Message);
            }

            if (!journal.Succeeded)
            {
                _recoveryRequired = true;
                return RecoveryRequiredResult(
                    request.OperationId,
                    journal.Error ?? "The operation journal could not be read.");
            }
            if (journal.Record is { } record && IsRecoveryPending(record.Status))
            {
                _recoveryRequired = true;
                return RecoveryRequiredResult(
                    request.OperationId,
                    "The previous power operation must be recovered first.");
            }

            return await RestoreSnapshotCoreAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<ModeSwitchResult> RestoreSnapshotCoreAsync(
        SnapshotRestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty)
            return FailedResult(request.OperationId, "Operation ID is required.");

        var expectations = DeriveSnapshotExpectations(request.Snapshot);
        if (expectations.Count == 0)
        {
            return FailedResult(
                request.OperationId,
                "The launch-state snapshot contains no restorable values.");
        }

        PowerModeState? beforeState;
        try
        {
            var initial = await _backend.ReadStateAsync(
                request.OperationId,
                cancellationToken).ConfigureAwait(false);
            beforeState = IsReliableRead(initial.Operation) ? initial.State : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CancelledResult(request.OperationId);
        }
        catch (Exception exception)
        {
            return FailedResult(request.OperationId, exception.Message);
        }

        if (beforeState is null)
        {
            return FailedResult(
                request.OperationId,
                "The current power state could not be read reliably; no restore was attempted.");
        }

        var prepared = new LastOperationRecord(
            JournalSchemaVersion,
            request.OperationId,
            request.Source,
            request.Reason,
            PowerModeTarget.ForSnapshot(request.Snapshot),
            null,
            false,
            _timeProvider.GetUtcNow(),
            beforeState,
            LastOperationStatus.Prepared);
        var preparedWrite = await TryWriteAsync(
            prepared,
            expectedOperationId: null,
            cancellationToken).ConfigureAwait(false);
        if (!preparedWrite.Succeeded)
        {
            var error = preparedWrite.Error ??
                "The snapshot-restore journal could not be prepared.";
            var failed = FailedResult(request.OperationId, error, beforeState);
            if (!preparedWrite.Conflict)
                return failed;

            _recoveryRequired = true;
            return failed with
            {
                RequiresRecovery = true,
                JournalError = error
            };
        }

        var applying = prepared with { Status = LastOperationStatus.Applying };
        var applyingWrite = await TryWriteAsync(
            applying,
            request.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (!applyingWrite.Succeeded)
        {
            var error = applyingWrite.Error ??
                "The snapshot-restore Applying record could not be written.";
            _recoveryRequired = true;
            return FailedResult(request.OperationId, error, beforeState) with
            {
                RequiresRecovery = true,
                JournalError = error
            };
        }

        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        BackendOperationResult restore;
        try
        {
            restore = await _backend.RestoreAsync(
                request.OperationId,
                request.Snapshot,
                cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            restore = BackendOperationResult.ContractFailure(
                BackendOperationOutcome.Failed,
                exception.Message) with { OperationId = request.OperationId };
        }

        PowerModeState? afterState = null;
        BackendOperationResult? readbackOperation = null;
        try
        {
            var readback = await _backend.ReadStateAsync(
                request.OperationId,
                cleanup.Token).ConfigureAwait(false);
            afterState = IsReliableRead(readback.Operation) ? readback.State : null;
            readbackOperation = readback.Operation;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            readbackOperation = BackendOperationResult.ContractFailure(
                BackendOperationOutcome.InvalidResponse,
                exception.Message) with { OperationId = request.OperationId };
        }

        var mismatches = CompareExpectations(expectations, afterState);
        var restoreSucceeded = restore.Outcome is
            BackendOperationOutcome.Succeeded or BackendOperationOutcome.Partial;
        var confirmed = restoreSucceeded &&
            restore.OperationId == request.OperationId &&
            string.Equals(restore.Action, "restore", StringComparison.OrdinalIgnoreCase) &&
            mismatches.Count == 0;
        var steps = restore.Steps
            .Concat(readbackOperation?.Steps ?? Array.Empty<BackendStepResult>())
            .ToArray();

        if (confirmed)
        {
            var succeeded = new ModeSwitchResult(
                request.OperationId,
                ModeSwitchOutcome.Succeeded,
                beforeState,
                afterState,
                steps,
                null,
                "Launch power state restored and verified.",
                BuildDiagnosticSummary(
                    request.OperationId,
                    restore,
                    readbackOperation,
                    mismatches));
            return await CompleteSnapshotRestoreAsync(
                applying,
                succeeded,
                LastOperationStatus.Verified,
                cleanup.Token).ConfigureAwait(false);
        }

        var rollback = await RestoreAndConfirmAsync(
            request.OperationId,
            beforeState,
            cleanup.Token).ConfigureAwait(false);
        var outcome = rollback.Succeeded
            ? restore.Outcome == BackendOperationOutcome.TimedOut
                ? ModeSwitchOutcome.TimedOut
                : ModeSwitchOutcome.Failed
            : ModeSwitchOutcome.RollbackFailed;
        var failedResult = new ModeSwitchResult(
            request.OperationId,
            outcome,
            beforeState,
            afterState,
            steps,
            rollback,
            "The launch power state could not be restored and verified.",
            BuildDiagnosticSummary(
                request.OperationId,
                restore,
                readbackOperation,
                mismatches));
        return await CompleteSnapshotRestoreAsync(
            applying,
            failedResult,
            rollback.Succeeded
                ? LastOperationStatus.RolledBack
                : LastOperationStatus.Uncertain,
            cleanup.Token).ConfigureAwait(false);
    }

    private async Task<ModeSwitchResult> TrySwitchCoreAsync(
        ModeSwitchRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return CancelledResult(request.OperationId);

        IReadOnlyList<VerificationExpectation> expectations;
        try
        {
            expectations = DeriveExpectations(request);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            return FailedResult(request.OperationId, exception.Message);
        }

        PowerModeState? beforeState;
        try
        {
            var initial = await _backend.ReadStateAsync(
                request.OperationId,
                cancellationToken).ConfigureAwait(false);
            beforeState = IsReliableRead(initial.Operation) ? initial.State : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CancelledResult(request.OperationId);
        }
        catch (Exception exception)
        {
            return FailedResult(request.OperationId, exception.Message);
        }

        if (beforeState is null)
        {
            return FailedResult(
                request.OperationId,
                "The current power state could not be read reliably; no mutation was attempted.");
        }
        if (cancellationToken.IsCancellationRequested)
            return CancelledResult(request.OperationId, beforeState);

        var startedAt = _timeProvider.GetUtcNow();
        var prepared = NewJournalRecord(
            request,
            beforeState,
            startedAt,
            LastOperationStatus.Prepared);
        var preparedWrite = await TryWriteAsync(
            prepared,
            expectedOperationId: null,
            cancellationToken).ConfigureAwait(false);
        if (!preparedWrite.Succeeded)
        {
            var error = preparedWrite.Error ??
                "The operation journal could not be prepared.";
            var failed = FailedResult(
                request.OperationId,
                error,
                beforeState);
            if (preparedWrite.Conflict)
            {
                _recoveryRequired = true;
                return failed with
                {
                    RequiresRecovery = true,
                    JournalError = error
                };
            }
            return failed;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledResult(request.OperationId, beforeState);
        }

        var applying = prepared with { Status = LastOperationStatus.Applying };
        var applyingWrite = await TryWriteAsync(
            applying,
            request.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (!applyingWrite.Succeeded)
        {
            var error = applyingWrite.Error ??
                "The applying journal record could not be written.";
            _recoveryRequired = true;
            return FailedResult(
                request.OperationId,
                error,
                beforeState) with
            {
                RequiresRecovery = true,
                JournalError = error
            };
        }

        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        BackendOperationResult applyOperation;
        try
        {
            applyOperation = await _backend.ApplyAsync(
                request.OperationId,
                request.Target,
                request.CpuMaximumPercent,
                request.DisableWifi,
                cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            applyOperation = BackendOperationResult.ContractFailure(
                BackendOperationOutcome.Failed,
                exception.Message) with { OperationId = request.OperationId };
        }

        PowerModeState? afterState = null;
        BackendOperationResult? readbackOperation = null;
        try
        {
            var readback = await _backend.ReadStateAsync(
                request.OperationId,
                cleanup.Token).ConfigureAwait(false);
            afterState = IsReliableRead(readback.Operation) ? readback.State : null;
            readbackOperation = readback.Operation;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            readbackOperation = BackendOperationResult.ContractFailure(
                BackendOperationOutcome.InvalidResponse,
                exception.Message) with { OperationId = request.OperationId };
        }

        var expectationContractValid = IsApplyContractValid(
            applyOperation,
            request,
            expectations);
        var mismatches = CompareExpectations(expectations, afterState);
        var criticalMismatch = mismatches.Any(field =>
            expectations.First(expectation => expectation.Field == field).Critical);
        var optionalMismatch = mismatches.Any(field =>
            expectations.First(expectation => expectation.Field == field).Critical is false);
        var applyFailed = applyOperation.Outcome is
            BackendOperationOutcome.Failed or
            BackendOperationOutcome.InvalidResponse or
            BackendOperationOutcome.ContractIncompatible;
        var readbackFailed = afterState is null;
        var needsRollback = applyFailed || !expectationContractValid ||
            readbackFailed || criticalMismatch;

        var baseOutcome = ClassifyOutcome(
            applyOperation.Outcome,
            criticalMismatch,
            optionalMismatch,
            expectationContractValid,
            afterState is not null);
        var steps = applyOperation.Steps
            .Concat(readbackOperation?.Steps ?? Array.Empty<BackendStepResult>())
            .ToArray();

        if (!needsRollback)
        {
            var result = new ModeSwitchResult(
                request.OperationId,
                baseOutcome,
                beforeState,
                afterState,
                steps,
                null,
                BuildUserSummary(baseOutcome, mismatches.Count),
                BuildDiagnosticSummary(
                    request.OperationId,
                    applyOperation,
                    readbackOperation,
                    mismatches),
                false,
                null);
            return await CompleteAsync(
                request,
                applying,
                result,
                LastOperationStatus.Verified,
                cleanup.Token).ConfigureAwait(false);
        }

        var rollback = await RestoreAndConfirmAsync(
            request.OperationId,
            beforeState,
            cleanup.Token).ConfigureAwait(false);
        var rollbackOutcome = rollback.Succeeded
            ? baseOutcome is ModeSwitchOutcome.TimedOut
                ? ModeSwitchOutcome.TimedOut
                : ModeSwitchOutcome.Failed
            : ModeSwitchOutcome.RollbackFailed;
        var rollbackResult = new ModeSwitchResult(
            request.OperationId,
            rollbackOutcome,
            beforeState,
            afterState,
            steps,
            rollback,
            BuildUserSummary(rollbackOutcome, mismatches.Count),
            BuildDiagnosticSummary(
                request.OperationId,
                applyOperation,
                readbackOperation,
                mismatches),
            false,
            null);
        return await CompleteAsync(
            request,
            applying,
            rollbackResult,
            rollback.Succeeded
                ? LastOperationStatus.RolledBack
                : LastOperationStatus.Uncertain,
            cleanup.Token).ConfigureAwait(false);
    }

    public async Task<LastOperationVerificationResult> VerifyLastOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!await _operationGate.WaitAsync(0).ConfigureAwait(false))
        {
            return new(operationId, false, null, [], "Another power operation is in progress.");
        }

        try
        {
            var read = await _lastOperationStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!read.Succeeded)
                return new(operationId, false, null, [], read.Error);
            if (read.Record is null || read.Record.OperationId != operationId)
                return new(operationId, false, null, [], "The requested operation is no longer current.");

            IReadOnlyList<VerificationExpectation> expectations;
            try
            {
                expectations = DeriveExpectations(read.Record);
            }
            catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
            {
                return new(operationId, false, null, [], exception.Message);
            }

            var reality = await _backend.ReadStateAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
            var mismatches = CompareExpectations(expectations, reality.State);
            var criticalMismatch = mismatches.Any(field =>
                expectations.First(expectation => expectation.Field == field).Critical);
            if (criticalMismatch)
            {
                return new(operationId, false, reality.State, mismatches, "Critical settings do not match.");
            }

            var verified = read.Record with
            {
                Status = LastOperationStatus.Verified,
                Outcome = mismatches.Count == 0
                    ? ModeSwitchOutcome.Succeeded
                    : ModeSwitchOutcome.Partial
            };
            var write = await TryWriteAsync(verified, operationId, cancellationToken)
                .ConfigureAwait(false);
            if (!write.Succeeded)
                return new(operationId, false, reality.State, mismatches, write.Error);

            _recoveryRequired = false;
            return new(operationId, true, reality.State, mismatches, null);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RecoveryActionResult> RestoreBeforeStateAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!await _operationGate.WaitAsync(0).ConfigureAwait(false))
            return new(false, false, "Another power operation is in progress.");

        try
        {
            var read = await _lastOperationStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!read.Succeeded)
                return new(false, false, read.Error);
            if (read.Record is null || read.Record.OperationId != operationId)
                return new(false, false, "The requested operation is no longer current.");

            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            var restore = await _backend.RestoreAsync(
                Guid.NewGuid(),
                read.Record.BeforeState,
                cleanup.Token).ConfigureAwait(false);
            var reality = await _backend.ReadStateAsync(
                operationId,
                cleanup.Token).ConfigureAwait(false);
            var restoreSucceeded = restore.Outcome is
                BackendOperationOutcome.Succeeded or BackendOperationOutcome.Partial;
            var confirmed = restoreSucceeded &&
                MatchesSnapshot(read.Record.BeforeState, reality.State);
            var status = confirmed
                ? LastOperationStatus.RolledBack
                : LastOperationStatus.Uncertain;
            var terminal = await TryWriteAsync(
                read.Record with
                {
                    Status = status,
                    Outcome = confirmed
                        ? ModeSwitchOutcome.Failed
                        : ModeSwitchOutcome.RollbackFailed,
                    Rollback = new RollbackResult(true, confirmed, restore)
                },
                operationId,
                cleanup.Token).ConfigureAwait(false);
            if (!terminal.Succeeded)
            {
                _recoveryRequired = true;
                return new(false, false, terminal.Error, confirmed);
            }
            _recoveryRequired = !confirmed;
            return new(
                confirmed,
                true,
                confirmed ? null : "The previous power state could not be confirmed.",
                confirmed);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<ModeSwitchResult> CompleteAsync(
        ModeSwitchRequest request,
        LastOperationRecord applying,
        ModeSwitchResult result,
        LastOperationStatus terminalStatus,
        CancellationToken cancellationToken)
    {
        var terminal = await TryWriteAsync(
            applying with
            {
                Status = terminalStatus,
                Outcome = result.Outcome,
                Rollback = result.Rollback
            },
            request.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (!terminal.Succeeded)
        {
            _recoveryRequired = true;
            return result with
            {
                RequiresRecovery = true,
                JournalError = terminal.Error ?? "The terminal operation journal write failed."
            };
        }

        _recoveryRequired = terminalStatus == LastOperationStatus.Uncertain;

        if (request.RecordHistory)
        {
            try
            {
                await _history.RecordAsync(
                    new SwitchHistoryEntry
                    {
                        Id = request.OperationId,
                        Timestamp = _timeProvider.GetUtcNow(),
                        OperationKind = "mode-switch",
                        PreviousMode = applying.BeforeState.DetectedMode?.ToString().ToLowerInvariant() ?? "unknown",
                        TargetMode = request.Target.Key,
                        Trigger = request.Source,
                        Reason = request.Reason ?? string.Empty,
                        RuleId = request.RuleId,
                        RuleName = request.RuleName,
                        Succeeded = result.Outcome is ModeSwitchOutcome.Succeeded or ModeSwitchOutcome.Partial,
                        ErrorMessage = result.Outcome is ModeSwitchOutcome.Succeeded or ModeSwitchOutcome.Partial
                            ? null
                            : result.UserSummary
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return result with
                {
                    DiagnosticSummary = result.DiagnosticSummary +
                        $" History write failed: {Bound(exception.Message)}"
                };
            }
        }

        return result;
    }

    private async Task<ModeSwitchResult> CompleteSnapshotRestoreAsync(
        LastOperationRecord applying,
        ModeSwitchResult result,
        LastOperationStatus terminalStatus,
        CancellationToken cancellationToken)
    {
        var terminal = await TryWriteAsync(
            applying with
            {
                Status = terminalStatus,
                Outcome = result.Outcome,
                Rollback = result.Rollback
            },
            applying.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (!terminal.Succeeded)
        {
            _recoveryRequired = true;
            return result with
            {
                RequiresRecovery = true,
                JournalError = terminal.Error ??
                    "The terminal snapshot-restore journal write failed."
            };
        }

        _recoveryRequired = terminalStatus == LastOperationStatus.Uncertain;
        return result;
    }

    private async Task<LastOperationWriteResult> TryWriteAsync(
        LastOperationRecord record,
        Guid? expectedOperationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _lastOperationStore.WriteAsync(
                record,
                expectedOperationId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(false, false, "The operation journal write was cancelled.");
        }
        catch (Exception exception)
        {
            return new(false, false, exception.Message);
        }
    }

    private async Task<RollbackResult> RestoreAndConfirmAsync(
        Guid operationId,
        PowerModeState beforeState,
        CancellationToken cancellationToken)
    {
        BackendOperationResult restore;
        try
        {
            restore = await _backend.RestoreAsync(
                Guid.NewGuid(),
                beforeState,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            restore = BackendOperationResult.ContractFailure(
                BackendOperationOutcome.Failed,
                exception.Message);
        }

        if (restore.Outcome is not (
            BackendOperationOutcome.Succeeded or BackendOperationOutcome.Partial))
            return new(true, false, restore);

        try
        {
            var confirmation = await _backend.ReadStateAsync(
                operationId,
                cancellationToken).ConfigureAwait(false);
            return new(
                true,
                MatchesSnapshot(beforeState, confirmation.State),
                restore);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(
                true,
                false,
                restore with
                {
                    ContractError = Bound(exception.Message)
                });
        }
    }

    private static IReadOnlyList<VerificationExpectation> DeriveExpectations(
        ModeSwitchRequest request)
    {
        if (request.OperationId == Guid.Empty)
            throw new ArgumentException("Operation ID is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Target);
        if (request.Target.Snapshot is not null)
        {
            throw new ArgumentException(
                "Snapshot targets must use RestoreSnapshotAsync.",
                nameof(request));
        }

        if (request.Target.Preset is PowerModePreset.Balanced or PowerModePreset.High or null &&
            request.CpuMaximumPercent.HasValue)
        {
            throw new ArgumentException(
                "CPU overrides are supported only for Remote and Saver targets.",
                nameof(request));
        }

        if (request.Target.Preset is null && request.CpuMaximumPercent.HasValue)
        {
            throw new ArgumentException(
                "CPU overrides are not supported for custom targets.",
                nameof(request));
        }

        var expectations = new List<VerificationExpectation>();
        if (request.Target.Preset is { } preset)
        {
            var cpu = preset switch
            {
                PowerModePreset.Remote => Math.Clamp(request.CpuMaximumPercent ?? 32, 1, 32),
                PowerModePreset.Saver => Math.Clamp(request.CpuMaximumPercent ?? 30, 1, 30),
                PowerModePreset.Balanced or PowerModePreset.High => 100,
                _ => throw new ArgumentOutOfRangeException()
            };
            var scheme = preset switch
            {
                PowerModePreset.Remote or PowerModePreset.Saver => SaverScheme,
                PowerModePreset.Balanced => BalancedScheme,
                PowerModePreset.High => HighScheme,
                _ => throw new ArgumentOutOfRangeException()
            };
            var brightness = preset switch
            {
                PowerModePreset.Balanced or PowerModePreset.High => 100,
                _ => 50
            };
            AddStandardExpectations(
                expectations,
                scheme,
                cpu,
                brightness,
                preset is PowerModePreset.Remote or PowerModePreset.Saver ? 60 : null);
        }
        else
        {
            var profile = request.Target.CustomProfile!;
            Add(expectations, PowerModeStateField.ActiveSchemeId, SaverScheme, true);
            Add(expectations, PowerModeStateField.CpuMaximumAcPercent, profile.CpuMaximumAcPercent, true);
            Add(expectations, PowerModeStateField.CpuMaximumDcPercent, profile.CpuMaximumDcPercent, true);
            Add(expectations, PowerModeStateField.CpuMinimumAcPercent, profile.CpuMinimumPercent, true);
            Add(expectations, PowerModeStateField.CpuMinimumDcPercent, profile.CpuMinimumPercent, true);
            if (profile.DisableBoost)
            {
                Add(expectations, PowerModeStateField.ProcessorBoostModeAc, 0, true);
                Add(expectations, PowerModeStateField.ProcessorBoostModeDc, 0, true);
            }
            Add(expectations, PowerModeStateField.BrightnessAcPercent, profile.BrightnessAcPercent, false);
            Add(expectations, PowerModeStateField.BrightnessDcPercent, profile.BrightnessDcPercent, false);
            Add(expectations, PowerModeStateField.DisplayTimeoutAcSeconds, profile.DisplayTimeoutAcSeconds, true);
            Add(expectations, PowerModeStateField.DisplayTimeoutDcSeconds, profile.DisplayTimeoutDcSeconds, true);
        }

        if (request.DisableWifi)
            Add(expectations, PowerModeStateField.WifiDisabled, true, false);
        return expectations;
    }

    private static IReadOnlyList<VerificationExpectation> DeriveExpectations(
        LastOperationRecord record) => record.Target.Snapshot is { } snapshot
        ? DeriveSnapshotExpectations(snapshot)
        : DeriveExpectations(new ModeSwitchRequest(
            record.OperationId,
            record.Target,
            record.CpuMaximumPercent,
            record.DisableWifi,
            record.Source,
            record.Reason));

    private static IReadOnlyList<VerificationExpectation> DeriveSnapshotExpectations(
        PowerModeState snapshot)
    {
        var expectations = new List<VerificationExpectation>();
        foreach (var field in Enum.GetValues<PowerModeStateField>())
        {
            var value = GetFieldValue(snapshot, field);
            if (value is not null)
                Add(expectations, field, value, critical: true);
        }
        return expectations;
    }

    private static void AddStandardExpectations(
        ICollection<VerificationExpectation> expectations,
        Guid scheme,
        int cpu,
        int brightness,
        int? displayTimeout)
    {
        Add(expectations, PowerModeStateField.ActiveSchemeId, scheme, true);
        Add(expectations, PowerModeStateField.CpuMaximumAcPercent, cpu, true);
        Add(expectations, PowerModeStateField.CpuMaximumDcPercent, cpu, true);
        Add(expectations, PowerModeStateField.BrightnessAcPercent, brightness, false);
        Add(expectations, PowerModeStateField.BrightnessDcPercent, brightness, false);
        if (displayTimeout.HasValue)
        {
            Add(expectations, PowerModeStateField.DisplayTimeoutAcSeconds, displayTimeout.Value, true);
            Add(expectations, PowerModeStateField.DisplayTimeoutDcSeconds, displayTimeout.Value, true);
        }
        Add(expectations, PowerModeStateField.SleepTimeoutAcSeconds, 0, true);
        Add(expectations, PowerModeStateField.SleepTimeoutDcSeconds, 0, true);
        Add(expectations, PowerModeStateField.HibernateTimeoutAcSeconds, 0, true);
        Add(expectations, PowerModeStateField.HibernateTimeoutDcSeconds, 0, true);
    }

    private static void Add(
        ICollection<VerificationExpectation> expectations,
        PowerModeStateField field,
        object value,
        bool critical) =>
        expectations.Add(new(
            field,
            Convert.ToString(value, CultureInfo.InvariantCulture)!,
            critical));

    private static bool IsReliableRead(BackendOperationResult operation) =>
        operation.Outcome is BackendOperationOutcome.Succeeded or BackendOperationOutcome.Partial;

    private static bool IsApplyContractValid(
        BackendOperationResult operation,
        ModeSwitchRequest request,
        IReadOnlyList<VerificationExpectation> expected)
    {
        if (operation.Outcome is BackendOperationOutcome.TimedOut or BackendOperationOutcome.Cancelled)
            return true;
        if (operation.OperationId != request.OperationId ||
            !string.Equals(operation.Action, "apply", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                operation.RequestedTargetKey,
                request.Target.Key,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var actual = operation.Expectations
            .Select(expectation => (expectation.Field, expectation.ExpectedValue, expectation.Critical))
            .ToHashSet();
        var planned = expected
            .Select(expectation => (expectation.Field, expectation.ExpectedValue, expectation.Critical))
            .ToHashSet();
        return actual.SetEquals(planned);
    }

    private static IReadOnlyList<PowerModeStateField> CompareExpectations(
        IReadOnlyList<VerificationExpectation> expectations,
        PowerModeState? actual)
    {
        if (actual is null)
            return expectations.Select(expectation => expectation.Field).Distinct().ToArray();
        return expectations
            .Where(expectation => !ValueEquals(
                GetFieldValue(actual, expectation.Field),
                expectation.ExpectedValue))
            .Select(expectation => expectation.Field)
            .Distinct()
            .ToArray();
    }

    private static bool MatchesSnapshot(PowerModeState expected, PowerModeState? actual)
    {
        if (actual is null)
            return false;
        foreach (var field in Enum.GetValues<PowerModeStateField>())
        {
            var expectedValue = GetFieldValue(expected, field);
            if (expectedValue is not null && !ValueEquals(
                GetFieldValue(actual, field),
                Convert.ToString(expectedValue, CultureInfo.InvariantCulture)!))
                return false;
        }
        return true;
    }

    private static object? GetFieldValue(PowerModeState state, PowerModeStateField field) => field switch
    {
        PowerModeStateField.ActiveSchemeId => state.ActiveSchemeId,
        PowerModeStateField.CpuMaximumAcPercent => state.CpuMaximumAcPercent,
        PowerModeStateField.CpuMaximumDcPercent => state.CpuMaximumDcPercent,
        PowerModeStateField.CpuMinimumAcPercent => state.CpuMinimumAcPercent,
        PowerModeStateField.CpuMinimumDcPercent => state.CpuMinimumDcPercent,
        PowerModeStateField.ProcessorBoostModeAc => state.ProcessorBoostModeAc,
        PowerModeStateField.ProcessorBoostModeDc => state.ProcessorBoostModeDc,
        PowerModeStateField.BrightnessAcPercent => state.BrightnessAcPercent,
        PowerModeStateField.BrightnessDcPercent => state.BrightnessDcPercent,
        PowerModeStateField.DisplayTimeoutAcSeconds => state.DisplayTimeoutAcSeconds,
        PowerModeStateField.DisplayTimeoutDcSeconds => state.DisplayTimeoutDcSeconds,
        PowerModeStateField.SleepTimeoutAcSeconds => state.SleepTimeoutAcSeconds,
        PowerModeStateField.SleepTimeoutDcSeconds => state.SleepTimeoutDcSeconds,
        PowerModeStateField.HibernateTimeoutAcSeconds => state.HibernateTimeoutAcSeconds,
        PowerModeStateField.HibernateTimeoutDcSeconds => state.HibernateTimeoutDcSeconds,
        PowerModeStateField.WifiDisabled => state.WifiDisabled,
        _ => null
    };

    private static bool ValueEquals(object? actual, string expected)
    {
        if (actual is null)
            return false;
        var normalized = Convert.ToString(actual, CultureInfo.InvariantCulture);
        return string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static ModeSwitchOutcome ClassifyOutcome(
        BackendOperationOutcome operationOutcome,
        bool criticalMismatch,
        bool optionalMismatch,
        bool expectationContractValid,
        bool hasReadback) =>
        operationOutcome switch
        {
            BackendOperationOutcome.TimedOut when criticalMismatch || !hasReadback => ModeSwitchOutcome.TimedOut,
            BackendOperationOutcome.TimedOut => optionalMismatch
                ? ModeSwitchOutcome.Partial
                : ModeSwitchOutcome.Succeeded,
            BackendOperationOutcome.Cancelled => ModeSwitchOutcome.Cancelled,
            BackendOperationOutcome.Partial when !criticalMismatch && expectationContractValid => ModeSwitchOutcome.Partial,
            BackendOperationOutcome.Succeeded when expectationContractValid && !criticalMismatch => optionalMismatch
                ? ModeSwitchOutcome.Partial
                : ModeSwitchOutcome.Succeeded,
            _ => ModeSwitchOutcome.Failed
        };

    private static bool IsCriticalMismatch(
        IReadOnlyList<VerificationExpectation> expectations,
        IReadOnlyList<PowerModeStateField> mismatches) =>
        mismatches.Any(field => expectations.First(expectation => expectation.Field == field).Critical);

    private static bool IsRecoveryPending(LastOperationStatus status) =>
        status is LastOperationStatus.Prepared or
            LastOperationStatus.Applying or
            LastOperationStatus.Uncertain;

    private static LastOperationRecord NewJournalRecord(
        ModeSwitchRequest request,
        PowerModeState beforeState,
        DateTimeOffset startedAt,
        LastOperationStatus status) => new(
        JournalSchemaVersion,
        request.OperationId,
        request.Source,
        request.Reason,
        request.Target,
        request.CpuMaximumPercent,
        request.DisableWifi,
        startedAt,
        beforeState,
        status);

    private static ModeSwitchResult FailedResult(
        Guid operationId,
        string error,
        PowerModeState? beforeState = null) => new(
        operationId,
        ModeSwitchOutcome.Failed,
        beforeState,
        null,
        [],
        null,
        "The mode switch could not be completed.",
        $"operationId={operationId:D}; {Bound(error)}");

    private static ModeSwitchResult RecoveryRequiredResult(
        Guid operationId,
        string error) => FailedResult(operationId, error) with
    {
        RequiresRecovery = true,
        JournalError = Bound(error)
    };

    private static ModeSwitchResult CancelledResult(
        Guid operationId,
        PowerModeState? beforeState = null) => new(
        operationId,
        ModeSwitchOutcome.Cancelled,
        beforeState,
        null,
        [],
        null,
        "The mode switch was cancelled before changing system power settings.",
        $"operationId={operationId:D}; cancelled");

    private static string BuildUserSummary(
        ModeSwitchOutcome outcome,
        int mismatchCount) => outcome switch
        {
            ModeSwitchOutcome.Succeeded => "Power mode applied and verified.",
            ModeSwitchOutcome.Partial => $"Power mode applied with {mismatchCount} optional setting(s) not confirmed.",
            ModeSwitchOutcome.TimedOut => "Power mode timed out; the previous state was restored when possible.",
            ModeSwitchOutcome.RollbackFailed => "Power mode failed and recovery could not be confirmed.",
            ModeSwitchOutcome.Cancelled => "Power mode switch was cancelled.",
            _ => "Power mode switch failed; the previous state was restored when possible."
        };

    private static string BuildDiagnosticSummary(
        Guid operationId,
        BackendOperationResult apply,
        BackendOperationResult? readback,
        IReadOnlyList<PowerModeStateField> mismatches)
    {
        var parts = new List<string>
        {
            $"operationId={operationId:D}",
            $"apply={apply.Outcome}"
        };
        if (readback is not null)
            parts.Add($"readback={readback.Outcome}");
        if (mismatches.Count > 0)
            parts.Add($"mismatches={string.Join(',', mismatches)}");
        var failedSteps = apply.Steps
            .Where(step => step.ExitCode is not null and not 0 || step.TimedOut)
            .Select(step => string.IsNullOrWhiteSpace(step.Name) ? "unnamed" : step.Name)
            .Take(20)
            .ToArray();
        if (failedSteps.Length > 0)
            parts.Add($"failedSteps={string.Join(',', failedSteps)}");
        return Bound(string.Join("; ", parts));
    }

    private static string Bound(string value) =>
        value.Length <= 8 * 1024 ? value : value[..(8 * 1024)];
}
