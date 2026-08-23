using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PowerModeWinUI;

internal sealed class WinUiAuxiliaryGridSurface(FrameworkElement root) :
    IAuxiliaryGridSurface
{
    public void SetColumnDefinitions(
        AuxiliaryGridId grid,
        IReadOnlyList<GridLengthProjection> columns)
    {
        var target = ResolveGrid(grid);
        if (target.ColumnDefinitions.Count != columns.Count)
        {
            throw new InvalidOperationException(
                $"Grid '{grid}' has {target.ColumnDefinitions.Count} columns; " +
                $"the auxiliary layout requires {columns.Count}.");
        }

        for (var index = 0; index < columns.Count; index++)
            target.ColumnDefinitions[index].Width = ToGridLength(columns[index]);
    }

    public void SetRowDefinitions(
        AuxiliaryGridId grid,
        IReadOnlyList<GridLengthProjection> rows)
    {
        var target = ResolveGrid(grid);
        if (target.RowDefinitions.Count != rows.Count)
        {
            throw new InvalidOperationException(
                $"Grid '{grid}' has {target.RowDefinitions.Count} rows; " +
                $"the auxiliary layout requires {rows.Count}.");
        }

        for (var index = 0; index < rows.Count; index++)
            target.RowDefinitions[index].Height = ToGridLength(rows[index]);
    }

    public void SetRegionPlacement(
        AuxiliaryGridId grid,
        AuxiliaryRegionId region,
        RegionPlacement placement)
    {
        _ = ResolveGrid(grid);
        var target = Resolve<FrameworkElement>(region.ToString());
        Grid.SetRow(target, placement.Row);
        Grid.SetColumn(target, placement.Column);
    }

    private Grid ResolveGrid(AuxiliaryGridId grid) =>
        Resolve<Grid>(grid.ToString());

    private T Resolve<T>(string name) where T : class =>
        root.FindName(name) as T ?? throw new InvalidOperationException(
            $"Auxiliary layout element '{name}' was not found as {typeof(T).Name}.");

    private static GridLength ToGridLength(GridLengthProjection projection) =>
        projection.Kind switch
        {
            GridLengthProjectionKind.Auto => GridLength.Auto,
            GridLengthProjectionKind.Star =>
                new GridLength(projection.Value, GridUnitType.Star),
            GridLengthProjectionKind.Fixed => new GridLength(projection.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(projection))
        };
}
