namespace PowerModeWinUI;

internal enum AuxiliaryGridId
{
    ProfilesEditorGrid,
    RulesEditorGrid,
    InsightsOverviewGrid
}

internal enum AuxiliaryRegionId
{
    ProfilesPrimaryEditor,
    ProfilesSecondaryEditor,
    RulesPrimaryEditor,
    RulesSecondaryEditor,
    InsightsMetricsRegion,
    InsightsTrendRegion
}

internal sealed record AuxiliaryGridProjection(
    AuxiliaryGridId Grid,
    IReadOnlyList<GridLengthProjection> Columns,
    IReadOnlyList<GridLengthProjection> Rows,
    IReadOnlyDictionary<AuxiliaryRegionId, RegionPlacement> Regions);

internal sealed record SettingsAuxiliaryLayoutProjection(
    IReadOnlyList<AuxiliaryGridProjection> Grids);

internal sealed record InsightsAuxiliaryLayoutProjection(
    IReadOnlyList<AuxiliaryGridProjection> Grids);

internal interface IAuxiliaryGridSurface
{
    void SetColumnDefinitions(
        AuxiliaryGridId grid,
        IReadOnlyList<GridLengthProjection> columns);

    void SetRowDefinitions(
        AuxiliaryGridId grid,
        IReadOnlyList<GridLengthProjection> rows);

    void SetRegionPlacement(
        AuxiliaryGridId grid,
        AuxiliaryRegionId region,
        RegionPlacement placement);
}

internal static class AuxiliaryGridApplier
{
    public static void Apply(
        IReadOnlyList<AuxiliaryGridProjection> grids,
        IAuxiliaryGridSurface surface)
    {
        ArgumentNullException.ThrowIfNull(grids);
        ArgumentNullException.ThrowIfNull(surface);

        foreach (var grid in grids)
        {
            surface.SetColumnDefinitions(grid.Grid, grid.Columns);
            surface.SetRowDefinitions(grid.Grid, grid.Rows);
            foreach (var (region, placement) in grid.Regions)
                surface.SetRegionPlacement(grid.Grid, region, placement);
        }
    }
}

internal static class SettingsAuxiliaryGridLayout
{
    public static void Apply(double logicalWidth, IAuxiliaryGridSurface surface) =>
        AuxiliaryGridApplier.Apply(
            ResponsiveLayoutPolicy.ProjectSettingsAuxiliaryContent(logicalWidth).Grids,
            surface);
}

internal static class InsightsAuxiliaryGridLayout
{
    public static void Apply(double logicalWidth, IAuxiliaryGridSurface surface) =>
        AuxiliaryGridApplier.Apply(
            ResponsiveLayoutPolicy.ProjectInsightsAuxiliaryContent(logicalWidth).Grids,
            surface);
}
