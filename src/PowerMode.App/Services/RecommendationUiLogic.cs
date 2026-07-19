namespace PowerModeWinUI;

internal sealed record RecommendationPowerState(bool? OnBattery, int? BatteryPercent);

internal sealed record RecommendationPresentation(
    string Title,
    string Reason,
    string ApplyText,
    string AutomationHelpText);

internal sealed record RecommendationApplyRequest(
    string Mode,
    string Trigger,
    string Reason,
    bool AllowPreview);

internal sealed record RecommendationApplyButtonState(bool IsEnabled, string Text);

internal sealed class RecommendationApplyGate
{
    private int _entered;

    public bool IsEntered => Volatile.Read(ref _entered) != 0;

    public async Task<bool> TryRunAsync(Func<Task> action)
    {
        if (Interlocked.CompareExchange(ref _entered, 1, 0) != 0)
            return false;

        try
        {
            await action();
            return true;
        }
        finally
        {
            Volatile.Write(ref _entered, 0);
        }
    }
}

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
            OnBattery: powerState.OnBattery,
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
        var localizedReason = LocalizeReason(recommendation.ReasonCode, isChinese);
        var visibleReason = recommendation.IsComplete
            ? localizedReason
            : isChinese
                ? $"信息不完整：{localizedReason}"
                : $"Information incomplete: {localizedReason}";
        if (isChinese)
        {
            return new(
                recommendation.IsComplete
                    ? $"建议：{modeDisplayName}"
                    : $"建议：{modeDisplayName} · 检测中",
                visibleReason,
                "一键应用",
                visibleReason);
        }

        return new(
            recommendation.IsComplete
                ? $"Suggested: {modeDisplayName}"
                : $"Suggested: {modeDisplayName} · Detecting",
            visibleReason,
            "Apply",
            visibleReason);
    }

    public static RecommendationApplyRequest CreateApplyRequest(
        ModeRecommendation recommendation,
        string visibleReason) =>
        new(
            recommendation.Mode,
            Trigger: "recommendation",
            visibleReason,
            AllowPreview: false);

    public static RecommendationApplyButtonState CreateApplyButtonState(
        ModeRecommendation recommendation,
        string? currentSuccessfulMode,
        bool isApplying,
        bool isChinese)
    {
        if (isApplying)
            return new(false, isChinese ? "应用中…" : "Applying…");

        if (string.Equals(
                recommendation.Mode.Trim(),
                currentSuccessfulMode?.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return new(false, isChinese ? "当前模式" : "Current mode");

        return new(true, isChinese ? "一键应用" : "Apply");
    }

    private static string LocalizeReason(RecommendationReasonCode reasonCode, bool isChinese) =>
        (reasonCode, isChinese) switch
        {
            (RecommendationReasonCode.TemperatureProtection, true) =>
                "温度保护已触发，建议降低功耗",
            (RecommendationReasonCode.TemperatureProtection, false) =>
                "Temperature protection is active; lower power use is recommended",
            (RecommendationReasonCode.RemoteProcess, true) =>
                "检测到远程连接程序，建议使用远程推荐",
            (RecommendationReasonCode.RemoteProcess, false) =>
                "A remote-access app is running; Remote mode is recommended",
            (RecommendationReasonCode.PerformanceProcess, true) =>
                "检测到高负载程序，建议使用高性能",
            (RecommendationReasonCode.PerformanceProcess, false) =>
                "A high-load app is running; High performance is recommended",
            (RecommendationReasonCode.BatteryPower, true) =>
                "当前使用电池供电，建议降低功耗",
            (RecommendationReasonCode.BatteryPower, false) =>
                "The device is on battery power; lower power use is recommended",
            (RecommendationReasonCode.LowBattery, true) =>
                "当前电量较低，建议降低功耗",
            (RecommendationReasonCode.LowBattery, false) =>
                "Battery level is low; lower power use is recommended",
            (RecommendationReasonCode.DailyAc, true) =>
                "当前为日常插电场景，建议使用平衡",
            (RecommendationReasonCode.DailyAc, false) =>
                "The device is plugged in for everyday use; Balanced is recommended",
            (RecommendationReasonCode.PowerStateUnknown, true) =>
                "供电状态未知，暂时建议使用平衡模式",
            (RecommendationReasonCode.PowerStateUnknown, false) =>
                "Power source is unavailable; Balanced is a conservative recommendation",
            (_, true) => "建议使用所示模式",
            _ => "The shown mode is recommended"
        };

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
        bool? OnBattery,
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
