using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class FluentAccessibilityPresentationTests
{
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Automation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void MainWindow_PreservesAcceleratorsLiveRegionsAndRecommendationWrap()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml"));
        var xaml = document.ToString();

        Assert.Contains("AutomationProperties.AcceleratorKey=\"1\"", xaml);
        Assert.Contains("AutomationProperties.AcceleratorKey=\"2\"", xaml);
        Assert.Contains("AutomationProperties.AcceleratorKey=\"3\"", xaml);
        Assert.Contains("AutomationProperties.AcceleratorKey=\"4\"", xaml);
        Assert.Contains("AutomationProperties.AcceleratorKey=\"F5\"", xaml);

        var recommendationReason = Assert.Single(
            document.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == "RecommendationReason");
        Assert.Equal("Wrap", (string?)recommendationReason.Attribute("TextWrapping"));
        Assert.Equal(
            "Polite",
            (string?)recommendationReason.Attribute(Automation + "AutomationProperties.LiveSetting")
            ?? (string?)recommendationReason.Attribute("AutomationProperties.LiveSetting"));

        Assert.DoesNotMatch(new Regex("""#[0-9A-Fa-f]{3,8}"""), xaml);
        Assert.Contains("AccentFillColorDefaultBrush", xaml);
        Assert.Contains("AccentTextFillColorPrimaryBrush", xaml);
        Assert.Contains("TextFillColorSecondaryBrush", xaml);
        Assert.Contains("SystemFillColorCriticalBrush", xaml);
    }

    [Fact]
    public void MainWindow_SetsLocalizedAutomationNamesHelpAndCurrentModeStatus()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml.cs"));
        var features = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.Features.cs"));

        Assert.Contains("AutomationProperties.SetName(ExperienceModeButton", source);
        Assert.Contains("T(\"ExperienceModeAutomation\")", source);
        Assert.Contains("AutomationProperties.SetItemStatus(button", source);
        Assert.Contains("当前模式", source);
        Assert.Contains("Current mode", source);
        Assert.Contains("AccentFillColorDefaultBrush", source);
        Assert.Contains("TextOnAccentFillColorPrimaryBrush", source);
        Assert.Contains("AutomationProperties.SetHelpText(element,presentation.Reason)", features);
        Assert.Contains("_hardwareCapabilities,IsChinese)", features);
        Assert.Contains("CapabilityVisibilityPolicy.Evaluate(", features);
    }

    [Fact]
    public void MainWindow_SimpleModeHidesLogColumnAndCompactHeaderCollapsesLabels()
    {
        var features = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.Features.cs"));
        var main = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml.cs"));

        Assert.Contains("ProfessionalLogPanel.Visibility=professional?Visibility.Visible:Visibility.Collapsed", features);
        Assert.Contains("MainContentGrid.ColumnDefinitions[1].Width=", features);
        Assert.Contains("professional?new GridLength(1,GridUnitType.Star):new GridLength(0)", features);
        Assert.Contains("var compact=e.NewSize.Width<1040", main);
        Assert.Contains("RecoveryCenterButtonText.Visibility=visibility", main);
        Assert.Contains("RefreshButtonText.Visibility=visibility", main);
        Assert.Contains("FeaturesButtonText.Visibility=visibility", main);
        Assert.Contains("InsightsButtonText.Visibility=visibility", main);
        Assert.Contains("ToolTipService.SetToolTip(RefreshButton", main);
        Assert.Contains("ToolTipService.SetToolTip(RecoveryCenterButton", main);
    }

    [Fact]
    public void RecoveryCenter_ExposesAccessibleNamesLiveRegionAndThemeBrushes()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "RecoveryCenterWindow.xaml"));
        var xaml = document.ToString();
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "RecoveryCenterWindow.xaml.cs"));

        Assert.DoesNotMatch(new Regex("""#[0-9A-Fa-f]{3,8}"""), xaml);
        Assert.Contains("AccentTextFillColorPrimaryBrush", xaml);
        Assert.Contains("SystemFillColorCriticalBrush", xaml);
        Assert.Contains("TextFillColorSecondaryBrush", xaml);

        var resultMessage = Assert.Single(
            document.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == "ResultMessageText");
        Assert.Equal("Wrap", (string?)resultMessage.Attribute("TextWrapping"));
        Assert.Equal(
            "Polite",
            (string?)resultMessage.Attribute(Automation + "AutomationProperties.LiveSetting")
            ?? (string?)resultMessage.Attribute("AutomationProperties.LiveSetting"));

        foreach (var buttonName in new[] { "UndoButton", "RestoreButton", "ResetButton" })
        {
            var button = Assert.Single(
                document.Descendants(),
                element => (string?)element.Attribute(Xaml + "Name") == buttonName);
            Assert.Equal("118", (string?)button.Attribute("MinWidth"));
            Assert.DoesNotContain(
                button.Attributes(),
                attribute => attribute.Name.LocalName == "MaxWidth");
        }

        Assert.Contains("AutomationProperties.SetName", source);
        Assert.Contains("UndoButton", source);
        Assert.Contains("RestoreButton", source);
        Assert.Contains("ResetButton", source);
        Assert.Contains("撤销最近模式切换", source);
        Assert.Contains("Undo latest mode switch", source);
        Assert.Contains("恢复配置备份", source);
        Assert.Contains("Restore configuration backup", source);
        Assert.Contains("重置为默认设置", source);
        Assert.Contains("Reset to defaults", source);
    }

    [Fact]
    public void AppResources_UseThemeBrushesWithoutHardCodedColors()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "App.xaml"));

        Assert.DoesNotMatch(new Regex("""#[0-9A-Fa-f]{3,8}"""), xaml);
        Assert.Contains("CardBackgroundFillColorDefaultBrush", xaml);
        Assert.Contains("TextFillColorSecondaryBrush", xaml);
        Assert.Contains("TextFillColorPrimaryBrush", xaml);
        Assert.Contains("XamlControlsResources", xaml);
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
