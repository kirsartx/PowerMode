using System.Xml.Linq;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class RefreshInteractionPresentationTests
{
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void RefreshButton_DeclaresExplicitKeyboardAndAccessibleEntry()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml"));
        var refreshButton = NamedElement(document, "RefreshButton");

        Assert.Equal("True", AttributeValue(refreshButton, "IsTabStop"));
        Assert.Equal("RefreshButton_Click", AttributeValue(refreshButton, "Click"));
        Assert.Equal("F5", AutomationAttribute(refreshButton, "AcceleratorKey"));

        var accelerator = Assert.Single(
            refreshButton.Descendants(),
            element => element.Name.LocalName == "KeyboardAccelerator");
        Assert.Equal("F5", AttributeValue(accelerator, "Key"));
        Assert.Equal(
            "RefreshKeyboardAccelerator_Invoked",
            AttributeValue(accelerator, "Invoked"));
    }

    [Fact]
    public void RefreshRoute_HandlesKeyboardActivationOnceAndDescribesReadOnlyAction()
    {
        var mainWindow = Minify(File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml.cs")));
        var featureWindow = Minify(File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.Features.cs")));

        Assert.Contains("RefreshKeyboardAccelerator_Invoked", mainWindow);
        Assert.Contains("args.Handled=true", mainWindow);
        Assert.Contains("awaitRefreshStatusAsync()", mainWindow);
        Assert.Contains("AutomationProperties.SetHelpText(RefreshButton", mainWindow);
        Assert.Contains("重新读取状态（只读，F5）", mainWindow);
        Assert.Contains("Reloadstatus(read-only,F5)", mainWindow);

        Assert.DoesNotContain("e.Key==VirtualKey.F5", featureWindow);
        Assert.Contains("VirtualKey.Number1", featureWindow);
        Assert.Contains("VirtualKey.Number4", featureWindow);
    }

    private static XElement NamedElement(XDocument document, string name) =>
        Assert.Single(
            document.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == name);

    private static string? AttributeValue(XElement element, string localName) =>
        (string?)element.Attribute(localName)
        ?? (string?)element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName);

    private static string? AutomationAttribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName == localName
                || attribute.Name.LocalName == $"AutomationProperties.{localName}")?.Value;

    private static string Minify(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, @"\s+", string.Empty);

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
