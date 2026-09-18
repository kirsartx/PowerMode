namespace PowerModeWinUI;

internal enum LayoutTier
{
    Narrow,
    Medium,
    Wide
}

internal enum StatusCardId
{
    Mode,
    Gpu,
    Power,
    Cpu,
    Brightness,
    Sleep
}

internal enum ToolbarAction
{
    Experience,
    Auto,
    Live,
    Refresh,
    Features,
    Insights,
    Recovery,
    Language
}

internal enum ToolbarDisplay
{
    Hidden,
    Full,
    IconOnly,
    Overflow
}

internal sealed record ResponsiveLayoutState(
    LayoutTier Tier,
    int StatusCardColumns,
    bool StackMainContent,
    bool ModePanelUsesContentHeight,
    double ProfessionalLogMinimumHeight,
    IReadOnlyDictionary<ToolbarAction, ToolbarDisplay> Toolbar);

internal readonly record struct CardPlacement(
    StatusCardId Card,
    int Row,
    int Column);

internal enum StandardModeButtonId
{
    Remote,
    Saver,
    Balanced,
    High
}

internal readonly record struct ModeButtonPlacement(
    StandardModeButtonId Button,
    int Row,
    int Column);

internal enum GridLengthProjectionKind
{
    Auto,
    Star,
    Fixed
}

internal readonly record struct GridLengthProjection(
    GridLengthProjectionKind Kind,
    double Value)
{
    public static GridLengthProjection Auto { get; } =
        new(GridLengthProjectionKind.Auto, 0);
    public static GridLengthProjection Star { get; } =
        new(GridLengthProjectionKind.Star, 1);
    public static GridLengthProjection Fixed(double value) =>
        new(GridLengthProjectionKind.Fixed, value);
}

internal sealed record ResponsiveDashboardProjection(
    double LogicalWidth,
    double LogicalHeight,
    ResponsiveLayoutState Layout,
    IReadOnlyList<GridLengthProjection> RootRows,
    bool EnableVerticalContentScroll,
    IReadOnlyList<GridLengthProjection> ContentRows,
    IReadOnlyList<GridLengthProjection> MainColumns,
    IReadOnlyList<GridLengthProjection> MainRows,
    IReadOnlyList<CardPlacement> StatusCards,
    int StatusCardRows,
    IReadOnlyList<ModeButtonPlacement> ModeButtons,
    int ModeButtonColumns,
    bool StackRecommendationContent,
    bool ShowToolbarOverflow);

internal sealed record AuxiliaryLayoutProjection(
    bool Stack,
    int FirstRow,
    int FirstColumn,
    int SecondRow,
    int SecondColumn,
    IReadOnlyList<GridLengthProjection> Columns,
    IReadOnlyList<GridLengthProjection> Rows);

internal readonly record struct RegionPlacement(int Row, int Column);

internal static class ResponsiveLayoutPolicy
{
    public const double DefaultWidth = 1120;
    public const double DefaultHeight = 760;
    public const double MinimumWidth = 720;
    public const double MinimumHeight = 560;
    public const double MediumMinimumWidth = 760;
    public const double WideMinimumWidth = 1040;

    // The layout depends only on (tier, experienceMode): 3 x 2 combinations. Resize fires
    // SizeChanged continuously, so memoize to avoid rebuilding the toolbar dictionary each pass.
    // ResponsiveLayoutState is immutable and consumers only read it, so sharing is safe.
    private static readonly ResponsiveLayoutState[,] LayoutCache =
        new ResponsiveLayoutState[3, 2];

    public static ResponsiveLayoutState Evaluate(
        double logicalWidth,
        ExperienceMode experienceMode)
    {
        var tier = logicalWidth >= WideMinimumWidth
            ? LayoutTier.Wide
            : logicalWidth >= MediumMinimumWidth
                ? LayoutTier.Medium
                : LayoutTier.Narrow;

        var tierIndex = (int)tier;
        var modeIndex = experienceMode == ExperienceMode.Simple ? 0 : 1;
        var cached = Volatile.Read(ref LayoutCache[tierIndex, modeIndex]);
        if (cached is not null)
            return cached;

        var state = BuildLayout(tier, experienceMode);
        Volatile.Write(ref LayoutCache[tierIndex, modeIndex], state);
        return state;
    }

