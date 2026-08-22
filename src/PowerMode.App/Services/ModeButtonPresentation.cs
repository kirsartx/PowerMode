namespace PowerModeWinUI;

internal sealed record ModeButtonPresentationState(
    bool IsEnabled,
    bool ShowCheckmark,
    bool ShowCurrentBadge,
    string BadgeText,
    bool ShowProgress,
    string AutomationName,
    string ItemStatus);

internal static class ModeButtonPresentation
{
    public static ModeButtonPresentationState Evaluate(
        string mode,
        string currentMode,
        string? pendingMode,
        bool isSwitching,
        string displayName,
        bool isChinese)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var normalizedMode = mode.Trim();
        var normalizedCurrent = currentMode.Trim();
        var normalizedPending = pendingMode?.Trim();
        var isCurrent = string.Equals(
            normalizedMode,
            normalizedCurrent,
            StringComparison.OrdinalIgnoreCase);
        var isPending = isSwitching && string.Equals(
            normalizedMode,
            normalizedPending,
            StringComparison.OrdinalIgnoreCase);
        var currentText = isChinese ? "当前" : "Current";
        var switchingText = isChinese ? "正在切换" : "Switching";
        var automationName = displayName.Trim();
        if (isCurrent)
            automationName += isChinese ? "，当前模式" : ", current mode";
        else if (isPending)
            automationName += isChinese ? "，正在切换" : ", switching";

        return new(
            !isSwitching,
            isCurrent,
            isCurrent,
            isCurrent ? currentText : string.Empty,
            isPending,
            automationName,
            isCurrent ? (isChinese ? "当前模式" : "Current mode") :
                isPending ? switchingText : string.Empty);
    }
}
