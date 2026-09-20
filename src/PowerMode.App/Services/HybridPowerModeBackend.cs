using System.Diagnostics;

namespace PowerModeWinUI;

/// <summary>
/// Wraps the PowerShell engine backend and answers status reads natively through powrprof,
/// removing the ~2.7s cold start from every read (twice per mode switch plus every refresh).
/// Mutations (apply/restore) still run through the engine unchanged, so write behaviour and
/// the rollback-verification contract are identical. If a native read cannot produce a
/// reliable state it transparently falls back to the engine.
/// </summary>
internal sealed class HybridPowerModeBackend : IPowerModeBackend
{
    private readonly IPowerModeBackend _engine;
    private readonly NativePowerStateReader _reader;
    private readonly NativePowerApplier _applier;

    public HybridPowerModeBackend(
        IPowerModeBackend engine,
        NativePowerStateReader reader,
        NativePowerApplier applier)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
    }

    public Task<PowerModeStateResult> ReadStateAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        PowerModeState? state = null;
        try
        {
            state = _reader.TryRead();
        }
        catch
        {
            state = null;
        }

        if (state is not null)
        {
            var operation = new BackendOperationResult(
                operationId,
                "status",
                null,
                DateTimeOffset.UtcNow,
                stopwatch.Elapsed,
                BackendOperationOutcome.Succeeded,
                state,
                state,
                Array.Empty<BackendStepResult>(),
                Array.Empty<VerificationExpectation>(),
                null);
            return Task.FromResult(new PowerModeStateResult(state, operation));
        }

        return _engine.ReadStateAsync(operationId, cancellationToken);
    }

    public Task<BackendOperationResult> ApplyAsync(
        Guid operationId,
        PowerModeTarget target,
        int? cpuMaximumPercent = null,
        bool disableWifi = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Native fast path: plain presets without a WiFi change. Custom profiles, snapshots
        // and WiFi operations keep using the engine unchanged.
        if (!disableWifi && target.Preset is not null && target.Snapshot is null)
        {
            BackendOperationResult? native = null;
            try
            {
                native = _applier.TryApplyPreset(operationId, target, cpuMaximumPercent);
            }
            catch
            {
                native = null;
            }

            if (native is not null)
                return Task.FromResult(native);
        }

        return _engine.ApplyAsync(
            operationId,
            target,
            cpuMaximumPercent,
            disableWifi,
            cancellationToken);
    }

    public Task<BackendOperationResult> RestoreAsync(
        Guid operationId,
        PowerModeState snapshot,
        CancellationToken cancellationToken = default) =>
        _engine.RestoreAsync(operationId, snapshot, cancellationToken);
}
