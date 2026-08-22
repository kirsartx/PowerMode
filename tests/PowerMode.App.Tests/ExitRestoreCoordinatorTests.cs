using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ExitRestoreCoordinatorTests
{
    [Fact]
    public async Task RestoreLaunchStateAsync_SuccessAllowsCloseAndPassesExactSnapshot()
    {
        var snapshot = State();
        var operationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var transaction = Result(operationId, ModeSwitchOutcome.Succeeded, snapshot);
        var modeSwitches = new RecordingCoordinator { RestoreResult = transaction };
        var coordinator = new ExitRestoreCoordinator(modeSwitches, () => operationId);

        var decision = await coordinator.RestoreLaunchStateAsync(snapshot);

        Assert.True(decision.ShouldClose);
        Assert.Same(transaction, decision.Operation);
        Assert.Equal(snapshot, modeSwitches.RestoreRequest!.Snapshot);
        Assert.Equal("shutdown", modeSwitches.RestoreRequest.Source);
    }

    [Fact]
    public async Task RestoreLaunchStateAsync_FailedTransactionKeepsWindowOpen()
    {
        var snapshot = State();
        var transaction = Result(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            ModeSwitchOutcome.RollbackFailed,
            snapshot) with
        {
            RequiresRecovery = true,
            JournalError = "terminal journal failure"
        };
        var modeSwitches = new RecordingCoordinator { RestoreResult = transaction };
        var coordinator = new ExitRestoreCoordinator(modeSwitches);

        var decision = await coordinator.RestoreLaunchStateAsync(snapshot);

        Assert.False(decision.ShouldClose);
        Assert.Same(transaction, decision.Operation);
        Assert.Contains("restore", decision.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private static ModeSwitchResult Result(
        Guid operationId,
        ModeSwitchOutcome outcome,
        PowerModeState state) => new(
        operationId,
        outcome,
        state,
        state,
        [],
        null,
        outcome == ModeSwitchOutcome.Succeeded
            ? "Launch power state restored and verified."
            : "Launch power state could not be restored.",
        $"operationId={operationId:D}");

    private static PowerModeState State() => new(
        Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
        "Balanced",
        PowerModePreset.Balanced,
        PowerSourceKind.Ac,
        3.5,
        91,
        73,
        7,
        4,
        2,
        1,
        82,
        61,
        480,
        240,
        900,
        600,
        1800,
        1200,
        true);

    private sealed class RecordingCoordinator : IModeSwitchCoordinator
    {
        public SnapshotRestoreRequest? RestoreRequest { get; private set; }
        public ModeSwitchResult? RestoreResult { get; init; }

        public Task<ModeSwitchResult?> TrySwitchAsync(
            ModeSwitchRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ModeSwitchResult?> RestoreSnapshotAsync(
            SnapshotRestoreRequest request,
            CancellationToken cancellationToken = default)
        {
            RestoreRequest = request;
            return Task.FromResult(RestoreResult);
        }

        public Task<LastOperationVerificationResult> VerifyLastOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RecoveryActionResult> RestoreBeforeStateAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
