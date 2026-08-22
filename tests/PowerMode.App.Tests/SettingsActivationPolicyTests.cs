using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class SettingsActivationPolicyTests
{
    [Fact]
    public void For_Corrupt_DisablesEverySettingsDerivedSideEffect()
    {
        var plan = SettingsActivationPolicy.For(SettingsLoadState.Corrupt);

        Assert.False(plan.StartFeatureTimer);
        Assert.False(plan.ConfigureMonitoring);
        Assert.False(plan.ConfigureStartup);
        Assert.False(plan.CreateStartupBackup);
        Assert.False(plan.StartAutomation);
        Assert.False(plan.ApplyLastMode);
        Assert.False(plan.CheckUpdates);
    }

    [Theory]
    [InlineData(SettingsLoadState.FirstRun)]
    [InlineData(SettingsLoadState.Loaded)]
    [InlineData(SettingsLoadState.Migrated)]
    public void For_UsableStates_AllowsActivation(SettingsLoadState state)
    {
        var plan = SettingsActivationPolicy.For(state);

        Assert.True(plan.StartFeatureTimer);
        Assert.True(plan.ConfigureMonitoring);
        Assert.True(plan.ConfigureStartup);
        Assert.True(plan.CreateStartupBackup);
        Assert.True(plan.StartAutomation);
        Assert.True(plan.ApplyLastMode);
        Assert.True(plan.CheckUpdates);
    }
}
