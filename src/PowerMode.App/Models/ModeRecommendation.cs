namespace PowerModeWinUI;

public enum RecommendationReasonCode
{
    Unspecified,
    TemperatureProtection,
    RemoteProcess,
    PerformanceProcess,
    BatteryPower,
    LowBattery,
    DailyAc,
    PowerStateUnknown
}

public sealed record RecommendationContext(
    bool? OnBattery,
    int? BatteryPercent,
    bool TemperatureProtectionActive,
    IReadOnlyCollection<string> RemoteProcessNames,
    IReadOnlyCollection<string> PerformanceProcessNames,
    IReadOnlyCollection<string> RunningProcessNames,
    int LowBatteryThreshold,
    HardwareCapabilities Capabilities,
    DateTimeOffset EvaluatedAt);

public sealed record ModeRecommendation(
    string Mode,
    string Reason,
    bool IsComplete,
    DateTimeOffset GeneratedAt,
    RecommendationReasonCode ReasonCode = RecommendationReasonCode.Unspecified);
