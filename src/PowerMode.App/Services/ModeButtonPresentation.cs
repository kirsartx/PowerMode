namespace PowerModeWinUI;

internal sealed record ModeButtonPresentationState(
    bool IsCurrent,
    bool IsEnabled,
    bool ShowCheckmark,
    bool ShowCurrentBadge,
    bool ShowProgress,
    string BadgeText,
    string AutomationName,
    string ItemStatus);

internal sealed record ActiveModePresentationState(
    IReadOnlyDictionary<string, ModeButtonPresentationState> ModeButtons,
    RecommendationApplyButtonState? Recommendation);

internal static class ModeButtonPresentation
{
    public static ModeButtonPresentationState Evaluate(
        string buttonMode,
        string? activeMode,
        string? pendingMode,
        bool switchInProgress,
        string localizedModeName,
        bool isChinese)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buttonMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(localizedModeName);

        var isCurrent = string.Equals(
            buttonMode.Trim(),
            activeMode?.Trim(),
            StringComparison.OrdinalIgnoreCase);
        var isPending = switchInProgress && string.Equals(
            buttonMode.Trim(),
            pendingMode?.Trim(),
            StringComparison.OrdinalIgnoreCase);
        var currentText = isChinese ? "当前" : "Current";
        var pendingText = isChinese ? "正在切换" : "Switching";
        var currentStatus = isChinese ? "当前模式" : "Current mode";
        var separator = isChinese ? "，" : ", ";
        var itemStatus = (isCurrent, isPending) switch
        {
            (true, true) => $"{currentStatus}{separator}{pendingText}",
            (true, false) => currentStatus,
            (false, true) => pendingText,
            _ => string.Empty
        };

        return new(
            isCurrent,
            !switchInProgress,
            isCurrent,
            isCurrent,
            isPending,
            isCurrent ? currentText : string.Empty,
            string.IsNullOrWhiteSpace(itemStatus)
                ? localizedModeName.Trim()
                : $"{localizedModeName.Trim()}{separator}{itemStatus}",
            itemStatus);
    }
}

internal static class ActiveModePresentation
{
    private static readonly string[] StandardModes =
        ["remote", "saver", "balanced", "high"];

    public static ActiveModePresentationState Project(
        string? activeMode,
        string? pendingMode,
        bool switchInProgress,
        bool recommendationApplyInProgress,
        ModeRecommendation? recommendation,
        IReadOnlyDictionary<string, string> localizedModeNames,
        bool isChinese)
    {
        ArgumentNullException.ThrowIfNull(localizedModeNames);

        var modeButtons = StandardModes.ToDictionary(
            mode => mode,
            mode => ModeButtonPresentation.Evaluate(
                mode,
                activeMode,
                pendingMode,
                switchInProgress,
                localizedModeNames.TryGetValue(mode, out var name)
                    ? name
                    : mode,
                isChinese),
            StringComparer.OrdinalIgnoreCase);
        var recommendationState = recommendation is null
            ? null
            : RecommendationUiLogic.CreateApplyButtonState(
                recommendation,
                activeMode,
                recommendationApplyInProgress || switchInProgress,
                isChinese);

        return new(modeButtons, recommendationState);
    }
}
