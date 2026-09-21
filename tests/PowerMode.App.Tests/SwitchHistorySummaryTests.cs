using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class SwitchHistorySummaryTests
{
    [Fact]
    public void Build_Empty_ReturnsNoHistoryPlaceholder()
    {
        Assert.Equal("暂无切换记录。", SwitchHistorySummary.Build([], isChinese: true));
        Assert.Equal(
            "No switch history yet.",
            SwitchHistorySummary.Build(null, isChinese: false));
    }

    [Fact]
    public void Build_CountsSuccessesAndPicksMostUsedMode()
    {
        var history = new[]
        {
            Entry("balanced", succeeded: true),
            Entry("balanced", succeeded: true),
            Entry("remote", succeeded: true),
            Entry("remote", succeeded: false)
        };

        var result = SwitchHistorySummary.Build(history, isChinese: true);

        Assert.Equal("近 4 次切换:成功 3/4(75%) · 最常用:balanced", result);
    }

    [Fact]
    public void Build_AppliesModeLabelResolverAndIsCaseInsensitive()
    {
        var history = new[]
        {
            Entry("Balanced", succeeded: true),
            Entry("balanced", succeeded: true),
            Entry("HIGH", succeeded: false)
        };

        var result = SwitchHistorySummary.Build(
            history,
            isChinese: false,
            modeLabel: mode => mode.ToLowerInvariant() switch
            {
                "balanced" => "Balanced",
                "high" => "High",
                _ => mode
            });

        Assert.Equal("Last 3 switches: 2/3 succeeded (67%) · Most used: Balanced", result);
    }

    [Fact]
    public void Build_AllFail_ShowsZeroRate()
    {
        var history = new[] { Entry("saver", succeeded: false), Entry("saver", succeeded: false) };

        var result = SwitchHistorySummary.Build(history, isChinese: false);

        Assert.Equal("Last 2 switches: 0/2 succeeded (0%) · Most used: saver", result);
    }

    [Fact]
    public void BuildTriggerSummary_CountsByTriggerMostFrequentFirst()
    {
        var history = new[]
        {
            Entry("balanced", true, "manual"),
            Entry("high", true, "automation"),
            Entry("high", true, "automation"),
            Entry("saver", true, "hotkey")
        };

        Assert.Equal(
            "触发来源:自动化 2 · 热键 1 · 手动 1",
            SwitchHistorySummary.BuildTriggerSummary(history, isChinese: true));
        Assert.Equal(
            "Triggers: Automation 2 · Hotkey 1 · Manual 1",
            SwitchHistorySummary.BuildTriggerSummary(history, isChinese: false));
    }

    [Fact]
    public void BuildTriggerSummary_EmptyReturnsBlankAndNormalizesBlankTrigger()
    {
        Assert.Equal(string.Empty, SwitchHistorySummary.BuildTriggerSummary([], isChinese: true));

        var history = new[] { Entry("balanced", true, "  ") };
        Assert.Equal(
            "触发来源:未知 1",
            SwitchHistorySummary.BuildTriggerSummary(history, isChinese: true));
    }

    private static SwitchHistoryEntry Entry(string mode, bool succeeded) => new()
    {
        TargetMode = mode,
        Succeeded = succeeded,
        Trigger = "manual"
    };

    private static SwitchHistoryEntry Entry(string mode, bool succeeded, string trigger) => new()
    {
        TargetMode = mode,
        Succeeded = succeeded,
        Trigger = trigger
    };
}
