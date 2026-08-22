namespace PowerModeWinUI;

internal enum ModeSwitchPresentationSeverity
{
    Informational,
    Success,
    Warning,
    Error
}

internal enum ModeSwitchPresentationAction
{
    None,
    ReviewPartialDetails,
    OpenRecoveryCenter
}

internal sealed record ModeSwitchPresentationState(
    ModeSwitchPresentationSeverity Severity,
    string IconGlyph,
    string Summary,
    string ActionText,
    ModeSwitchPresentationAction Action,
    bool ShowDiagnosticDetails,
    bool IsPersistent,
    string AccessibleName);

internal static class ModeSwitchResultPresentationPolicy
{
    public static ModeSwitchPresentationState Create(
        ModeSwitchResult result,
        ExperienceMode experienceMode,
        bool isChinese)
    {
        ArgumentNullException.ThrowIfNull(result);

        var failed = result.Outcome is
            ModeSwitchOutcome.Failed or
            ModeSwitchOutcome.TimedOut or
            ModeSwitchOutcome.RollbackFailed;
        var severity = result.RequiresRecovery
            ? ModeSwitchPresentationSeverity.Error
            : result.Outcome switch
            {
                ModeSwitchOutcome.Succeeded => ModeSwitchPresentationSeverity.Success,
                ModeSwitchOutcome.Partial => ModeSwitchPresentationSeverity.Warning,
                ModeSwitchOutcome.Cancelled => ModeSwitchPresentationSeverity.Informational,
                _ => ModeSwitchPresentationSeverity.Error
            };
        var action = result.RequiresRecovery || failed
            ? ModeSwitchPresentationAction.OpenRecoveryCenter
            : result.Outcome == ModeSwitchOutcome.Partial
                ? ModeSwitchPresentationAction.ReviewPartialDetails
                : ModeSwitchPresentationAction.None;
        var actionText = action switch
        {
            ModeSwitchPresentationAction.ReviewPartialDetails =>
                isChinese ? "检查未应用的项目" : "Review items not applied",
            ModeSwitchPresentationAction.OpenRecoveryCenter
                when result.Outcome == ModeSwitchOutcome.RollbackFailed ||
                    result.RequiresRecovery =>
                isChinese ? "立即打开恢复中心" : "Open Recovery Center now",
            ModeSwitchPresentationAction.OpenRecoveryCenter =>
                isChinese ? "打开恢复中心" : "Open Recovery Center",
            _ => string.Empty
        };
        var summary = string.IsNullOrWhiteSpace(result.UserSummary)
            ? isChinese ? "操作已完成。" : "The operation completed."
            : result.UserSummary.Trim();

        return new(
            severity,
            result.Outcome switch
            {
                ModeSwitchOutcome.Succeeded => "\uE73E",
                ModeSwitchOutcome.Partial => "\uE7BA",
                ModeSwitchOutcome.Cancelled => "\uE946",
                _ => "\uEA39"
            },
            summary,
            actionText,
            action,
            experienceMode == ExperienceMode.Professional &&
                !string.IsNullOrWhiteSpace(result.DiagnosticSummary),
            result.RequiresRecovery || result.Outcome == ModeSwitchOutcome.RollbackFailed,
            string.IsNullOrWhiteSpace(actionText)
                ? summary
                : $"{summary} {actionText}");
    }
}
