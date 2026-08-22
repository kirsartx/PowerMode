namespace PowerModeWinUI;

internal sealed record ExitRestoreDecision(
    bool ShouldClose,
    ModeSwitchResult? Operation,
    string? Error);

internal sealed class ExitRestoreCoordinator
{
    private readonly IModeSwitchCoordinator _modeSwitchCoordinator;
    private readonly Func<Guid> _operationIdFactory;

    public ExitRestoreCoordinator(
        IModeSwitchCoordinator modeSwitchCoordinator,
        Func<Guid>? operationIdFactory = null)
    {
        _modeSwitchCoordinator = modeSwitchCoordinator
            ?? throw new ArgumentNullException(nameof(modeSwitchCoordinator));
        _operationIdFactory = operationIdFactory ?? Guid.NewGuid;
    }

    public async Task<ExitRestoreDecision> RestoreLaunchStateAsync(
        PowerModeState snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            var result = await _modeSwitchCoordinator.RestoreSnapshotAsync(
                new SnapshotRestoreRequest(
                    _operationIdFactory(),
                    snapshot,
                    "shutdown",
                    "restore exact launch state"),
                cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return new(
                    false,
                    null,
                    "The launch-state restore transaction was not accepted.");
            }

            var shouldClose = result.Outcome == ModeSwitchOutcome.Succeeded &&
                !result.RequiresRecovery;
            return new(
                shouldClose,
                result,
                shouldClose
                    ? null
                    : "The launch-state restore transaction did not complete safely.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(
                false,
                null,
                $"The launch-state restore transaction failed: {exception.Message}");
        }
    }
}