    private static ResponsiveLayoutState BuildLayout(
        LayoutTier tier,
        ExperienceMode experienceMode)
    {
        var toolbar = Enum.GetValues<ToolbarAction>().ToDictionary(
            action => action,
            action => action switch
            {
                ToolbarAction.Experience or ToolbarAction.Recovery
                    or ToolbarAction.Language => tier == LayoutTier.Wide
                        ? ToolbarDisplay.Full
                        : ToolbarDisplay.IconOnly,
                ToolbarAction.Refresh => tier == LayoutTier.Narrow
                    ? ToolbarDisplay.IconOnly
                    : ToolbarDisplay.Full,
                _ when experienceMode == ExperienceMode.Simple =>
                    ToolbarDisplay.Hidden,
                _ => tier switch
                {
                    LayoutTier.Wide => ToolbarDisplay.Full,
                    LayoutTier.Medium => ToolbarDisplay.IconOnly,
                    _ => ToolbarDisplay.Overflow
                }
            });

        return new(
            tier,
            tier switch
            {
                LayoutTier.Wide => 3,
                LayoutTier.Medium => 2,
                _ => 1
            },
            experienceMode == ExperienceMode.Professional &&
                tier != LayoutTier.Wide,
            experienceMode == ExperienceMode.Simple,
            tier == LayoutTier.Narrow ? 180 : 220,
            toolbar);
    }

    public static IReadOnlyList<CardPlacement> ReflowStatusCards(
        IReadOnlyList<StatusCardId> visibleCards,
        int columnCount)
    {
        ArgumentNullException.ThrowIfNull(visibleCards);
        ArgumentOutOfRangeException.ThrowIfLessThan(columnCount, 1);

        return visibleCards
            .Select((card, index) =>
                new CardPlacement(card, index / columnCount, index % columnCount))
            .ToArray();
    }

    public static bool ShouldStackAuxiliaryContent(double logicalWidth) =>
        logicalWidth < MediumMinimumWidth;

    public static AuxiliaryLayoutProjection ProjectAuxiliaryContent(
        double logicalWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(logicalWidth, 0);
        var stack = ShouldStackAuxiliaryContent(logicalWidth);
        return new(
            stack,
            0,
            0,
            stack ? 1 : 0,
            stack ? 0 : 1,
            stack
                ? [GridLengthProjection.Star, GridLengthProjection.Fixed(0)]
                : [GridLengthProjection.Star, GridLengthProjection.Star],
            stack
                ? [GridLengthProjection.Auto, GridLengthProjection.Auto]
                : [GridLengthProjection.Star, GridLengthProjection.Fixed(0)]);
    }

    public static SettingsAuxiliaryLayoutProjection ProjectSettingsAuxiliaryContent(
        double logicalWidth)
    {
        var layout = ProjectAuxiliaryContent(logicalWidth);
        var primary = new RegionPlacement(layout.FirstRow, layout.FirstColumn);
        var secondary = new RegionPlacement(layout.SecondRow, layout.SecondColumn);
        return new(
            [
                new AuxiliaryGridProjection(
                    AuxiliaryGridId.ProfilesEditorGrid,
                    layout.Columns,
                    layout.Rows,
                    new Dictionary<AuxiliaryRegionId, RegionPlacement>
                    {
                        [AuxiliaryRegionId.ProfilesPrimaryEditor] = primary,
                        [AuxiliaryRegionId.ProfilesSecondaryEditor] = secondary
                    }),
                new AuxiliaryGridProjection(
                    AuxiliaryGridId.RulesEditorGrid,
                    layout.Stack
                        ? layout.Columns
                        :
                        [
                            GridLengthProjection.Fixed(280),
                            GridLengthProjection.Star
                        ],
                    layout.Rows,
                    new Dictionary<AuxiliaryRegionId, RegionPlacement>
                    {
                        [AuxiliaryRegionId.RulesPrimaryEditor] = primary,
                        [AuxiliaryRegionId.RulesSecondaryEditor] = secondary
                    })
            ]);
    }

