using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class InsightsAccessibilityPresentationTests
{
    [Fact]
    public void BuildTrendSummary_WithSamples_DescribesLatestAndRange()
    {
        var text = InsightsAccessibilityPresentation.BuildTrendSummary(
            [
                new PowerTelemetrySample
                {
                    Timestamp = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
                    NvidiaGpuPowerWatts = 10,
                    HighestTemperatureCelsius = 45
                },
                new PowerTelemetrySample
                {
                    Timestamp = new DateTimeOffset(2026, 8, 9, 0, 1, 0, TimeSpan.Zero),
                    NvidiaGpuPowerWatts = 42,
                    HighestTemperatureCelsius = 78
                }
            ],
            isChinese: false);

        Assert.Contains("latest", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10", text);
        Assert.Contains("42", text);
        Assert.Contains("45", text);
        Assert.Contains("78", text);
    }

    [Fact]
    public void BuildTrendSummary_ChineseAndMissingValuesRemainMeaningful()
    {
        var empty = InsightsAccessibilityPresentation.BuildTrendSummary([], isChinese: true);
        var partial = InsightsAccessibilityPresentation.BuildTrendSummary(
            [new PowerTelemetrySample { HighestTemperatureCelsius = 67 }],
            isChinese: true);

        Assert.Equal("暂无趋势样本。", empty);
        Assert.Contains("最新样本", partial);
        Assert.Contains("GPU 功耗不可用", partial);
        Assert.Contains("67", partial);
    }
}
