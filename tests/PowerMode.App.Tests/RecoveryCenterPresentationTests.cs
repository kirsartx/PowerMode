using System.Xml.Linq;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class RecoveryCenterPresentationTests
{
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void RecoveryCenter_UsesMicaThreePanelCardsAndPersistentInfoBar()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "RecoveryCenterWindow.xaml"));

        Assert.Single(document.Descendants(), element => element.Name.LocalName == "MicaBackdrop");
        Assert.Equal(
            3,
            document.Descendants()
                .Count(element =>
                    element.Name.LocalName == "Border"
                    && (string?)element.Attribute("Style") == "{StaticResource PanelStyle}"));
        var infoBar = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "InfoBar");
        Assert.Equal("False", (string?)infoBar.Attribute("IsClosable"));
        foreach (var buttonName in new[]
                 {
                     "UndoButton",
                     "VerifyLastOperationButton",
                     "RestoreBeforeStateButton",
                     "RestoreButton",
                     "ResetButton"
                 })
        {
            var button = Assert.Single(
                document.Descendants(),
                element => (string?)element.Attribute(Xaml + "Name") == buttonName);
            Assert.Equal("False", (string?)button.Attribute("IsEnabled"));
        }
    }

    [Fact]
    public void MainWindow_RecoveryEntryIsOutsideProfessionalOnlyContainer()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml"));
        var button = Assert.Single(
            document.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == "RecoveryCenterButton");

        Assert.DoesNotContain(
            button.Ancestors(),
            ancestor =>
                (string?)ancestor.Attribute(Xaml + "Name") == "ProfessionalQuickActions");
        Assert.Equal("OpenRecoveryCenterButton_Click", (string?)button.Attribute("Click"));
    }

    [Fact]
    public void MainWindow_RecoveryOrchestrationSuppressesRegularHistoryAndUsesSafetyBackup()
    {
        var advanced = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.AdvancedFeatures.cs"));
        var main = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml.cs"));
        var source = advanced + main;

        Assert.Contains("GetLastOperationAvailabilityAsync", source);
        Assert.Contains("VerifyLastOperationAsync", source);
        Assert.Contains("RestoreBeforeStateAsync", source);
        Assert.Contains("createSafetyBackup: true", source);
        Assert.Contains("ProductionRecoveryBackend", source);
        Assert.DoesNotContain("FindLatestUndoable", source);
        Assert.DoesNotContain("UndoLatestAsync", source);
    }

    [Fact]
    public void MainWindow_ConfigurationRecoveryUsesStrictReloadAndAuditedService()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.AdvancedFeatures.cs"));

        Assert.Contains("RestoreConfigurationAsync(", source);
        Assert.Contains("ResetDefaultsAsync(", source);
        Assert.True(
            CountOccurrences(source, "ApplyFeatureSettings(SettingsStore.LoadStrict())") >= 2,
            "Configuration recovery must strictly reload and apply both restored and reset settings.");
        Assert.DoesNotContain("RecordConfigurationRestoreAsync", source);
        Assert.DoesNotContain("RecordConfigurationResetAsync", source);
    }

    [Fact]
    public void RecoveryCenter_AcquiresBusyBeforeConfirmationAndCatchesDialogFailures()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "RecoveryCenterWindow.xaml.cs"));

        Assert.Contains("TryBeginOperation", source);
        Assert.DoesNotContain("!await ConfirmAsync", source);
        Assert.Contains("catch (Exception ex)", source);
        Assert.Contains("RefreshAvailabilityAsync(showProgress: false)", source);
        Assert.Contains("TryUpdatePresentation(() => SetBusy(false))", source);
        foreach (var handler in new[]
                 {
                     "UndoButton_Click",
                     "VerifyLastOperationButton_Click",
                     "RestoreBeforeStateButton_Click",
                     "RestoreButton_Click",
                     "ResetButton_Click"
                 })
        {
            var start = source.IndexOf(handler, StringComparison.Ordinal);
            var end = source.IndexOf("\n    private ", start + handler.Length, StringComparison.Ordinal);
            var body = source[start..(end < 0 ? source.Length : end)];
            Assert.True(
                body.IndexOf("TryBeginOperation", StringComparison.Ordinal)
                < body.IndexOf("await ConfirmAsync", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RecoveryCenter_CloseCancelsLifetimeAndAvailabilityUsesClearedLocals()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "RecoveryCenterWindow.xaml.cs"));

        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource", source);
        Assert.Contains("RecoveryCenterWindow_Closed", source);
        Assert.Contains("_lastOperationAvailability = null;", source);
        Assert.Contains("_latestBackup = null;", source);
        Assert.Contains("var lastOperationAvailability", source);
        Assert.Contains("var backupAvailability", source);
        Assert.Contains("TryUpdatePresentation", source);
    }

    [Fact]
    public void RecoveryCenter_PresentsJournalSourceReasonTargetAndExactActions()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "RecoveryCenterWindow.xaml.cs"));

        Assert.Contains("record.Source", source);
        Assert.Contains("record.Reason", source);
        Assert.Contains("record.Target.Key", source);
        Assert.Contains("availability?.CanUndo", source);
        Assert.Contains("availability?.RequiresVerification", source);
        Assert.Contains("_owner.VerifyLastOperationAsync(operationId", source);
        Assert.Contains("_owner.RestoreBeforeStateAsync(operationId", source);
    }

    [Fact]
    public void MainWindow_UsesOneTypedRuntimeAndNoLegacyPowerMutationPaths()
    {
        var main = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml.cs"));
        var advanced = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.AdvancedFeatures.cs"));
        var features = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.Features.cs"));
        var combined = main + advanced + features;

        Assert.Equal(1, CountOccurrences(combined, "new ProcessRunner()"));
        Assert.Equal(1, CountOccurrences(combined, "new PowerModeBackend("));
        Assert.Equal(1, CountOccurrences(combined, "new LastOperationStore("));
        Assert.Equal(1, CountOccurrences(combined, "new ModeSwitchCoordinator("));
        Assert.Equal(1, CountOccurrences(combined, "new StartupCoordinator("));
        Assert.Contains("ReadStateAsync", combined);
        Assert.Contains("TrySwitchAsync", combined);
        Assert.Contains("PowerModeTarget.ForCustom(", combined);
        Assert.Contains("CustomPowerProfileSnapshot.FromSettings(profile)", combined);
        Assert.DoesNotContain("PrepareCachedScript", combined);
        Assert.DoesNotContain("_scriptPath", combined);
        Assert.DoesNotContain("GetActivePlanGuidFast", combined);
        Assert.DoesNotContain("ActivatePlanImmediatelyAsync", combined);
        Assert.DoesNotContain("ApplyOptimisticMode", combined);
        Assert.DoesNotContain("RunPowerCfgAsync", combined);
        Assert.DoesNotContain("Process.Start(new ProcessStartInfo(\"powercfg.exe\"", combined);
        Assert.DoesNotContain("Regex.Matches(result.Output", combined);
        Assert.DoesNotContain("ApplyStatus(result.Output)", combined);
    }

    [Fact]
    public void MainWindow_RendersOutcomeActionAndProfessionalDiagnostics()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml"));

        foreach (var name in new[]
                 {
                     "StatusActionButton",
                     "ModeSwitchDiagnosticText",
                     "CopyModeSwitchDiagnosticButton"
                 })
        {
            Assert.Single(
                document.Descendants(),
                element => (string?)element.Attribute(Xaml + "Name") == name);
        }
    }

    [Fact]
    public void MainWindow_CloseCancelsRecoveryAndDefersIntegrationDisposalUntilIdle()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.AdvancedFeatures.cs"));

        var cancel = source.IndexOf(
            "_recoveryLifetimeCancellation.Cancel()",
            StringComparison.Ordinal);
        var wait = source.IndexOf("WaitForIdleAsync", cancel, StringComparison.Ordinal);
        var dispose = source.IndexOf("_systemIntegration.Dispose()", wait, StringComparison.Ordinal);
        Assert.True(cancel >= 0 && wait > cancel && dispose > wait);
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

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }
}
