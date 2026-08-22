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
    Power,
    Cpu,
    Sleep
}

internal sealed record ResponsiveLayoutState(
    LayoutTier Tier,
    int StatusCardColumns,
    bool StacksProfessionalContent,
    bool UseContentHeight,
    bool ShowProfessionalToolbarActions,
    bool ShowSecondaryProfessionalActions,
    bool KeepCoreNavigationVisible);

internal sealed record StatusCardPlacement(
    StatusCardId Card,
    int Row,
    int Column);

internal static class ResponsiveLayoutPolicy
{
    public static ResponsiveLayoutState Evaluate(
        double width,
        ExperienceMode experienceMode)
    {
        var tier = width < 760
            ? LayoutTier.Narrow
            : width < 1040
                ? LayoutTier.Medium
                : LayoutTier.Wide;
        var professional = experienceMode == ExperienceMode.Professional;
        return new(
            tier,
            tier switch
            {
                LayoutTier.Narrow => 1,
                LayoutTier.Medium => 2,
                _ => 3
            },
            professional && tier != LayoutTier.Wide,
            !professional,
            professional,
            professional && tier == LayoutTier.Medium,
            true);
    }

    public static IReadOnlyList<StatusCardPlacement> ReflowStatusCards(
        IReadOnlyList<StatusCardId> visibleCards,
        int columns)
    {
        ArgumentNullException.ThrowIfNull(visibleCards);
        columns = Math.Max(1, columns);
        return visibleCards
            .Select((card, index) => new StatusCardPlacement(
                card,
                index / columns,
                index % columns))
            .ToArray();
    }
}
