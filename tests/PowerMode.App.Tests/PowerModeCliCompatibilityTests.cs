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
        Assert.Contains("Open the interactive menu", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PowerModeSwitcher.bat")]
    [InlineData("PowerMode.Engine.ps1")]
    public async Task LaunchPortable_RebuildsWhenCliInputIsNewerUnderQuotedRoot(
        string newerCliInput)
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "PowerMode's workspace");
        var scripts = Path.Combine(root, "scripts");
        var cli = Path.Combine(root, "src", "PowerMode.Cli");
        var appSource = Path.Combine(root, "src", "PowerMode.App");
        var output = Path.Combine(root, "dist", "PowerMode-win-x64");
        var app = Path.Combine(output, "App");
        var marker = Path.Combine(root, "publish-ran.marker");

        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(cli);
        Directory.CreateDirectory(appSource);
        Directory.CreateDirectory(app);
        File.Copy(TestPaths.Repo("scripts", "Launch-PowerMode.bat"),
            Path.Combine(scripts, "Launch-PowerMode.bat"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"),
            Path.Combine(app, "PowerMode.exe"));

        var sourceProject = Path.Combine(appSource, "PowerMode.App.csproj");
        var bat = Path.Combine(cli, "PowerModeSwitcher.bat");
        var engine = Path.Combine(cli, "PowerMode.Engine.ps1");
        var readme = Path.Combine(root, "README.md");
        var stamp = Path.Combine(output, "build-info.json");
        File.WriteAllText(sourceProject, "<Project />");
        File.WriteAllText(bat, "@echo off");
        File.WriteAllText(engine, "# test engine");
        File.WriteAllText(readme, "test");
        File.WriteAllText(stamp, "{}");
        File.WriteAllText(Path.Combine(scripts, "Publish-Portable.ps1"),
            "[System.IO.File]::WriteAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'publish-ran.marker'), 'ran')");

        var stampTime = DateTime.UtcNow.AddMinutes(-2);
        foreach (var input in new[] { sourceProject, bat, engine, readme })
            File.SetLastWriteTimeUtc(input, stampTime.AddMinutes(-1));
        File.SetLastWriteTimeUtc(Path.Combine(cli, newerCliInput), stampTime.AddMinutes(1));
        File.SetLastWriteTimeUtc(stamp, stampTime);

        var result = await new ProcessRunner().RunAsync(new(
            "cmd.exe",
            ["/d", "/c", Path.Combine(scripts, "Launch-PowerMode.bat")],
            TimeSpan.FromSeconds(15)));

        Assert.True(result.Succeeded,
            $"Launch failed: {result.StandardError}{Environment.NewLine}{result.StandardOutput}");
        Assert.True(File.Exists(marker),
            $"A newer {newerCliInput} must trigger Publish-Portable.ps1. " +
            $"Standard error: {result.StandardError}");
    }

    [Fact]
    public async Task RootLauncher_Help_ForwardsToEngine()
    {
        var result = await RunRootBatAsync("help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("remote", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Open the interactive menu", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootLauncher_ExitsWhenCliScriptIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var launcherPath = Path.Combine(directory.Path, "PowerModeSwitcher.bat");
        File.WriteAllText(
            launcherPath,
            File.ReadAllText(TestPaths.Repo("PowerModeSwitcher.bat")));

        var result = await new ProcessRunner().RunAsync(new(
            "cmd.exe",
            ["/d", "/c", launcherPath],
            TimeSpan.FromSeconds(15)));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("PowerMode CLI was not found:", result.StandardError);
        Assert.DoesNotContain("PowerMode.Engine.ps1", result.StandardError);
    }

    [Fact]
    public async Task Engine_TextStatus_FormatsTimeoutsAndGroupsSections()
    {
        using var state = FakePowerState.CreateBalanced();
        await File.WriteAllTextAsync(
            state.Path,
            """
            {
              "activeSchemeId": "381b4222-f694-41f0-9685-ff5bb260df2e",
              "wifiDisabled": false,
              "values": {
                "381b4222-f694-41f0-9685-ff5bb260df2e|sub_video|videoidle|ac": 90,
                "381b4222-f694-41f0-9685-ff5bb260df2e|sub_video|videoidle|dc": 0,
                "381b4222-f694-41f0-9685-ff5bb260df2e|sub_sleep|standbyidle|ac": 3661,
                "381b4222-f694-41f0-9685-ff5bb260df2e|sub_sleep|standbyidle|dc": 7200
              }
            }
            """);

        var result = await RunEngineAsync(state, "-Mode", "status");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("\"schemaVersion\"", result.StandardOutput);
        Assert.Contains("Power and hardware", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Power policy (AC / battery)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("AC 1m 30s / Battery Never", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("AC 1h 1m 1s / Battery 2h", result.StandardOutput, StringComparison.Ordinal);
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
        params string[] arguments) =>
        RunLauncherAsync(
            TestPaths.Repo("src", "PowerMode.Cli", "PowerModeSwitcher.bat"),
            arguments);

    private static Task<ProcessExecutionResult> RunRootBatAsync(
        params string[] arguments) =>
        RunLauncherAsync(TestPaths.Repo("PowerModeSwitcher.bat"), arguments);

    private static Task<ProcessExecutionResult> RunLauncherAsync(
        string batPath,
        params string[] arguments)
    {
        return new ProcessRunner().RunAsync(new(
            "cmd.exe",
            ["/d", "/c", batPath, .. arguments],
            TimeSpan.FromSeconds(15),
            TestPaths.RepositoryRoot,
            new Dictionary<string, string?>
            {
                ["PM_NO_PAUSE"] = "1",
                ["POWERMODE_LANGUAGE"] = "en"
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
                ["POWERMODE_TEST_FAIL_SETTING"] = state.FailSetting,
                ["POWERMODE_LANGUAGE"] = "en"
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
