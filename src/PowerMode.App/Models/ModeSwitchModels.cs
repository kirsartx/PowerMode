namespace PowerModeWinUI;

internal enum ModeSwitchOutcome
{
    Succeeded,
    Partial,
    Failed,
    TimedOut,
    RollbackFailed,
    Cancelled
}

internal sealed record ModeSwitchRequest(
    Guid OperationId,
    PowerModeTarget Target,
    int? CpuMaximumPercent,
    bool DisableWifi,
    string Source,
    string? Reason,
    Guid? RuleId = null,
    string? RuleName = null,
    bool RecordHistory = true);

internal sealed record SnapshotRestoreRequest(
    Guid OperationId,
    PowerModeState Snapshot,
    string Source,
    string? Reason);

internal sealed record RollbackResult(
    bool Attempted,
    bool Succeeded,
    BackendOperationResult? Operation);

internal sealed record ModeSwitchResult(
    Guid OperationId,
    ModeSwitchOutcome Outcome,
    PowerModeState? BeforeState,
    PowerModeState? AfterState,
    IReadOnlyList<BackendStepResult> Steps,
    RollbackResult? Rollback,
    string UserSummary,
    string DiagnosticSummary,
    bool RequiresRecovery = false,
    string? JournalError = null);

internal enum LastOperationStatus
{
    Prepared,
    Applying,
    Verified,
    RolledBack,
    Uncertain
}

internal sealed record LastOperationRecord(
    int SchemaVersion,
    Guid OperationId,
    string Source,
    string? Reason,
    PowerModeTarget Target,
    int? CpuMaximumPercent,
    bool DisableWifi,
    DateTimeOffset StartedAtUtc,
    PowerModeState BeforeState,
    LastOperationStatus Status,
    ModeSwitchOutcome? Outcome = null,
    RollbackResult? Rollback = null);
