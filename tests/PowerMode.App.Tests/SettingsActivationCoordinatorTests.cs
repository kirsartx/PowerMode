using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class SettingsActivationCoordinatorTests
{
    [Fact]
    public async Task CorruptSettings_DisablesAllEffectsAndPersistence()
    {
        var effects = new CountingEffects();
        var result = new SettingsLoadResult(
            SettingsLoadState.Corrupt,
            new PowerModeSettings(),
            Error: "invalid settings");
        var coordinator = new SettingsActivationCoordinator(result, effects);

        await coordinator.ActivateAsync(SettingsActivationPolicy.For(result.State));

        Assert.False(coordinator.CanPersistSettings);
        Assert.Empty(effects.Calls);
    }

    [Fact]
    public async Task LoadedSettings_ActivatesEveryEnabledEffect()
    {
        var effects = new CountingEffects();
        var result = new SettingsLoadResult(
            SettingsLoadState.Loaded,
            new PowerModeSettings());
        var coordinator = new SettingsActivationCoordinator(result, effects);

        await coordinator.ActivateAsync(SettingsActivationPolicy.For(result.State));

        Assert.True(coordinator.CanPersistSettings);
        Assert.Equal(
            [
                "timer",
                "monitoring",
                "startup",
                "backup",
                "automation",
                "last-mode",
                "updates"
            ],
            effects.Calls);
    }

    [Fact]
    public void AcceptRecoveredSettings_ReenablesPersistence()
    {
        var effects = new CountingEffects();
        var coordinator = new SettingsActivationCoordinator(
            new SettingsLoadResult(
                SettingsLoadState.Corrupt,
                new PowerModeSettings()),
            effects);

        coordinator.AcceptRecoveredSettings(new SettingsLoadResult(
            SettingsLoadState.Loaded,
            new PowerModeSettings()));

        Assert.True(coordinator.CanPersistSettings);
    }

    private sealed class CountingEffects : ISettingsActivationEffects
    {
        public List<string> Calls { get; } = [];

        public void StartFeatureTimer() => Calls.Add("timer");
        public void ConfigureMonitoring() => Calls.Add("monitoring");
        public void ConfigureStartup() => Calls.Add("startup");
        public Task CreateStartupBackupAsync(CancellationToken cancellationToken)
        {
            Calls.Add("backup");
            return Task.CompletedTask;
        }
        public void StartAutomation() => Calls.Add("automation");
        public Task ApplyLastModeAsync(CancellationToken cancellationToken)
        {
            Calls.Add("last-mode");
            return Task.CompletedTask;
        }
        public Task CheckUpdatesAsync(CancellationToken cancellationToken)
        {
            Calls.Add("updates");
            return Task.CompletedTask;
        }
    }
}
