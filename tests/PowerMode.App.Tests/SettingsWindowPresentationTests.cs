using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class SettingsWindowPresentationTests
{
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(true, "开", "关", "保存", "放弃", "继续编辑")]
    [InlineData(false, "On", "Off", "Save", "Discard", "Continue editing")]
    public void SharedStrings_LocalizeToggleAndCloseActions(
        bool chinese,
        string on,
        string off,
        string save,
        string discard,
        string continueEditing)
    {
        Assert.Equal(on, SettingsWindowStrings.On(chinese));
        Assert.Equal(off, SettingsWindowStrings.Off(chinese));
        Assert.Equal(save, SettingsWindowStrings.Save(chinese));
        Assert.Equal(discard, SettingsWindowStrings.Discard(chinese));
        Assert.Equal(continueEditing, SettingsWindowStrings.ContinueEditing(chinese));
    }

    [Theory]
    [InlineData(true, "方案 <&>", "删除自定义方案？", "将删除“方案 <&>”。")]
    [InlineData(false, "Rule <&>", "Delete automation rule?", "This will delete “Rule <&>”.")]
    public void DeleteStrings_NameTheSelectedItemAsPlainText(
        bool chinese,
        string name,
        string expectedTitle,
        string expectedMessage)
    {
        var title = chinese
            ? SettingsWindowStrings.DeleteProfileTitle(true)
            : SettingsWindowStrings.DeleteRuleTitle(false);

        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedMessage, SettingsWindowStrings.DeleteNamedItem(chinese, name));
    }

    [Theory]
    [InlineData(759, true, 1, 0)]
    [InlineData(760, false, 0, 1)]
    public void AuxiliaryProjection_StacksAt759AndSplitsAt760(
        double width,
        bool stack,
        int secondRow,
        int secondColumn)
    {
        var projection = ResponsiveLayoutPolicy.ProjectAuxiliaryContent(width);

        Assert.Equal(stack, projection.Stack);
        Assert.Equal(0, projection.FirstRow);
        Assert.Equal(0, projection.FirstColumn);
        Assert.Equal(secondRow, projection.SecondRow);
        Assert.Equal(secondColumn, projection.SecondColumn);
        Assert.Equal(stack ? GridLengthProjection.Fixed(0) : GridLengthProjection.Star,
            projection.Columns[1]);
    }

    [Fact]
    public void SettingsWindow_DeclaresNamedResponsiveEditorsSeparateSaveCloseAndAccessibleInputs()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "SettingsWindow.xaml"));

        _ = NamedElement(document, "ProfilesEditorGrid");
        _ = NamedElement(document, "RulesEditorGrid");
        _ = NamedElement(document, "SaveButton");
        _ = NamedElement(document, "CloseButton");
        _ = NamedElement(document, "EditRuleButton");

        foreach (var name in new[]
                 {
                     "CpuMaxSlider", "BrightnessSlider", "CpuMinBox", "DisplayOffBox",
                     "BatteryCpuBox", "BatteryBrightnessBox", "BatteryDisplayBox",
                     "TemperatureLimitBox", "TemperatureRecoveryBox", "LowBatteryBox",
                     "IntervalBox", "BackupCountBox"
                 })
        {
            var input = NamedElement(document, name);
            Assert.True(
                input.Attributes().Any(attribute =>
                    attribute.Name.LocalName is "AutomationProperties.LabeledBy" or
                        "AutomationProperties.Name"),
                $"{name} needs an accessible label.");
        }

        var xaml = document.ToString();
        Assert.DoesNotContain("OffContent=\"关闭\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnContent=\"开启\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWindow_UsesSingleSaveAndGuardedClosePaths()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "SettingsWindow.xaml.cs"));
        var mainSource = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.Features.cs"));

        Assert.Contains("SettingsStore.Clone", source);
        Assert.Contains("AppWindow.Closing += SettingsWindow_Closing", source);
        Assert.Contains("RequestCloseAsync", source);
        Assert.Contains("SaveAsync", source);
        Assert.DoesNotContain("PersistSettingsAsync", source);
        Assert.DoesNotContain("_settingsWindow?.Close()", mainSource);
        Assert.Contains("await settingsWindow.RequestCloseAsync()", mainSource);
        Assert.Contains("SettingsWindowStrings.On(_zh)", source);
        Assert.Contains("SettingsWindowStrings.Off(_zh)", source);
        Assert.Empty(Regex.Matches(source, @"SettingsStore\.Save\s*\(").Cast<Match>());
    }

    [Fact]
    public void SettingsAndInsights_ApplyAuxiliaryProjectionToNamedRegions()
    {
        var settings = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "SettingsWindow.xaml.cs"));
        var insights = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "InsightsWindow.xaml.cs"));

        Assert.Contains("ResponsiveLayoutPolicy.ProjectAuxiliaryContent(e.NewSize.Width)", settings);
        Assert.Contains("ProfilesEditorGrid", settings);
        Assert.Contains("RulesEditorGrid", settings);
        Assert.Contains("Grid.SetRow(second, projection.SecondRow)", settings);
        Assert.Contains("ResponsiveLayoutPolicy.ProjectAuxiliaryContent(e.NewSize.Width)", insights);
        Assert.Contains("Grid.SetRow(InsightsMetricsRegion, projection.FirstRow)", insights);
        Assert.Contains("Grid.SetRow(InsightsTrendRegion, projection.SecondRow)", insights);
    }

    private static XElement NamedElement(XDocument document, string name) =>
        Assert.Single(
            document.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == name);

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
