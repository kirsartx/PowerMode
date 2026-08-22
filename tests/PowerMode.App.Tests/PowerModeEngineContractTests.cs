using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class PowerModeEngineContractTests
{
    [Fact]
    public void Parse_V1Status_MapsStronglyTypedState()
    {
        var result = PowerModeEngineContract.Parse(
            TestPaths.ReadFixture("engine-status-v1.json"));

        Assert.Equal(BackendOperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(
            PowerModePreset.Balanced,
            result.AfterState!.DetectedMode);
        Assert.Equal(100, result.AfterState.CpuMaximumAcPercent);
        Assert.Equal(
            PowerSourceKind.Ac,
            result.AfterState.PowerSource);
        Assert.Equal(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            result.OperationId);
    }

    [Fact]
    public void Parse_UnknownSchema_ReturnsContractIncompatible()
    {
        var result = PowerModeEngineContract.Parse(
            TestPaths.ReadFixture("engine-unknown-version.json"));

        Assert.Equal(
            BackendOperationOutcome.ContractIncompatible,
            result.Outcome);
        Assert.Contains("2", result.ContractError);
        Assert.Empty(result.Steps);
        Assert.Null(result.AfterState);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("prefix {\"schemaVersion\":1}")]
    public void Parse_MalformedOrMixedOutput_ReturnsInvalidResponse(
        string text)
    {
        Assert.Equal(
            BackendOperationOutcome.InvalidResponse,
            PowerModeEngineContract.Parse(text).Outcome);
    }

    [Fact]
    public void Parse_PartialOptionalFailure_RemainsNonCritical()
    {
        var result = PowerModeEngineContract.Parse(
            TestPaths.ReadFixture("engine-partial-v1.json"));

        var step = Assert.Single(result.Steps);
        Assert.Equal(BackendOperationOutcome.Partial, result.Outcome);
        Assert.False(step.Critical);
        Assert.False(step.TimedOut);
        Assert.False(result.Expectations.Single().Critical);
    }

    [Fact]
    public void Parse_FailedCriticalStep_RemainsCritical()
    {
        var result = PowerModeEngineContract.Parse(
            TestPaths.ReadFixture("engine-failed-v1.json"));

        var step = Assert.Single(result.Steps);
        Assert.Equal(BackendOperationOutcome.Failed, result.Outcome);
        Assert.True(step.Critical);
        Assert.True(result.Expectations.Single().Critical);
        Assert.Equal(
            PowerModeStateField.CpuMaximumAcPercent,
            result.Expectations.Single().Field);
    }

    [Fact]
    public void Parse_NumericEnumToken_ReturnsInvalidResponse()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "operationId": "11111111-1111-1111-1111-111111111111",
              "action": "status",
              "startedAtUtc": "2026-08-09T00:00:00Z",
              "durationMs": 1,
              "outcome": 0,
              "steps": [],
              "beforeState": null,
              "afterState": null,
              "expectations": []
            }
            """;

        Assert.Equal(
            BackendOperationOutcome.InvalidResponse,
            PowerModeEngineContract.Parse(json).Outcome);
    }

    [Fact]
    public void TargetFactory_RequiresExactlyOneTargetKind()
    {
        var profile = new CustomPowerProfile
        {
            Name = "Quiet",
            CpuMax = 45,
            CpuMin = 5,
            Brightness = 65,
            DisplayOffSeconds = 300,
            UseSeparateBatteryValues = true,
            BatteryCpuMax = 35,
            BatteryBrightness = 45,
            BatteryDisplayOffSeconds = 120
        };

        var target = PowerModeTarget.ForCustom(
            CustomPowerProfileSnapshot.FromSettings(profile));

        Assert.Null(target.Preset);
        Assert.Equal("custom:Quiet", target.Key);
        Assert.Equal(35, target.CustomProfile!.CpuMaximumDcPercent);
        Assert.Throws<ArgumentException>(() =>
            PowerModeTarget.ForCustom(
                new CustomPowerProfileSnapshot(
                    " ", 50, 50, 5, 50, 50, 60, 60, true)));
    }
}
