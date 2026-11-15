using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class CustomProfileSleepDisplayTests
{
    [Fact]
    public void ApplyCustomProfile_SuccessPath_KeepsVerifiedSleepReading()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.Features.cs"));
        var method = source[
            source.IndexOf("internal async Task ApplyCustomProfileAsync", StringComparison.Ordinal)..
            source.IndexOf("internal async Task VerifyWithSummaryAsync", StringComparison.Ordinal)];

        Assert.Contains("RunTargetCoreAsync", method);
        Assert.DoesNotContain("SleepValue.Text", method);
        Assert.DoesNotContain("CustomProfileSleepDisplay", source);
    }

    [Fact]
    public void TrayMenu_UsesLocalizedLabels()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.Features.cs"));
        var method = source[
            source.IndexOf("private void ShowTrayMenu()", StringComparison.Ordinal)..
            source.IndexOf("internal async Task ApplyCustomProfileAsync", StringComparison.Ordinal)];

        Assert.Contains("T(\"Remote\")", method);
        Assert.Contains("T(\"Saver\")", method);
        Assert.Contains("T(\"Balanced\")", method);
        Assert.Contains("T(\"High\")", method);
        Assert.Contains("T(\"TrayShow\")", method);
        Assert.Contains("T(\"Exit\")", method);
        Assert.DoesNotContain("\"Show PowerMode\"", method);
        Assert.DoesNotContain("\"High performance\"", method);
    }

    [Fact]
    public void Engine_SaverFamilyDetection_UsesCpuMinimum()
    {
        var engine = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.Cli", "PowerMode.Engine.ps1"));

        Assert.Contains("function Resolve-SaverFamilyMode", engine);
        Assert.Contains("if ($CpuMinimum -eq 3) { return 'saver' }", engine);
        Assert.Contains("Test-CpuMaximumInRange", engine);
        Assert.DoesNotContain(
            "$expectedCpu = Get-PowerSettingAcValue $guid SUB_PROCESSOR PROCTHROTTLEMAX",
            engine);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PowerMode.slnx")))
                return Path.Combine([directory.FullName, .. path]);
        }

        throw new DirectoryNotFoundException("Could not locate the PowerMode repository root.");
    }
}
