using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class FluentAccessibilityPresentationTests
{
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MainWindow_ExposesAcceleratorsLiveRegionWrapAndThemeResources()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml"));

        AssertNamedAccelerator(document, "RemoteButton", "1");
        AssertNamedAccelerator(document, "SaverButton", "2");
        AssertNamedAccelerator(document, "BalancedButton", "3");
        AssertNamedAccelerator(document, "HighButton", "4");
        AssertNamedAccelerator(document, "RefreshButton", "F5");

        var recommendationReason = NamedElement(document, "RecommendationReason");
        Assert.Equal("Wrap", AttributeValue(recommendationReason, "TextWrapping"));
        Assert.Equal("Polite", AutomationAttribute(recommendationReason, "LiveSetting"));

        var experienceToggle = NamedElement(document, "ExperienceModeButton");
        Assert.False(string.IsNullOrWhiteSpace(AutomationAttribute(experienceToggle, "Name")));

        Assert.DoesNotMatch(new Regex("""#[0-9A-Fa-f]{3,8}"""), document.ToString());
        Assert.Contains(
            document.Descendants(),
            element => UsesThemeResource(element, "AccentFillColorDefaultBrush"));
        Assert.Contains(
            document.Descendants(),
            element => UsesThemeResource(element, "AccentTextFillColorPrimaryBrush"));
        Assert.Contains(
            document.Descendants(),
            element => UsesThemeResource(element, "TextFillColorSecondaryBrush"));
        Assert.Contains(
            document.Descendants(),
            element => UsesThemeResource(element, "SystemFillColorCriticalBrush"));
    }

    [Fact]
    public void RecoveryCenter_ExposesLiveRegionWrapThemeResourcesAndRecoveryActions()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "RecoveryCenterWindow.xaml"));

        Assert.DoesNotMatch(new Regex("""#[0-9A-Fa-f]{3,8}"""), document.ToString());
        Assert.Contains(
            document.Descendants(),
            element => UsesThemeResource(element, "AccentTextFillColorPrimaryBrush"));
        Assert.Contains(
            document.Descendants(),
            element => UsesThemeResource(element, "SystemFillColorCriticalBrush"));
        Assert.Contains(
            document.Descendants(),
            element => UsesThemeResource(element, "TextFillColorSecondaryBrush"));

        var resultMessage = NamedElement(document, "ResultMessageText");
        Assert.Equal("Wrap", AttributeValue(resultMessage, "TextWrapping"));
        Assert.Equal("Polite", AutomationAttribute(resultMessage, "LiveSetting"));

        foreach (var buttonName in new[] { "UndoButton", "RestoreButton", "ResetButton" })
        {
            var button = NamedElement(document, buttonName);
            Assert.Equal("118", AttributeValue(button, "MinWidth"));
            Assert.Null(AttributeValue(button, "MaxWidth"));
        }
    }

    [Theory]
    [InlineData(true, "撤销最近模式切换", "恢复配置备份", "重置为默认设置", "恢复操作结果")]
    [InlineData(false, "Undo latest mode switch", "Restore configuration backup", "Reset to defaults", "Recovery operation result")]
    public void RecoveryAutomationLabels_AreLocalizedForActionsAndResult(
        bool isChinese,
        string undo,
        string restore,
        string reset,
        string result)
    {
        Assert.Equal(undo, RecoveryCenterAutomationLabels.Undo(isChinese));
        Assert.Equal(restore, RecoveryCenterAutomationLabels.Restore(isChinese));
        Assert.Equal(reset, RecoveryCenterAutomationLabels.Reset(isChinese));
        Assert.Equal(result, RecoveryCenterAutomationLabels.Result(isChinese));
    }

    [Fact]
    public void RecoveryCenterWindow_ConsumesRecoveryAutomationLabelsHelper()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "RecoveryCenterWindow.xaml.cs"));

        Assert.Contains("RecoveryCenterAutomationLabels.Undo(_isChinese)", source);
        Assert.Contains("RecoveryCenterAutomationLabels.Restore(_isChinese)", source);
        Assert.Contains("RecoveryCenterAutomationLabels.Reset(_isChinese)", source);
        Assert.Contains("RecoveryCenterAutomationLabels.Result(_isChinese)", source);
        Assert.Contains("AutomationProperties.SetName", source);
    }

    [Fact]
    public void CapabilityVisibilityPolicy_RequiresLanguageArgument()
    {
        var parameter = typeof(CapabilityVisibilityPolicy)
            .GetMethod(
                nameof(CapabilityVisibilityPolicy.Evaluate),
                BindingFlags.Public | BindingFlags.Static)!
            .GetParameters()[2];

        Assert.Equal("isChinese", parameter.Name);
        Assert.False(parameter.HasDefaultValue);
    }

    [Fact]
    public void CapabilityPresentationCallSites_PassLanguageExplicitly()
    {
        foreach (var pathParts in new[]
                 {
                     new[] { "src", "PowerMode.App", "Views", "MainWindow.Features.cs" },
                     new[] { "src", "PowerMode.App", "Views", "SettingsWindow.xaml.cs" }
                 })
        {
            var source = File.ReadAllText(FindRepositoryFile(pathParts));
            var matches = Regex.Matches(
                source,
                @"CapabilityVisibilityPolicy\.Evaluate\s*\((?<args>[\s\S]*?)\)");
            Assert.NotEmpty(matches);
            foreach (Match match in matches)
            {
                var args = match.Groups["args"].Value;
                var argCount = args.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Length;
                Assert.True(
                    argCount >= 3,
                    $"{string.Join('/', pathParts)} must pass isChinese explicitly: {match.Value}");
            }
        }
    }

    [Fact]
    public void CapabilityDisabledPresentation_UsesEnabledHelpAndTooltipWithoutExtraOpacity()
    {
        var disabled = new FeaturePresentation(
            IsVisible: true,
            IsEnabled: false,
            Reason: "No controllable Wi-Fi adapter was detected");
        var enabled = new FeaturePresentation(
            IsVisible: true,
            IsEnabled: true,
            Reason: string.Empty);

        var disabledState = CapabilityControlPresentation.Map(disabled);
        Assert.False(disabledState.IsEnabled);
        Assert.Equal(disabled.Reason, disabledState.HelpText);
        Assert.Equal(disabled.Reason, disabledState.ToolTip);
        Assert.False(disabledState.ApplyExtraOpacityDimming);

        var enabledState = CapabilityControlPresentation.Map(enabled);
        Assert.True(enabledState.IsEnabled);
        Assert.Equal(string.Empty, enabledState.HelpText);
        Assert.Null(enabledState.ToolTip);
        Assert.False(enabledState.ApplyExtraOpacityDimming);

        var main = Minify(File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.Features.cs")));
        var settings = Minify(File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "SettingsWindow.xaml.cs")));

        Assert.Contains("CapabilityControlPresentation.Map(", main);
        Assert.Contains("CapabilityControlPresentation.Map(", settings);
        Assert.DoesNotContain("Opacity=presentation.IsEnabled?1:0.55", main);
        Assert.DoesNotContain("Opacity=presentation.IsEnabled?1:0.55", settings);
        Assert.DoesNotContain("ApplyExtraOpacityDimming", main + settings);
    }

    [Fact]
    public void AppResources_UseThemeBrushesWithoutHardCodedColors()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "App.xaml"));
        var xaml = document.ToString();

        Assert.DoesNotMatch(new Regex("""#[0-9A-Fa-f]{3,8}"""), xaml);
        Assert.Contains("CardBackgroundFillColorDefaultBrush", xaml);
        Assert.Contains("TextFillColorSecondaryBrush", xaml);
        Assert.Contains("TextFillColorPrimaryBrush", xaml);
        Assert.Contains("XamlControlsResources", xaml);
    }

    private static void AssertNamedAccelerator(XDocument document, string name, string key)
    {
        var element = NamedElement(document, name);
        Assert.Equal(key, AutomationAttribute(element, "AcceleratorKey"));
    }

    private static XElement NamedElement(XDocument document, string name) =>
        Assert.Single(
            document.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == name);

    private static string? AttributeValue(XElement element, string localName) =>
        (string?)element.Attribute(localName)
        ?? (string?)element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName);

    private static string? AutomationAttribute(XElement element, string localName)
    {
        foreach (var attribute in element.Attributes())
        {
            var name = attribute.Name.LocalName;
            if (name == localName || name == $"AutomationProperties.{localName}")
                return attribute.Value;
        }

        return null;
    }

    private static bool UsesThemeResource(XElement element, string brushName) =>
        element.Attributes().Any(attribute =>
            attribute.Value.Contains(brushName, StringComparison.Ordinal)
            && attribute.Value.Contains("ThemeResource", StringComparison.Ordinal));

    private static string Minify(string source) =>
        Regex.Replace(source, @"\s+", string.Empty);

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
