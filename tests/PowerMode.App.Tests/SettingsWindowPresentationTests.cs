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
        Assert.Contains("SettingsOwnerShutdownCoordinator", mainSource);
        Assert.Contains("_settingsOwnerShutdownCoordinator.RequestAsync", mainSource);
        Assert.Contains("SettingsWindowStrings.On(_zh)", source);
        Assert.Contains("SettingsWindowStrings.Off(_zh)", source);
        Assert.Empty(Regex.Matches(source, @"SettingsStore\.Save\s*\(").Cast<Match>());
    }

    [Theory]
    [InlineData(759, true)]
    [InlineData(760, false)]
    public void SettingsAuxiliaryGridLayout_AppliesEveryNamedGridAndRegion(
        double width,
        bool stack)
    {
        var surface = RecordingAuxiliaryGridSurface.FromXaml(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "SettingsWindow.xaml"));

        SettingsAuxiliaryGridLayout.Apply(width, surface);

        Assert.Equal(2, surface.Columns.Count);
        Assert.Equal(2, surface.Rows.Count);
        var stackedColumns = new[]
        {
            GridLengthProjection.Star,
            GridLengthProjection.Fixed(0)
        };
        var rows = stack
            ? new[] { GridLengthProjection.Auto, GridLengthProjection.Auto }
            : new[] { GridLengthProjection.Star, GridLengthProjection.Fixed(0) };
        AssertGrid(
            surface,
            AuxiliaryGridId.ProfilesEditorGrid,
            stack
                ? stackedColumns
                : [GridLengthProjection.Star, GridLengthProjection.Star],
            rows);
        AssertGrid(
            surface,
            AuxiliaryGridId.RulesEditorGrid,
            stack
                ? stackedColumns
                : [GridLengthProjection.Fixed(280), GridLengthProjection.Star],
            rows);
        Assert.Equal(4, surface.Placements.Count);
        AssertPlacement(surface, AuxiliaryGridId.ProfilesEditorGrid,
            AuxiliaryRegionId.ProfilesPrimaryEditor, 0, 0);
        AssertPlacement(surface, AuxiliaryGridId.ProfilesEditorGrid,
            AuxiliaryRegionId.ProfilesSecondaryEditor, stack ? 1 : 0, stack ? 0 : 1);
        AssertPlacement(surface, AuxiliaryGridId.RulesEditorGrid,
            AuxiliaryRegionId.RulesPrimaryEditor, 0, 0);
        AssertPlacement(surface, AuxiliaryGridId.RulesEditorGrid,
            AuxiliaryRegionId.RulesSecondaryEditor, stack ? 1 : 0, stack ? 0 : 1);
    }

    [Theory]
    [InlineData(759, true)]
    [InlineData(760, false)]
    public void InsightsAuxiliaryGridLayout_AppliesEveryNamedGridAndRegion(
        double width,
        bool stack)
    {
        var surface = RecordingAuxiliaryGridSurface.FromXaml(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "InsightsWindow.xaml"));

        InsightsAuxiliaryGridLayout.Apply(width, surface);

        Assert.Single(surface.Columns);
        Assert.Single(surface.Rows);
        AssertGrid(
            surface,
            AuxiliaryGridId.InsightsOverviewGrid,
            stack
                ? [GridLengthProjection.Star, GridLengthProjection.Fixed(0)]
                :
                [
                    GridLengthProjection.Star,
                    new GridLengthProjection(GridLengthProjectionKind.Star, 2)
                ],
            stack
                ? [GridLengthProjection.Auto, GridLengthProjection.Auto]
                : [GridLengthProjection.Star, GridLengthProjection.Fixed(0)]);
        Assert.Equal(2, surface.Placements.Count);
        AssertPlacement(surface, AuxiliaryGridId.InsightsOverviewGrid,
            AuxiliaryRegionId.InsightsMetricsRegion, 0, 0);
        AssertPlacement(surface, AuxiliaryGridId.InsightsOverviewGrid,
            AuxiliaryRegionId.InsightsTrendRegion, stack ? 1 : 0, stack ? 0 : 1);
    }

    private static void AssertGrid(
        RecordingAuxiliaryGridSurface surface,
        AuxiliaryGridId grid,
        IReadOnlyList<GridLengthProjection> columns,
        IReadOnlyList<GridLengthProjection> rows)
    {
        Assert.Equal(columns, surface.Columns[grid]);
        Assert.Equal(rows, surface.Rows[grid]);
    }

    private static void AssertPlacement(
        RecordingAuxiliaryGridSurface surface,
        AuxiliaryGridId grid,
        AuxiliaryRegionId region,
        int row,
        int column) =>
        Assert.Equal(
            new RegionPlacement(row, column),
            surface.Placements[(grid, region)]);

    private sealed class RecordingAuxiliaryGridSurface : IAuxiliaryGridSurface
    {
        private readonly IReadOnlyDictionary<string, XElement> _declaredElements;

        private RecordingAuxiliaryGridSurface(
            IReadOnlyDictionary<string, XElement> declaredElements) =>
            _declaredElements = declaredElements;

        public Dictionary<AuxiliaryGridId, IReadOnlyList<GridLengthProjection>> Columns
        {
            get;
        } = [];

        public Dictionary<AuxiliaryGridId, IReadOnlyList<GridLengthProjection>> Rows
        {
            get;
        } = [];

        public Dictionary<(AuxiliaryGridId Grid, AuxiliaryRegionId Region), RegionPlacement>
            Placements { get; } = [];

        public static RecordingAuxiliaryGridSurface FromXaml(string path)
        {
            var document = XDocument.Load(path);
            return new(document.Descendants()
                .Where(element => element.Attribute(Xaml + "Name") is not null)
                .ToDictionary(
                    element => (string)element.Attribute(Xaml + "Name")!,
                    StringComparer.Ordinal));
        }

        public void SetColumnDefinitions(
            AuxiliaryGridId grid,
            IReadOnlyList<GridLengthProjection> columns)
        {
            var element = RequireGrid(grid);
            RequireDefinitionCount(element, "Grid.ColumnDefinitions", columns.Count);
            Columns.Add(grid, columns.ToArray());
        }

        public void SetRowDefinitions(
            AuxiliaryGridId grid,
            IReadOnlyList<GridLengthProjection> rows)
        {
            var element = RequireGrid(grid);
            RequireDefinitionCount(element, "Grid.RowDefinitions", rows.Count);
            Rows.Add(grid, rows.ToArray());
        }

        public void SetRegionPlacement(
            AuxiliaryGridId grid,
            AuxiliaryRegionId region,
            RegionPlacement placement)
        {
            _ = RequireGrid(grid);
            var element = RequireDeclared(region.ToString());
            if (!element.Ancestors().Any(ancestor =>
                    (string?)ancestor.Attribute(Xaml + "Name") == grid.ToString()))
            {
                throw new InvalidOperationException(
                    $"Region '{region}' is not declared inside grid '{grid}'.");
            }
            Placements.Add((grid, region), placement);
        }

        private XElement RequireGrid(AuxiliaryGridId grid)
        {
            var element = RequireDeclared(grid.ToString());
            if (element.Name.LocalName != "Grid")
            {
                throw new InvalidOperationException(
                    $"Auxiliary grid '{grid}' is declared as '{element.Name.LocalName}'.");
            }

            return element;
        }

        private XElement RequireDeclared(string name) =>
            _declaredElements.TryGetValue(name, out var element)
                ? element
                : throw new InvalidOperationException(
                    $"The production auxiliary layout references undeclared XAML element '{name}'.");

        private static void RequireDefinitionCount(
            XElement grid,
            string definitionsName,
            int expectedCount)
        {
            var definitions = grid.Elements()
                .SingleOrDefault(element => element.Name.LocalName == definitionsName);
            var actualCount = definitions?.Elements().Count() ?? 0;
            if (actualCount != expectedCount)
            {
                throw new InvalidOperationException(
                    $"Grid '{(string?)grid.Attribute(Xaml + "Name")}' declares " +
                    $"{actualCount} {definitionsName}; expected {expectedCount}.");
            }
        }
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
