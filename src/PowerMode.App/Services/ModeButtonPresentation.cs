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
        var itemStatus = isCurrent
            ? (isChinese ? "当前模式" : "Current mode")
            : isPending
                ? pendingText
                : string.Empty;

        return new(
            isCurrent,
            !switchInProgress,
            isCurrent,
            isCurrent,
            isPending,
            isCurrent ? currentText : string.Empty,
            string.IsNullOrWhiteSpace(itemStatus)
                ? localizedModeName.Trim()
                : $"{localizedModeName.Trim()}{(isChinese ? "，" : ", ")}{itemStatus}",
            itemStatus);
    }
}
