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
        foreach (var buttonName in new[] { "UndoButton", "RestoreButton", "ResetButton" })
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
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.AdvancedFeatures.cs"));

        Assert.Contains("RecordHistory: false", source);
        Assert.Contains("createSafetyBackup: true", source);
        Assert.Contains("ProductionRecoveryBackend", source);
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
        Assert.Contains("_latestUndo = null;", source);
        Assert.Contains("_latestBackup = null;", source);
        Assert.Contains("var latestUndo", source);
        Assert.Contains("var backupAvailability", source);
        Assert.Contains("TryUpdatePresentation", source);
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