    public static InsightsAuxiliaryLayoutProjection ProjectInsightsAuxiliaryContent(
        double logicalWidth)
    {
        var layout = ProjectAuxiliaryContent(logicalWidth);
        return new(
            [
                new AuxiliaryGridProjection(
                    AuxiliaryGridId.InsightsOverviewGrid,
                    layout.Stack
                        ? layout.Columns
                        :
                        [
                            GridLengthProjection.Star,
                            new GridLengthProjection(
                                GridLengthProjectionKind.Star,
                                2)
                        ],
                    layout.Rows,
                    new Dictionary<AuxiliaryRegionId, RegionPlacement>
                    {
                        [AuxiliaryRegionId.InsightsMetricsRegion] =
                            new(layout.FirstRow, layout.FirstColumn),
                        [AuxiliaryRegionId.InsightsTrendRegion] =
                            new(layout.SecondRow, layout.SecondColumn)
                    })
            ]);
    }

    public static ResponsiveDashboardProjection Project(
        double logicalWidth,
        double logicalHeight,
        ExperienceMode experienceMode,
        IReadOnlyList<StatusCardId> visibleCards)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(logicalWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(logicalHeight, 0);
        ArgumentNullException.ThrowIfNull(visibleCards);

        var layout = Evaluate(logicalWidth, experienceMode);
        var statusCards = ReflowStatusCards(
            visibleCards,
            layout.StatusCardColumns);
        var statusRows = Math.Max(
            1,
            (visibleCards.Count + layout.StatusCardColumns - 1) /
                layout.StatusCardColumns);
        var enableVerticalScroll = layout.Tier != LayoutTier.Wide;
        var professional = experienceMode == ExperienceMode.Professional;
        var modeButtonColumns = professional && layout.Tier == LayoutTier.Wide
            ? 1
            : 2;
        var modeButtons = Enum.GetValues<StandardModeButtonId>()
            .Select((button, index) => new ModeButtonPlacement(
                button,
                index / modeButtonColumns,
                index % modeButtonColumns))
            .ToArray();

        IReadOnlyList<GridLengthProjection> mainColumns;
        IReadOnlyList<GridLengthProjection> mainRows;
        if (layout.ModePanelUsesContentHeight)
        {
            mainColumns = [GridLengthProjection.Star, GridLengthProjection.Fixed(0)];
            mainRows = [GridLengthProjection.Auto, GridLengthProjection.Fixed(0)];
        }
        else if (layout.StackMainContent)
        {
            mainColumns = [GridLengthProjection.Star, GridLengthProjection.Fixed(0)];
            mainRows = [GridLengthProjection.Auto, GridLengthProjection.Auto];
        }
        else
        {
            mainColumns = [GridLengthProjection.Fixed(390), GridLengthProjection.Star];
            mainRows = [GridLengthProjection.Star, GridLengthProjection.Fixed(0)];
        }

        return new(
            logicalWidth,
            logicalHeight,
            layout,
            [GridLengthProjection.Auto, GridLengthProjection.Star, GridLengthProjection.Auto],
            enableVerticalScroll,
            enableVerticalScroll
                ? [GridLengthProjection.Auto, GridLengthProjection.Auto]
                : [GridLengthProjection.Auto, GridLengthProjection.Star],
            mainColumns,
            mainRows,
            statusCards,
            statusRows,
            modeButtons,
            modeButtonColumns,
            ShouldStackAuxiliaryContent(logicalWidth),
            layout.Toolbar.Values.Any(display => display == ToolbarDisplay.Overflow));
    }
}
