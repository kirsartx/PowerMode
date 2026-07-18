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
    public void MainWindow_ConfigurationRecoveryAppliesBeforeRecordingOperation()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.AdvancedFeatures.cs"));

        var restore = source.IndexOf("RestoreConfigurationBackupAsync(", StringComparison.Ordinal);
        var restoreApply = source.IndexOf(
            "ApplyFeatureSettings(SettingsStore.Load())",
            restore,
            StringComparison.Ordinal);
        var restoreRecord = source.IndexOf(
            "RecordConfigurationRestoreAsync(result)",
            restore,
            StringComparison.Ordinal);
        Assert.True(restore >= 0 && restoreApply > restore && restoreRecord > restoreApply);

        var reset = source.IndexOf("ResetSettingsDefaultsAsync()", StringComparison.Ordinal);
        var resetApply = source.IndexOf(
            "ApplyFeatureSettings(SettingsStore.Load())",
            reset,
            StringComparison.Ordinal);
        var resetRecord = source.IndexOf(
            "RecordConfigurationResetAsync()",
            reset,
            StringComparison.Ordinal);
        Assert.True(reset >= 0 && resetApply > reset && resetRecord > resetApply);
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
