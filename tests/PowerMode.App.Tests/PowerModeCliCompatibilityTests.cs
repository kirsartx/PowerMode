using System.Text.Json;
using System.Text;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class PowerModeCliCompatibilityTests
{
    private static class KnownPowerSettings
    {
        public const string CpuSubgroup =
            "54533251-82be-4824-96c1-47b60b740d00";
        public const string VideoSubgroup =
            "7516b95f-f776-4464-8c53-06167f40cc99";
        public const string CpuMaximum =
            "bc5038f7-23e0-4960-96da-33abaf5935ec";
        public const string CpuMinimum =
            "893dee8e-2bef-41e0-89c6-b55d0929964c";
        public const string ProcessorBoost =
            "be337238-0d82-4146-a960-4f3749d470c7";
        public const string Brightness =
            "aded5e82-b909-4619-9949-f5d71dac0bcb";
        public const string DisplayTimeout =
            "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
    }

    private const string SaverScheme =
        "a1841308-3541-4fab-bc81-f71556f20b4a";

    [Fact]
    public void BatLauncher_IsThinAndUsesSiblingEngine()
    {
        var bat = File.ReadAllText(
            TestPaths.Repo("src", "PowerMode.Cli", "PowerModeSwitcher.bat"));

        Assert.DoesNotContain("POWERSHELL_PAYLOAD_BELOW", bat);
        Assert.DoesNotContain("%TEMP%", bat, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PowerMode.Engine.ps1", bat);
        Assert.Contains("%*", bat);
        Assert.Contains("PM_NO_PAUSE", bat);
    }

    [Fact]
    public void Engine_IsUtf8WithBomAndKeepsChineseHelpText()
    {
        var enginePath = TestPaths.Repo(
            "src", "PowerMode.Cli", "PowerMode.Engine.ps1");
        var bytes = File.ReadAllBytes(enginePath);

        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);

        var content = Encoding.UTF8.GetString(bytes);
        Assert.Contains("模式说明", content);
        Assert.DoesNotContain("�", content);
    }

    [Fact]
    public async Task HumanCli_Help_PreservesDocumentedCommands()
    {
        var result = await RunBatAsync("help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("remote", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("balanced", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("high", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HumanCli_Status_PreservesNonEmptyTextOutput()
    {
        var result = await RunBatAsync("status");

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StandardOutput.Trim());
    }

    [Fact]
    public async Task Engine_JsonStatus_WritesExactlyOneDocument()
    {
        using var state = FakePowerState.CreateBalanced();
        var result = await RunEngineAsync(
            state,
            "-Mode", "status",
            "-OutputFormat", "Json",
            "-OperationId", "22222222-2222-2222-2222-222222222222");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("status", document.RootElement.GetProperty("action").GetString());
        Assert.Equal(
            string.Empty,
            result.StandardOutput[
                (result.StandardOutput.LastIndexOf('}') + 1)..].Trim());
    }

    [Fact]
    public async Task Engine_JsonApply_ContainsBeforeAfterStepsAndExpectations()
    {
        using var state = FakePowerState.CreateBalanced();
        var result = await RunEngineAsync(
            state,
            "-Mode", "remote",
            "-OutputFormat", "Json");

        var contract = PowerModeEngineContract.Parse(result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(BackendOperationOutcome.Succeeded, contract.Outcome);
        Assert.NotNull(contract.BeforeState);
        Assert.NotNull(contract.AfterState);
        Assert.NotEmpty(contract.Steps);
        Assert.Contains(
            contract.Expectations,
            expectation =>
                expectation.Field == PowerModeStateField.CpuMaximumAcPercent &&
                expectation.Critical);
    }

    [Fact]
    public async Task Engine_OptionalBrightnessFailure_ProducesPartial()
    {
        using var state = FakePowerState.CreateBalanced(
            failSetting: KnownPowerSettings.Brightness);
        var result = await RunEngineAsync(
            state,
            "-Mode", "remote",
            "-OutputFormat", "Json");

        var contract = PowerModeEngineContract.Parse(result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(BackendOperationOutcome.Partial, contract.Outcome);
        Assert.Contains(
            contract.Steps,
            step => !step.Critical && step.ExitCode != 0);
    }

    [Fact]
    public async Task Engine_CriticalCpuFailure_ProducesFailed()
    {
        using var state = FakePowerState.CreateBalanced(
            failSetting: KnownPowerSettings.CpuMaximum);
        var result = await RunEngineAsync(
            state,
            "-Mode", "remote",
            "-OutputFormat", "Json");

        var contract = PowerModeEngineContract.Parse(result.StandardOutput);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(BackendOperationOutcome.Failed, contract.Outcome);
        Assert.Contains(
            contract.Steps,
            step => step.Critical && step.ExitCode != 0);
    }

    [Fact]
    public async Task Engine_JsonCustomProfile_AppliesTypedAcDcValuesExactly()
    {
        using var state = FakePowerState.CreateBalanced();
        var profile = Encode("""
            {
              "name": "Quiet",
              "cpuMaximumAcPercent": 45,
              "cpuMaximumDcPercent": 35,
              "cpuMinimumPercent": 5,
              "brightnessAcPercent": 65,
              "brightnessDcPercent": 45,
              "displayTimeoutAcSeconds": 300,
              "displayTimeoutDcSeconds": 120,
              "disableBoost": true
            }
            """);
        var result = await RunEngineAsync(
            state,
            "-Mode", "custom",
            "-OutputFormat", "Json",
            "-CustomProfileBase64", profile);

        var contract = PowerModeEngineContract.Parse(result.StandardOutput);
        using var saved = JsonDocument.Parse(File.ReadAllText(state.Path));
        Assert.Equal(BackendOperationOutcome.Succeeded, contract.Outcome);
        Assert.Equal("custom:Quiet", contract.RequestedTargetKey);
        Assert.Equal(45, SavedValue(saved, SaverScheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.CpuMaximum, "ac"));
        Assert.Equal(35, SavedValue(saved, SaverScheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.CpuMaximum, "dc"));
        Assert.Equal(5, SavedValue(saved, SaverScheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.CpuMinimum, "ac"));
        Assert.Equal(5, SavedValue(saved, SaverScheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.CpuMinimum, "dc"));
        Assert.Equal(0, SavedValue(saved, SaverScheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.ProcessorBoost, "ac"));
        Assert.Equal(0, SavedValue(saved, SaverScheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.ProcessorBoost, "dc"));
        Assert.Equal(65, SavedValue(saved, SaverScheme, KnownPowerSettings.VideoSubgroup, KnownPowerSettings.Brightness, "ac"));
        Assert.Equal(45, SavedValue(saved, SaverScheme, KnownPowerSettings.VideoSubgroup, KnownPowerSettings.Brightness, "dc"));
        Assert.Equal(300, SavedValue(saved, SaverScheme, KnownPowerSettings.VideoSubgroup, KnownPowerSettings.DisplayTimeout, "ac"));
        Assert.Equal(120, SavedValue(saved, SaverScheme, KnownPowerSettings.VideoSubgroup, KnownPowerSettings.DisplayTimeout, "dc"));
        Assert.DoesNotContain(
            contract.Expectations,
            expectation => expectation.Field is
                PowerModeStateField.SleepTimeoutAcSeconds or
                PowerModeStateField.SleepTimeoutDcSeconds or
                PowerModeStateField.HibernateTimeoutAcSeconds or
                PowerModeStateField.HibernateTimeoutDcSeconds);
        Assert.Contains(
            contract.Expectations,
            expectation => expectation.Field == PowerModeStateField.DisplayTimeoutDcSeconds &&
                expectation.ExpectedValue == "120");
        Assert.Contains(
            contract.Expectations,
            expectation => expectation.Field == PowerModeStateField.ProcessorBoostModeAc &&
                expectation.ExpectedValue == "0");
    }

    [Fact]
    public async Task Engine_RestoreSnapshot_RestoresActiveSchemeAndMutableFields()
    {
        using var state = FakePowerState.CreateBalanced();
        var snapshot = Encode("""
            {
              "activeSchemeId": "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
              "cpuMaximumAcPercent": 77,
              "cpuMaximumDcPercent": 76,
              "cpuMinimumAcPercent": 7,
              "cpuMinimumDcPercent": 6,
              "processorBoostModeAc": 2,
              "processorBoostModeDc": 1,
              "brightnessAcPercent": 61,
              "brightnessDcPercent": 60,
              "displayTimeoutAcSeconds": 321,
              "displayTimeoutDcSeconds": 322,
              "sleepTimeoutAcSeconds": 11,
              "sleepTimeoutDcSeconds": 12,
              "hibernateTimeoutAcSeconds": 13,
              "hibernateTimeoutDcSeconds": 14,
              "wifiDisabled": true
            }
            """);
        var result = await RunEngineAsync(
            state,
            "-Mode", "restore",
            "-OutputFormat", "Json",
            "-RestoreSnapshotBase64", snapshot);

        var contract = PowerModeEngineContract.Parse(result.StandardOutput);
        var saved = JsonDocument.Parse(File.ReadAllText(state.Path));
        Assert.Equal(
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            saved.RootElement.GetProperty("activeSchemeId").GetString());
        const string scheme = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        Assert.Equal(77, SavedValue(saved, scheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.CpuMaximum, "ac"));
        Assert.Equal(76, SavedValue(saved, scheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.CpuMaximum, "dc"));
        Assert.Equal(7, SavedValue(saved, scheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.CpuMinimum, "ac"));
        Assert.Equal(6, SavedValue(saved, scheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.CpuMinimum, "dc"));
        Assert.Equal(2, SavedValue(saved, scheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.ProcessorBoost, "ac"));
        Assert.Equal(1, SavedValue(saved, scheme, KnownPowerSettings.CpuSubgroup, KnownPowerSettings.ProcessorBoost, "dc"));
        Assert.Equal(61, SavedValue(saved, scheme, KnownPowerSettings.VideoSubgroup, KnownPowerSettings.Brightness, "ac"));
        Assert.Equal(60, SavedValue(saved, scheme, KnownPowerSettings.VideoSubgroup, KnownPowerSettings.Brightness, "dc"));
        Assert.Equal(321, SavedValue(saved, scheme, KnownPowerSettings.VideoSubgroup, KnownPowerSettings.DisplayTimeout, "ac"));
        Assert.Equal(322, SavedValue(saved, scheme, KnownPowerSettings.VideoSubgroup, KnownPowerSettings.DisplayTimeout, "dc"));
        Assert.True(saved.RootElement.GetProperty("wifiDisabled").GetBoolean());
        foreach (var field in new[]
                 {
                     PowerModeStateField.CpuMinimumAcPercent,
                     PowerModeStateField.CpuMinimumDcPercent,
                     PowerModeStateField.ProcessorBoostModeAc,
                     PowerModeStateField.ProcessorBoostModeDc,
                     PowerModeStateField.WifiDisabled
                 })
        {
            Assert.Contains(contract.Expectations, expectation => expectation.Field == field);
        }
        Assert.True(
            contract.Outcome == BackendOperationOutcome.Succeeded,
            result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Engine_TextMode_DoesNotEmitJson()
    {
        using var state = FakePowerState.CreateBalanced();
        var result = await RunEngineAsync(state, "-Mode", "status");

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StandardOutput.Trim());
        Assert.DoesNotContain("\"schemaVersion\"", result.StandardOutput);
    }

    private static Task<ProcessExecutionResult> RunBatAsync(
        params string[] arguments)
    {
        var batPath = TestPaths.Repo(
            "src", "PowerMode.Cli", "PowerModeSwitcher.bat");
        return new ProcessRunner().RunAsync(new(
            "cmd.exe",
            ["/d", "/c", batPath, .. arguments],
            TimeSpan.FromSeconds(15),
            TestPaths.RepositoryRoot,
            new Dictionary<string, string?>
            {
                ["PM_NO_PAUSE"] = "1"
            }));
    }

    private static Task<ProcessExecutionResult> RunEngineAsync(
        FakePowerState state,
        params string[] arguments)
    {
        var request = new ProcessExecutionRequest(
            "powershell.exe",
            [
                "-NoLogo",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                TestPaths.Repo("src", "PowerMode.Cli", "PowerMode.Engine.ps1"),
                .. arguments
            ],
            TimeSpan.FromSeconds(15),
            Environment: new Dictionary<string, string?>
            {
                ["POWERMODE_POWERCFG_PATH"] = TestPaths.TestHostExe,
                ["POWERMODE_TEST_POWER_STATE"] = state.Path,
                ["POWERMODE_TEST_FAIL_SETTING"] = state.FailSetting
            });
        return new ProcessRunner().RunAsync(request);
    }

    private static string Encode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    private static int SavedValue(
        JsonDocument state,
        string scheme,
        string subgroup,
        string setting,
        string powerSource) =>
        state.RootElement
            .GetProperty("values")
            .GetProperty($"{scheme}|{subgroup}|{setting}|{powerSource}")
            .GetInt32();

    private sealed class FakePowerState : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public string Path { get; }
        public string? FailSetting { get; }

        private FakePowerState(string? failSetting)
        {
            Path = System.IO.Path.Combine(_directory.Path, "state.json");
            FailSetting = failSetting;
            File.WriteAllText(
                Path,
                """
                {
                  "activeSchemeId": "381b4222-f694-41f0-9685-ff5bb260df2e",
                  "wifiDisabled": false,
                  "values": {}
                }
                """);
        }

        public static FakePowerState CreateBalanced(
            string? failSetting = null) =>
            new(failSetting);

        public void Dispose() => _directory.Dispose();
    }
}
