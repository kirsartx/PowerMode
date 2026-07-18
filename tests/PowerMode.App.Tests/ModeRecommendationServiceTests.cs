using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ModeRecommendationServiceTests
{
    private static RecommendationContext Context() => new(
        OnBattery: false,
        BatteryPercent: 100,
        TemperatureProtectionActive: false,
        RemoteProcessNames: [],
        PerformanceProcessNames: [],
        RunningProcessNames: [],
        LowBatteryThreshold: 30,
        Capabilities: HardwareCapabilities.Unknown,
        EvaluatedAt: new DateTimeOffset(2026, 7, 19, 9, 30, 0, TimeSpan.Zero));

    [Fact]
    public void Recommend_SameContext_ReturnsEqualCompleteRecommendation()
    {
        var context = Context();

        var first = ModeRecommendationService.Recommend(context);
        var second = ModeRecommendationService.Recommend(context);

        Assert.Equal(first, second);
        Assert.Equal(context.EvaluatedAt, first.GeneratedAt);
    }

    [Fact]
    public void Recommend_TemperatureProtection_WinsOverPerformanceProcess()
    {
        var context = Context() with
        {
            TemperatureProtectionActive = true,
            PerformanceProcessNames = ["game"],
            RunningProcessNames = ["game"]
        };

        var result = ModeRecommendationService.Recommend(context);

        Assert.Equal("saver", result.Mode);
        Assert.Contains("温度", result.Reason);
    }

    [Fact]
    public void Recommend_TemperatureProtection_WinsOverRemoteProcess()
    {
        var context = Context() with
        {
            TemperatureProtectionActive = true,
            RemoteProcessNames = ["Hermes"],
            RunningProcessNames = ["Hermes"]
        };

        var result = ModeRecommendationService.Recommend(context);

        Assert.Equal("saver", result.Mode);
        Assert.Contains("温度", result.Reason);
    }

    [Fact]
    public void Recommend_RemoteProcess_WinsOverBattery()
    {
        var context = Context() with
        {
            OnBattery = true,
            RemoteProcessNames = ["Hermes"],
            RunningProcessNames = ["Hermes"]
        };

        Assert.Equal("remote", ModeRecommendationService.Recommend(context).Mode);
    }

    [Fact]
    public void Recommend_RemoteProcess_WinsOverPerformanceProcess()
    {
        var context = Context() with
        {
            RemoteProcessNames = ["Hermes"],
            PerformanceProcessNames = ["game"],
            RunningProcessNames = ["Hermes", "game"]
        };

        Assert.Equal("remote", ModeRecommendationService.Recommend(context).Mode);
    }

    [Fact]
    public void Recommend_PerformanceProcess_ReturnsHigh()
    {
        var context = Context() with
        {
            PerformanceProcessNames = ["game"],
            RunningProcessNames = ["GAME"]
        };

        Assert.Equal("high", ModeRecommendationService.Recommend(context).Mode);
    }

    [Fact]
    public void Recommend_Battery_ReturnsSaver()
    {
        var result = ModeRecommendationService.Recommend(Context() with { OnBattery = true });

        Assert.Equal("saver", result.Mode);
        Assert.Equal("当前使用电池供电，建议降低功耗", result.Reason);
    }

    [Fact]
    public void Recommend_LowBatteryPercentage_ReturnsSaver()
    {
        var result = ModeRecommendationService.Recommend(Context() with { BatteryPercent = 30 });

        Assert.Equal("saver", result.Mode);
        Assert.Equal("当前电量较低，建议降低功耗", result.Reason);
    }

    [Fact]
    public void Recommend_DefaultAc_ReturnsBalanced()
    {
        Assert.Equal("balanced", ModeRecommendationService.Recommend(Context()).Mode);
    }

    [Fact]
    public void Recommend_IsCompleteOnlyWhenBatteryCapabilityIsKnown()
    {
        var complete = Context() with
        {
            Capabilities = HardwareCapabilities.Unknown with { Battery = CapabilitySupport.Supported }
        };

        Assert.True(ModeRecommendationService.Recommend(complete).IsComplete);
        Assert.False(ModeRecommendationService.Recommend(Context()).IsComplete);
    }
}
