namespace PowerModeWinUI;

internal sealed record StartupInitializationResult(
    bool ActivationDeferred,
    LastOperationRecord? LastOperation,
    PowerModeState? CurrentState,
    string? Error);

internal interface IStartupCoordinator
{
    Task<StartupInitializationResult> InitializeAsync(
        SettingsLoadResult settingsLoad,
        CancellationToken cancellationToken = default);

    Task ResumeAfterRecoveryAsync(
        CancellationToken cancellationToken = default);

    void AcceptRecoveredSettings(SettingsLoadResult recovered);
}

internal sealed class StartupCoordinator : IStartupCoordinator
{
    private readonly ILastOperationStore _lastOperationStore;
    private readonly IPowerModeBackend _backend;
    private readonly Func<SettingsLoadResult, CancellationToken, Task> _activateAsync;
    private readonly StartupMutationGate _mutationGate;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SettingsLoadResult? _settingsLoad;
    private StartupInitializationResult? _initializationResult;
    private PowerModeState? _launchState;
    private bool _activationCompleted;

    public StartupCoordinator(
        ILastOperationStore lastOperationStore,
        IPowerModeBackend backend,
        Func<SettingsLoadResult, CancellationToken, Task> activateAsync,
        StartupMutationGate? mutationGate = null)
    {
        _lastOperationStore = lastOperationStore
            ?? throw new ArgumentNullException(nameof(lastOperationStore));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _activateAsync = activateAsync
            ?? throw new ArgumentNullException(nameof(activateAsync));
        _mutationGate = mutationGate ?? new StartupMutationGate();
    }

    public async Task<StartupInitializationResult> InitializeAsync(
        SettingsLoadResult settingsLoad,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsLoad);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initializationResult is not null)
                return _initializationResult;

            _settingsLoad = settingsLoad;
            var journal = await _lastOperationStore.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!journal.Succeeded)
            {
                return Remember(new(
                    true,
                    null,
                    null,
                    $"The last-operation journal could not be read: {journal.Error}"));
            }

            try
            {
                var reality = await _backend.ReadStateAsync(
                    journal.Record?.OperationId ?? Guid.NewGuid(),
                    cancellationToken).ConfigureAwait(false);
                _launchState = reality.State;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Remember(new(true, journal.Record, null, Bound(exception.Message)));
            }

            if (_launchState is null)
            {
                return Remember(new(
                    true,
                    journal.Record,
                    null,
                    "The typed launch power state could not be captured reliably."));
            }

            if (journal.Record is { } record && IsRecoveryPending(record.Status))
            {
                return Remember(new(
                    true,
                    record,
                    _launchState,
                    "Recovery is required before normal startup activation can continue."));
            }

            if (!settingsLoad.AllowsExternalSideEffects)
            {
                return Remember(new(
                    true,
                    journal.Record,
                    _launchState,
                    "Settings are corrupt; startup activation is disabled until recovery completes."));
            }

            _mutationGate.Open();
            var activationError = await ActivateOnceAsync(
                settingsLoad,
                cancellationToken).ConfigureAwait(false);
            return Remember(new(
                activationError is not null,
                journal.Record,
                _launchState,
                activationError));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAfterRecoveryAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settingsLoad is null)
                throw new InvalidOperationException(
                    "Startup must be initialized before resuming after recovery.");
            if (!_settingsLoad.AllowsExternalSideEffects)
                throw new InvalidOperationException(
                    "Settings recovery must complete before startup activation can resume.");

            var journal = await _lastOperationStore.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!journal.Succeeded)
                throw new IOException(journal.Error ?? "The last-operation journal could not be read.");
            if (journal.Record is { } record && IsRecoveryPending(record.Status))
                throw new InvalidOperationException(
                    "The last operation still requires verification or restoration.");

            if (_activationCompleted)
                return;

            if (_launchState is null)
            {
                var reality = await _backend.ReadStateAsync(
                    journal.Record?.OperationId ?? Guid.NewGuid(),
                    cancellationToken).ConfigureAwait(false);
                _launchState = reality.State ?? throw new IOException(
                    "The typed launch power state could not be captured reliably.");
            }

            _mutationGate.Open();
            var error = await ActivateOnceAsync(_settingsLoad, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
                throw new IOException(error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void AcceptRecoveredSettings(SettingsLoadResult recovered)
    {
        ArgumentNullException.ThrowIfNull(recovered);
        if (!recovered.AllowsExternalSideEffects)
            throw new InvalidOperationException(
                "Recovered settings must be usable before startup can resume.");
        _settingsLoad = recovered;
        _activationCompleted = false;
    }

    private async Task<string?> ActivateOnceAsync(
        SettingsLoadResult settingsLoad,
        CancellationToken cancellationToken)
    {
        if (_activationCompleted)
            return null;
        try
        {
            await _activateAsync(settingsLoad, cancellationToken).ConfigureAwait(false);
            _activationCompleted = true;
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Bound(exception.Message);
        }
    }

    private StartupInitializationResult Remember(StartupInitializationResult result)
    {
        _initializationResult = result;
        return result;
    }

    private static bool IsRecoveryPending(LastOperationStatus status) =>
        status is LastOperationStatus.Prepared or
            LastOperationStatus.Applying or
            LastOperationStatus.Uncertain;

    private static string Bound(string value) =>
        value.Length <= 8 * 1024 ? value : value[..(8 * 1024)];
}
