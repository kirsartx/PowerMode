namespace PowerModeWinUI;

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
    public const double MediumMinimumWidth = 760;

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
}
