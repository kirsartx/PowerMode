using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class RecommendationUiLogicTests
{
    private static readonly HardwareCapabilities KnownBattery =
        HardwareCapabilities.Unknown with { Battery = CapabilitySupport.Supported };

    private static RecommendationContext Context(
        DateTimeOffset? evaluatedAt = null,
        bool? onBattery = false,
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
        Assert.Null(result.OnBattery);
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

    [Fact]
    public void NeedsRefresh_UnknownPowerBecomingKnownAc_Refreshes()
    {
        var previous = Context(onBattery: null, batteryPercent: null);
        var next = Context(onBattery: false, batteryPercent: 80);

        Assert.True(RecommendationUiLogic.NeedsRefresh(previous, next));
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
            "legacy raw reason",
            IsComplete: false,
            DateTimeOffset.Now,
            RecommendationReasonCode.PowerStateUnknown);

        var result = RecommendationUiLogic.CreatePresentation(
            recommendation,
            modeDisplayName: "平衡",
            isChinese: true);

        Assert.Equal("建议：平衡 · 检测中", result.Title);
        Assert.Equal("信息不完整：供电状态未知，暂时建议使用平衡模式", result.Reason);
        Assert.Equal("一键应用", result.ApplyText);
        Assert.Equal(result.Reason, result.AutomationHelpText);
    }

    [Fact]
    public void CreatePresentation_EnglishLocalizesReasonWithoutChineseServiceText()
    {
        var recommendation = new ModeRecommendation(
            "high",
            "检测到高负载程序，建议使用高性能",
            IsComplete: true,
            DateTimeOffset.Now,
            RecommendationReasonCode.PerformanceProcess);

        var result = RecommendationUiLogic.CreatePresentation(
            recommendation,
            modeDisplayName: "High performance",
            isChinese: false);

        Assert.Equal("Suggested: High performance", result.Title);
        Assert.Equal("A high-load app is running; High performance is recommended", result.Reason);
        Assert.Equal(result.Reason, result.AutomationHelpText);
        Assert.DoesNotMatch("[\u4e00-\u9fff]", result.Reason);
    }

    [Fact]
    public void CreatePresentation_EnglishIncompleteReasonIsFullyLocalized()
    {
        var recommendation = new ModeRecommendation(
            "balanced",
            "供电状态未知，暂时建议使用平衡模式",
            IsComplete: false,
            DateTimeOffset.Now,
            RecommendationReasonCode.PowerStateUnknown);

        var result = RecommendationUiLogic.CreatePresentation(
            recommendation,
            modeDisplayName: "Balanced",
            isChinese: false);

        Assert.Equal(
            "Information incomplete: Power source is unavailable; Balanced is a conservative recommendation",
            result.Reason);
        Assert.Equal(result.Reason, result.AutomationHelpText);
        Assert.DoesNotMatch("[\u4e00-\u9fff]", result.Reason);
    }

    [Fact]
    public void CreateApplyRequest_PreservesModeReasonAndExplicitRecommendationTrigger()
    {
        var recommendation = new ModeRecommendation(
            "high",
            "检测到高负载程序",
            IsComplete: true,
            DateTimeOffset.Now,
            RecommendationReasonCode.PerformanceProcess);
        const string visibleReason =
            "A high-load app is running; High performance is recommended";

        var result = RecommendationUiLogic.CreateApplyRequest(recommendation, visibleReason);

        Assert.Equal("high", result.Mode);
        Assert.Equal("recommendation", result.Trigger);
        Assert.Equal(visibleReason, result.Reason);
        Assert.False(result.AllowPreview);
    }

    [Theory]
    [InlineData("balanced", "balanced", false, true, false, "当前模式")]
    [InlineData("balanced", "balanced", false, false, false, "Current mode")]
    [InlineData("balanced", "saver", true, true, false, "应用中…")]
    [InlineData("balanced", "saver", true, false, false, "Applying…")]
    [InlineData("balanced", "saver", false, true, true, "一键应用")]
    [InlineData("balanced", "saver", false, false, true, "Apply")]
    public void CreateApplyButtonState_MapsCurrentApplyingAndReadyStates(
        string recommendationMode,
        string currentSuccessfulMode,
        bool isApplying,
        bool isChinese,
        bool expectedEnabled,
        string expectedText)
    {
        var recommendation = new ModeRecommendation(
            recommendationMode,
            "reason",
            IsComplete: true,
            DateTimeOffset.Now,
            RecommendationReasonCode.DailyAc);

        var result = RecommendationUiLogic.CreateApplyButtonState(
            recommendation,
            currentSuccessfulMode,
            isApplying,
            isChinese);

        Assert.Equal(expectedEnabled, result.IsEnabled);
        Assert.Equal(expectedText, result.Text);
    }

    [Fact]
    public async Task RecommendationApplyGate_ConcurrentSecondRequestDoesNotEnterAction()
    {
        var gate = new RecommendationApplyGate();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actionCalls = 0;

        var first = gate.TryRunAsync(async () =>
        {
            Interlocked.Increment(ref actionCalls);
            firstEntered.SetResult();
            await releaseFirst.Task;
        });
        await firstEntered.Task;

        var second = await gate.TryRunAsync(() =>
        {
            Interlocked.Increment(ref actionCalls);
            return Task.CompletedTask;
        });

        Assert.False(second);
        Assert.True(gate.IsEntered);
        Assert.Equal(1, actionCalls);

        releaseFirst.SetResult();
        Assert.True(await first);
        Assert.False(gate.IsEntered);
    }
}
