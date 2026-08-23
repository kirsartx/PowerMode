namespace PowerModeWinUI;

internal static class InsightsAccessibilityPresentation
{
    public static string BuildTrendSummary(
        IReadOnlyList<PowerTelemetrySample> samples,
        bool isChinese)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            return isChinese ? "暂无趋势样本。" : "No trend samples are available.";

        var latest = samples[^1];
        var temperatureValues = samples
            .Select(sample => sample.HighestTemperatureCelsius)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        var gpuValues = samples
            .Select(sample => sample.NvidiaGpuPowerWatts)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();

        var gpu = Describe(
            latest.NvidiaGpuPowerWatts,
            gpuValues,
            " W",
            isChinese ? "GPU 功耗" : "GPU power",
            isChinese);
        var temperature = Describe(
            latest.HighestTemperatureCelsius,
            temperatureValues,
            " °C",
            isChinese ? "最高温度" : "highest temperature",
            isChinese);
        return isChinese
            ? $"最新样本：{gpu}；{temperature}。"
            : $"Latest sample: {gpu}; {temperature}.";
    }

    private static string Describe(
        double? latest,
        IReadOnlyList<double> values,
        string suffix,
        string label,
        bool isChinese)
    {
        if (!latest.HasValue || values.Count == 0)
            return isChinese ? $"{label}不可用" : $"{label} unavailable";

        return isChinese
            ? $"{label} {latest.Value:0.#}{suffix}，范围 " +
                $"{values.Min():0.#}–{values.Max():0.#}{suffix}"
            : $"{label} {latest.Value:0.#}{suffix}, range " +
                $"{values.Min():0.#}–{values.Max():0.#}{suffix}";
    }
}
