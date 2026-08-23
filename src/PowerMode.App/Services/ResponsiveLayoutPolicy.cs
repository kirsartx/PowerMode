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

internal static class ResponsiveLayoutPolicy
{
    public const double MinimumWidth = 720;
    public const double MediumMinimumWidth = 760;
    public const double WideMinimumWidth = 1040;

    public static ResponsiveLayoutState Evaluate(
        double logicalWidth,
        ExperienceMode experienceMode)
    {
        var tier = logicalWidth >= WideMinimumWidth
            ? LayoutTier.Wide
            : logicalWidth >= MediumMinimumWidth
                ? LayoutTier.Medium
                : LayoutTier.Narrow;
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
}
