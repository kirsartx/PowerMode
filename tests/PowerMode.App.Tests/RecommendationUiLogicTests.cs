using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class RecommendationUiLogicTests
{
    private static readonly HardwareCapabilities KnownBattery =
        HardwareCapabilities.Unknown with { Battery = CapabilitySupport.Supported };

    private static RecommendationContext Context(
        DateTimeOffset? evaluatedAt = null,
        bool onBattery = false,
        int? batteryPercent = 80,
        bool temperatureProtectionActive = false,
        string[]? runningProcessNames = null,
        string[]? remoteProcessNames = null,
        string[]? performanceProcessNames = null,
        int lowBatteryThreshold = 30,
        HardwareCapabilities? capabilities = null) =>
        new(
            onBattery,
            batteryPercent,
            temperatureProtectionActive,
            remoteProcessNames ?? ["Hermes"],
            performanceProcessNames ?? ["game"],
            runningProcessNames ?? [],
            lowBatteryThreshold,
            capabilities ?? KnownBattery,
            evaluatedAt ?? new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void CreateContext_UsesConfiguredProcessesPowerStateCapabilitiesAndCallerTime()
    {
        var settings = new PowerModeSettings
        {
            RemoteProcesses = "Hermes.exe; ToDesk",
            PerformanceProcesses = "game.exe\nobs64",
            LowBatteryThreshold = 35
        };
        var evaluatedAt = new DateTimeOffset(2026, 7, 19, 10, 30, 0, TimeSpan.FromHours(8));

        var result = RecommendationUiLogic.CreateContext(
            settings,
            KnownBattery,
            temperatureProtectionActive: true,
            new RecommendationPowerState(OnBattery: true, BatteryPercent: 34),
            ["Hermes", "explorer"],
            evaluatedAt);

        Assert.True(result.OnBattery);
        Assert.Equal(34, result.BatteryPercent);
        Assert.True(result.TemperatureProtectionActive);
        Assert.Equal(["Hermes", "ToDesk"], result.RemoteProcessNames);
        Assert.Equal(["game", "obs64"], result.PerformanceProcessNames);
        Assert.Equal(["Hermes", "explorer"], result.RunningProcessNames);
        Assert.Equal(35, result.LowBatteryThreshold);
        Assert.Same(KnownBattery, result.Capabilities);
        Assert.Equal(evaluatedAt, result.EvaluatedAt);
    }

    [Theory]
    [InlineData(true, 0, 28, true, 28)]
    [InlineData(true, 1, 100, false, 100)]
    [InlineData(true, 255, 255, null, null)]
    [InlineData(false, 0, 28, null, null)]
    public void CreatePowerState_MapsNativeUnknownValuesWithoutAssumingAc(
        bool succeeded,
        byte acLineStatus,
        byte batteryLifePercent,
        bool? expectedOnBattery,
        int? expectedBatteryPercent)
    {
        var result = RecommendationUiLogic.CreatePowerState(
            succeeded,
            acLineStatus,
            batteryLifePercent);

        Assert.Equal(expectedOnBattery, result.OnBattery);
        Assert.Equal(expectedBatteryPercent, result.BatteryPercent);
    }

    [Fact]
    public void CreateContext_UnknownPowerStateMakesBatteryCapabilityIncomplete()
    {
        var result = RecommendationUiLogic.CreateContext(
            new PowerModeSettings(),
            KnownBattery,
            temperatureProtectionActive: false,
            new RecommendationPowerState(OnBattery: null, BatteryPercent: null),
            [],
            DateTimeOffset.Now);

        Assert.Equal(CapabilitySupport.Unknown, result.Capabilities.Battery);
        Assert.False(ModeRecommendationService.Recommend(result).IsComplete);
    }

    [Fact]
    public void CreateContext_UnavailableProcessSnapshotMakesRecommendationIncomplete()
    {
        var result = RecommendationUiLogic.CreateContext(
            new PowerModeSettings(),
            KnownBattery,
            temperatureProtectionActive: false,
            new RecommendationPowerState(OnBattery: false, BatteryPercent: 80),
            [],
            DateTimeOffset.Now,
            runningProcessesAvailable: false);

        Assert.Equal(CapabilitySupport.Unknown, result.Capabilities.Battery);
        Assert.False(ModeRecommendationService.Recommend(result).IsComplete);
    }

    [Fact]
    public void NeedsRefresh_IgnoresEvaluationTimeAndBatteryChangesOnSameSideOfThreshold()
    {
        var previous = Context(
            evaluatedAt: new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero),
            batteryPercent: 82);
        var next = Context(
            evaluatedAt: new DateTimeOffset(2026, 7, 19, 10, 1, 0, TimeSpan.Zero),
            batteryPercent: 81);

        Assert.False(RecommendationUiLogic.NeedsRefresh(previous, next));
    }

    [Theory]
    [InlineData("power")]
    [InlineData("threshold")]
    [InlineData("temperature")]
    [InlineData("process")]
    [InlineData("configuration")]
    [InlineData("capability")]
    public void NeedsRefresh_DetectsMeaningfulRecommendationInputChanges(string change)
    {
        var previous = Context();
        var next = change switch
        {
            "power" => Context(onBattery: true),
            "threshold" => Context(batteryPercent: 30),
            "temperature" => Context(temperatureProtectionActive: true),
            "process" => Context(runningProcessNames: ["game"]),
            "configuration" => Context(remoteProcessNames: ["ToDesk"]),
            "capability" => Context(capabilities: HardwareCapabilities.Unknown),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        Assert.True(RecommendationUiLogic.NeedsRefresh(previous, next));
    }

    [Fact]
    public void CreatePresentation_IncompleteRecommendationExplainsDetectionState()
    {
        var recommendation = new ModeRecommendation(
            "balanced",
            "当前为日常插电场景，建议使用平衡",
            IsComplete: false,
            DateTimeOffset.Now);

        var result = RecommendationUiLogic.CreatePresentation(
            recommendation,
            modeDisplayName: "平衡",
            isChinese: true);

        Assert.Equal("建议：平衡 · 检测中", result.Title);
        Assert.Equal("信息不完整：当前为日常插电场景，建议使用平衡", result.Reason);
        Assert.Equal("一键应用", result.ApplyText);
    }

    [Fact]
    public void CreateApplyRequest_PreservesModeReasonAndExplicitRecommendationTrigger()
    {
        var recommendation = new ModeRecommendation(
            "high",
            "检测到高负载程序",
            IsComplete: true,
            DateTimeOffset.Now);

        var result = RecommendationUiLogic.CreateApplyRequest(recommendation);

        Assert.Equal("high", result.Mode);
        Assert.Equal("recommendation", result.Trigger);
        Assert.Equal("检测到高负载程序", result.Reason);
        Assert.False(result.AllowPreview);
    }
}
