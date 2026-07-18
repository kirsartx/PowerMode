namespace PowerModeWinUI;

internal sealed record RecommendationPowerState(bool? OnBattery, int? BatteryPercent);

internal sealed record RecommendationPresentation(string Title, string Reason, string ApplyText);

internal sealed record RecommendationApplyRequest(
    string Mode,
    string Trigger,
    string Reason,
    bool AllowPreview);

internal static class RecommendationUiLogic
{
    public static RecommendationPowerState CreatePowerState(
        bool succeeded,
        byte acLineStatus,
        byte batteryLifePercent)
    {
        if (!succeeded || acLineStatus is not (0 or 1))
            return new(null, null);

        return new(
            OnBattery: acLineStatus == 0,
            BatteryPercent: batteryLifePercent <= 100 ? batteryLifePercent : null);
    }

    public static RecommendationContext CreateContext(
        PowerModeSettings settings,
        HardwareCapabilities capabilities,
        bool temperatureProtectionActive,
        RecommendationPowerState powerState,
        IReadOnlyCollection<string> runningProcessNames,
        DateTimeOffset evaluatedAt,
        bool runningProcessesAvailable = true)
    {
        var effectiveCapabilities = powerState.OnBattery.HasValue && runningProcessesAvailable
            ? capabilities
            : capabilities with { Battery = CapabilitySupport.Unknown };

        return new(
            OnBattery: powerState.OnBattery == true,
            BatteryPercent: powerState.BatteryPercent,
            TemperatureProtectionActive: temperatureProtectionActive,
            RemoteProcessNames: SplitProcessNames(settings.RemoteProcesses),
            PerformanceProcessNames: SplitProcessNames(settings.PerformanceProcesses),
            RunningProcessNames: runningProcessNames.ToArray(),
            LowBatteryThreshold: settings.LowBatteryThreshold,
            Capabilities: effectiveCapabilities,
            EvaluatedAt: evaluatedAt);
    }

    public static bool NeedsRefresh(
        RecommendationContext? previous,
        RecommendationContext next) =>
        previous is null || CreateRefreshKey(previous) != CreateRefreshKey(next);

    public static RecommendationPresentation CreatePresentation(
        ModeRecommendation recommendation,
        string modeDisplayName,
        bool isChinese)
    {
        if (isChinese)
        {
            return new(
                recommendation.IsComplete
                    ? $"建议：{modeDisplayName}"
                    : $"建议：{modeDisplayName} · 检测中",
                recommendation.IsComplete
                    ? recommendation.Reason
                    : $"信息不完整：{recommendation.Reason}",
                "一键应用");
        }

        return new(
            recommendation.IsComplete
                ? $"Suggested: {modeDisplayName}"
                : $"Suggested: {modeDisplayName} · Detecting",
            recommendation.IsComplete
                ? recommendation.Reason
                : $"Information incomplete: {recommendation.Reason}",
            "Apply");
    }

    public static RecommendationApplyRequest CreateApplyRequest(ModeRecommendation recommendation) =>
        new(
            recommendation.Mode,
            Trigger: "recommendation",
            recommendation.Reason,
            AllowPreview: false);

    private static RecommendationRefreshKey CreateRefreshKey(RecommendationContext context)
    {
        var running = context.RunningProcessNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(
            context.OnBattery,
            context.BatteryPercent.HasValue,
            context.BatteryPercent is int percent && percent <= context.LowBatteryThreshold,
            context.TemperatureProtectionActive,
            context.RemoteProcessNames.Any(running.Contains),
            context.PerformanceProcessNames.Any(running.Contains),
            NormalizeNames(context.RemoteProcessNames),
            NormalizeNames(context.PerformanceProcessNames),
            context.LowBatteryThreshold,
            context.Capabilities);
    }

    private static string[] SplitProcessNames(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => Path.GetFileNameWithoutExtension(name) ?? name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

    private static string NormalizeNames(IEnumerable<string> names) =>
        string.Join(
            "\n",
            names
                .Select(name => (Path.GetFileNameWithoutExtension(name) ?? name).Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => name.ToUpperInvariant()));

    private sealed record RecommendationRefreshKey(
        bool OnBattery,
        bool BatteryPercentKnown,
        bool LowBattery,
        bool TemperatureProtectionActive,
        bool RemoteProcessRunning,
        bool PerformanceProcessRunning,
        string RemoteProcesses,
        string PerformanceProcesses,
        int LowBatteryThreshold,
        HardwareCapabilities Capabilities);
}
