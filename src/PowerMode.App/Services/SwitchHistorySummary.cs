namespace PowerModeWinUI;

/// <summary>
/// Builds a one-line summary of recent mode-switch history for the Insights window:
/// how many switches there were, how many succeeded, and which target mode is used most.
/// Pure over the supplied entries so it is unit-testable without the store or UI.
/// </summary>
internal static class SwitchHistorySummary
{
    public static string Build(
        IReadOnlyList<SwitchHistoryEntry>? history,
        bool isChinese,
        Func<string, string>? modeLabel = null)
    {
        if (history is null || history.Count == 0)
            return isChinese ? "暂无切换记录。" : "No switch history yet.";

        var total = history.Count;
        var succeeded = history.Count(entry => entry.Succeeded);
        var rate = (int)Math.Round(succeeded * 100d / total);

        var mostCommon = history
            .Where(entry => !string.IsNullOrWhiteSpace(entry.TargetMode))
            .GroupBy(entry => entry.TargetMode.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.Key;

        var mostCommonLabel = mostCommon is null
            ? null
            : modeLabel is null ? mostCommon : modeLabel(mostCommon);

        return isChinese
            ? $"近 {total} 次切换:成功 {succeeded}/{total}({rate}%)" +
                (mostCommonLabel is null ? "" : $" · 最常用:{mostCommonLabel}")
            : $"Last {total} switches: {succeeded}/{total} succeeded ({rate}%)" +
                (mostCommonLabel is null ? "" : $" · Most used: {mostCommonLabel}");
    }

    /// <summary>
    /// Builds a "triggers:" breakdown (e.g. manual / automation / advisor / hotkey) from the
    /// same history, most frequent first. Returns an empty string when there is nothing to show.
    /// </summary>
    public static string BuildTriggerSummary(
        IReadOnlyList<SwitchHistoryEntry>? history,
        bool isChinese)
    {
        if (history is null || history.Count == 0)
            return string.Empty;

        var parts = history
            .GroupBy(entry => string.IsNullOrWhiteSpace(entry.Trigger)
                ? "unknown"
                : entry.Trigger.Trim().ToLowerInvariant())
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{TriggerLabel(group.Key, isChinese)} {group.Count()}");

        var joined = string.Join(" · ", parts);
        return joined.Length == 0
            ? string.Empty
            : (isChinese ? "触发来源:" : "Triggers: ") + joined;
    }

    private static string TriggerLabel(string trigger, bool isChinese) => trigger switch
    {
        "manual" => isChinese ? "手动" : "Manual",
        "automation" => isChinese ? "自动化" : "Automation",
        "recommendation" => isChinese ? "建议" : "Advisor",
        "hotkey" => isChinese ? "热键" : "Hotkey",
        "low-battery" => isChinese ? "低电量" : "Low battery",
        "startup" => isChinese ? "启动" : "Startup",
        "undo" => isChinese ? "撤销" : "Undo",
        "unknown" => isChinese ? "未知" : "Unknown",
        _ => trigger
    };
}
