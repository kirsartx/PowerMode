namespace PowerModeWinUI;

public sealed record RecommendationContext(
    bool OnBattery,
    int? BatteryPercent,
    bool TemperatureProtectionActive,
    IReadOnlyCollection<string> RemoteProcessNames,
    IReadOnlyCollection<string> PerformanceProcessNames,
    IReadOnlyCollection<string> RunningProcessNames,
    int LowBatteryThreshold,
    HardwareCapabilities Capabilities);

public sealed record ModeRecommendation(
    string Mode,
    string Reason,
    bool IsComplete,
    DateTimeOffset GeneratedAt);
