namespace PowerModeWinUI;

public static class ModeRecommendationService
{
    public static ModeRecommendation Recommend(RecommendationContext context)
    {
        var running = context.RunningProcessNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (context.TemperatureProtectionActive)
            return Result("saver", "温度保护已触发，建议降低功耗", context);

        if (context.RemoteProcessNames.Any(running.Contains))
            return Result("remote", "检测到远程连接程序，建议使用远程推荐", context);

        if (context.PerformanceProcessNames.Any(running.Contains))
            return Result("high", "检测到高负载程序，建议使用高性能", context);

        if (context.OnBattery)
            return Result("saver", "当前使用电池供电，建议降低功耗", context);

        if (context.BatteryPercent is int percent && percent <= context.LowBatteryThreshold)
            return Result("saver", "当前电量较低，建议降低功耗", context);

        return Result("balanced", "当前为日常插电场景，建议使用平衡", context);
    }

    private static ModeRecommendation Result(
        string mode,
        string reason,
        RecommendationContext context) =>
        new(
            mode,
            reason,
            context.Capabilities.Battery != CapabilitySupport.Unknown,
            context.EvaluatedAt);
}
