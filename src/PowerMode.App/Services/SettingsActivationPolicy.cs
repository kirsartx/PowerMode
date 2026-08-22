namespace PowerModeWinUI;

internal sealed record SettingsActivationPlan(
    bool StartFeatureTimer,
    bool ConfigureMonitoring,
    bool ConfigureStartup,
    bool CreateStartupBackup,
    bool StartAutomation,
    bool ApplyLastMode,
    bool CheckUpdates);

internal static class SettingsActivationPolicy
{
    public static SettingsActivationPlan For(SettingsLoadState state) =>
        state == SettingsLoadState.Corrupt
            ? new(false, false, false, false, false, false, false)
            : new(true, true, true, true, true, true, true);
}
