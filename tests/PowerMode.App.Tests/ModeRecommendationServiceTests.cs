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
    public void Recommend_UnknownPower_ReturnsIncompleteNeutralReason()
    {
        var result = ModeRecommendationService.Recommend(Context() with
        {
            OnBattery = null,
            Capabilities = HardwareCapabilities.Unknown with
            {
                Battery = CapabilitySupport.Supported
            }
        });

        Assert.Equal("balanced", result.Mode);
        Assert.False(result.IsComplete);
        Assert.Contains("供电状态未知", result.Reason);
        Assert.DoesNotContain("插电", result.Reason);
        Assert.DoesNotContain("使用电池", result.Reason);
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

    [Theory]
    [InlineData("temperature", RecommendationReasonCode.TemperatureProtection)]
    [InlineData("remote", RecommendationReasonCode.RemoteProcess)]
    [InlineData("performance", RecommendationReasonCode.PerformanceProcess)]
    [InlineData("battery", RecommendationReasonCode.BatteryPower)]
    [InlineData("low-battery", RecommendationReasonCode.LowBattery)]
    [InlineData("ac", RecommendationReasonCode.DailyAc)]
    [InlineData("unknown", RecommendationReasonCode.PowerStateUnknown)]
    public void Recommend_AssignsStableReasonCode(string scenario, RecommendationReasonCode expected)
    {
        var context = Context() with
        {
            OnBattery = scenario switch
            {
                "battery" => true,
                "unknown" => null,
                _ => false
            },
            BatteryPercent = scenario == "low-battery" ? 30 : 100,
            TemperatureProtectionActive = scenario == "temperature",
            RemoteProcessNames = scenario == "remote" ? ["Hermes"] : [],
            PerformanceProcessNames = scenario == "performance" ? ["game"] : [],
            RunningProcessNames = scenario switch
            {
                "remote" => ["Hermes"],
                "performance" => ["game"],
                _ => []
            }
        };

        Assert.Equal(expected, ModeRecommendationService.Recommend(context).ReasonCode);
    }
}
