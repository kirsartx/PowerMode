namespace PowerModeWinUI;

internal enum ExpensiveProbeKind
{
    Nvidia,
    BatteryHealth,
    Temperature
}

internal static class MonitoringProbePolicy
{
    public static bool ShouldRun(
        CapabilitySupport support,
        DateTimeOffset now,
        DateTimeOffset nextUnknownRetryUtc) =>
        support == CapabilitySupport.Supported ||
        support == CapabilitySupport.Unknown && now >= nextUnknownRetryUtc;

    public static CapabilitySupport CombineNvidia(
        CapabilitySupport gpu,
        CapabilitySupport nvidiaSmi) =>
        gpu == CapabilitySupport.Unsupported ||
        nvidiaSmi == CapabilitySupport.Unsupported
            ? CapabilitySupport.Unsupported
            : gpu == CapabilitySupport.Supported &&
              nvidiaSmi == CapabilitySupport.Supported
                ? CapabilitySupport.Supported
                : CapabilitySupport.Unknown;
}

internal interface IMonitoringProbeRunner
{
    Task<NvidiaTelemetry?> QueryNvidiaAsync(CancellationToken token);
    Task<double?> QueryTemperatureAsync(CancellationToken token);
    Task<BatteryHealthTelemetry> QueryBatteryHealthAsync(CancellationToken token);
    Task<string> GenerateBatteryReportAsync(
        string outputPath,
        CancellationToken token);
}

internal sealed record NvidiaTelemetry(
    double? PowerWatts,
    double? TemperatureCelsius,
    double? UtilizationPercent);

internal sealed record BatteryHealthTelemetry(
    long? DesignCapacityMWh,
    long? FullChargeCapacityMWh,
    double? HealthPercent,
    int? CycleCount)
{
    public bool HasData =>
        DesignCapacityMWh is not null ||
        FullChargeCapacityMWh is not null ||
        HealthPercent is not null ||
        CycleCount is not null;

    public static BatteryHealthTelemetry Empty { get; } =
        new(null, null, null, null);
}

internal readonly record struct MonitoringPowerStatus(
    byte? Percent,
    BatteryChargeState State,
    bool? IsOnAcPower,
    bool IsCritical,
    TimeSpan? EstimatedRemaining)
{
    public static MonitoringPowerStatus Empty { get; } =
        new(null, BatteryChargeState.Unknown, null, false, null);
}

internal interface IMonitoringSnapshotSource
{
    void UpdateCapabilities(HardwareCapabilities capabilities);

    bool TryGetRecentSample(
        TimeSpan maximumAge,
        out PowerTelemetrySample sample);

    Task<PowerTelemetrySample> GetOrSampleAsync(
        TimeSpan maximumAge,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<string> GenerateBatteryReportAsync(
        string outputPath,
        CancellationToken cancellationToken = default);
}
