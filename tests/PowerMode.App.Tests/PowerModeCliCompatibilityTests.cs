using System.Text;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class PowerModeCliCompatibilityTests
{
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
}
