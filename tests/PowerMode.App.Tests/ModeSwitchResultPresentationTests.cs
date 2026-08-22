using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ModeSwitchResultPresentationTests
{
    [Fact]
    public void CustomPowerProfileSnapshot_FromSettingsMapsEveryFieldWithoutRetainingSettingsModel()
    {
        var settings = new CustomPowerProfile
        {
            Name = "Quiet",
            CpuMax = 47,
            BatteryCpuMax = 33,
            CpuMin = 7,
            Brightness = 71,
            BatteryBrightness = 42,
            DisplayOffSeconds = 420,
            BatteryDisplayOffSeconds = 150,
            DisableBoost = true,
            UseSeparateBatteryValues = true
        };

        var snapshot = CustomPowerProfileSnapshot.FromSettings(settings);

        Assert.Equal("Quiet", snapshot.Name);
        Assert.Equal(47, snapshot.CpuMaximumAcPercent);
        Assert.Equal(33, snapshot.CpuMaximumDcPercent);
        Assert.Equal(7, snapshot.CpuMinimumPercent);
        Assert.Equal(71, snapshot.BrightnessAcPercent);
        Assert.Equal(42, snapshot.BrightnessDcPercent);
        Assert.Equal(420, snapshot.DisplayTimeoutAcSeconds);
        Assert.Equal(150, snapshot.DisplayTimeoutDcSeconds);
        Assert.True(snapshot.DisableBoost);

        settings.Name = "Mutated";
        settings.CpuMax = 99;
        Assert.Equal("Quiet", snapshot.Name);
        Assert.Equal(47, snapshot.CpuMaximumAcPercent);
    }

    [Fact]
    public void CustomPowerProfileSnapshot_SharedBatteryValuesCopyEveryAcField()
    {
        var snapshot = CustomPowerProfileSnapshot.FromSettings(new CustomPowerProfile
        {
            Name = "Shared",
            CpuMax = 44,
            CpuMin = 6,
            Brightness = 64,
            DisplayOffSeconds = 240,
            DisableBoost = false,
            UseSeparateBatteryValues = false,
            BatteryCpuMax = 21,
            BatteryBrightness = 22,
            BatteryDisplayOffSeconds = 23
        });

        Assert.Equal(snapshot.CpuMaximumAcPercent, snapshot.CpuMaximumDcPercent);
        Assert.Equal(snapshot.BrightnessAcPercent, snapshot.BrightnessDcPercent);
        Assert.Equal(snapshot.DisplayTimeoutAcSeconds, snapshot.DisplayTimeoutDcSeconds);
        Assert.False(snapshot.DisableBoost);
    }

    [Fact]
    public void PartialResult_ShowsWarningAndReviewAction()
    {
        var state = ModeSwitchResultPresentationPolicy.Create(
            Result(ModeSwitchOutcome.Partial),
            ExperienceMode.Simple,
            isChinese: true);

        Assert.Equal(ModeSwitchPresentationSeverity.Warning, state.Severity);
        Assert.Equal(ModeSwitchPresentationAction.ReviewPartialDetails, state.Action);
        Assert.Contains("检查", state.ActionText);
        Assert.False(state.IsPersistent);
    }

    [Fact]
    public void FailedResult_OpensRecoveryCenter()
    {
        var state = ModeSwitchResultPresentationPolicy.Create(
            Result(ModeSwitchOutcome.Failed),
            ExperienceMode.Simple,
            isChinese: false);

        Assert.Equal(ModeSwitchPresentationSeverity.Error, state.Severity);
        Assert.Equal(ModeSwitchPresentationAction.OpenRecoveryCenter, state.Action);
        Assert.Contains("Recovery", state.ActionText);
    }

    [Fact]
    public void RollbackFailedResult_IsPersistentAndOffersImmediateRecovery()
    {
        var state = ModeSwitchResultPresentationPolicy.Create(
            Result(ModeSwitchOutcome.RollbackFailed),
            ExperienceMode.Professional,
            isChinese: true);

        Assert.True(state.IsPersistent);
        Assert.Equal(ModeSwitchPresentationAction.OpenRecoveryCenter, state.Action);
        Assert.Contains("立即", state.ActionText);
    }

    [Fact]
    public void JournalError_MakesOtherwiseSuccessfulResultPersistent()
    {
        var state = ModeSwitchResultPresentationPolicy.Create(
            Result(ModeSwitchOutcome.Succeeded) with
            {
                RequiresRecovery = true,
                JournalError = "terminal journal failure"
            },
            ExperienceMode.Simple,
            isChinese: false);

        Assert.Equal(ModeSwitchPresentationSeverity.Error, state.Severity);
        Assert.True(state.IsPersistent);
        Assert.Equal(ModeSwitchPresentationAction.OpenRecoveryCenter, state.Action);
    }

    [Fact]
    public void SimpleMode_HidesRawDiagnostics()
    {
        var state = ModeSwitchResultPresentationPolicy.Create(
            Result(ModeSwitchOutcome.Failed),
            ExperienceMode.Simple,
            isChinese: false);

        Assert.False(state.ShowDiagnosticDetails);
        Assert.Contains("failed", state.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfessionalMode_ExposesCopyableDiagnostics()
    {
        var state = ModeSwitchResultPresentationPolicy.Create(
            Result(ModeSwitchOutcome.Failed),
            ExperienceMode.Professional,
            isChinese: false);

        Assert.True(state.ShowDiagnosticDetails);
        Assert.Contains("failed", state.AccessibleName, StringComparison.OrdinalIgnoreCase);
    }

    private static ModeSwitchResult Result(ModeSwitchOutcome outcome) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        outcome,
        null,
        null,
        [],
        null,
        outcome == ModeSwitchOutcome.Partial
            ? "Power mode applied with optional differences."
            : "Power mode failed.",
        "operationId=11111111-1111-1111-1111-111111111111; cpu mismatch");
}
