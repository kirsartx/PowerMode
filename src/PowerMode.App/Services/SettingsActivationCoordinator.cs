namespace PowerModeWinUI;

internal interface ISettingsActivationEffects
{
    void StartFeatureTimer();
    void ConfigureMonitoring();
    void ConfigureStartup();
    Task CreateStartupBackupAsync(CancellationToken cancellationToken);
    void StartAutomation();
    Task ApplyLastModeAsync(CancellationToken cancellationToken);
    Task CheckUpdatesAsync(CancellationToken cancellationToken);
}

internal interface ISettingsActivationCoordinator
{
    bool CanPersistSettings { get; }
    Task ActivateAsync(
        SettingsActivationPlan plan,
        CancellationToken cancellationToken = default);
    void AcceptRecoveredSettings(SettingsLoadResult recovered);
}

internal sealed class SettingsActivationCoordinator
    : ISettingsActivationCoordinator
{
    private SettingsLoadResult _loadResult;
    private readonly ISettingsActivationEffects _effects;

    public SettingsActivationCoordinator(
        SettingsLoadResult loadResult,
        ISettingsActivationEffects effects)
    {
        _loadResult = loadResult ?? throw new ArgumentNullException(nameof(loadResult));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
    }

    public bool CanPersistSettings => _loadResult.AllowsExternalSideEffects;

    public async Task ActivateAsync(
        SettingsActivationPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (!CanPersistSettings)
        {
            return;
        }

        if (plan.StartFeatureTimer)
            _effects.StartFeatureTimer();
        if (plan.ConfigureMonitoring)
            _effects.ConfigureMonitoring();
        if (plan.ConfigureStartup)
            _effects.ConfigureStartup();
        if (plan.CreateStartupBackup)
            await _effects.CreateStartupBackupAsync(cancellationToken).ConfigureAwait(false);
        if (plan.StartAutomation)
            _effects.StartAutomation();
        if (plan.ApplyLastMode)
            await _effects.ApplyLastModeAsync(cancellationToken).ConfigureAwait(false);
        if (plan.CheckUpdates)
            await _effects.CheckUpdatesAsync(cancellationToken).ConfigureAwait(false);
    }

    public void AcceptRecoveredSettings(SettingsLoadResult recovered)
    {
        ArgumentNullException.ThrowIfNull(recovered);
        if (!recovered.AllowsExternalSideEffects)
            throw new InvalidOperationException(
                "Recovered settings must be usable before activation.");
        _loadResult = recovered;
    }
}
