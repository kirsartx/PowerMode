using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ModeButtonPresentationTests
{
    [Fact]
    public void Evaluate_CurrentMode_ShowsCheckBadgeAndAutomationState()
    {
        var state = ModeButtonPresentation.Evaluate(
            "balanced", "balanced", null, false, "平衡", true);

        Assert.True(state.IsCurrent);
        Assert.True(state.ShowCheckmark);
        Assert.True(state.ShowCurrentBadge);
        Assert.Equal("当前", state.BadgeText);
        Assert.Contains("当前", state.AutomationName);
        Assert.Equal("当前模式", state.ItemStatus);
    }

    [Fact]
    public void Evaluate_SwitchInProgress_DisablesAllAndMarksOnlyTarget()
    {
        var target = ModeButtonPresentation.Evaluate(
            "remote", "balanced", "remote", true, "远程推荐", true);
        var other = ModeButtonPresentation.Evaluate(
            "saver", "balanced", "remote", true, "低功耗", true);

        Assert.False(target.IsEnabled);
        Assert.False(target.IsCurrent);
        Assert.True(target.ShowProgress);
        Assert.False(other.IsEnabled);
        Assert.False(other.ShowProgress);
    }

    [Fact]
    public void Evaluate_NonCurrentModeDoesNotUseColorOnlyState()
    {
        var state = ModeButtonPresentation.Evaluate(
            "high", "balanced", null, false, "高性能", true);

        Assert.False(state.ShowCheckmark);
        Assert.False(state.ShowCurrentBadge);
        Assert.Equal("", state.ItemStatus);
        Assert.Contains("高性能", state.AutomationName);
    }

    [Fact]
    public void Evaluate_UnknownActiveModeLeavesEveryModeUnselected()
    {
        var state = ModeButtonPresentation.Evaluate(
            "balanced", null, null, false, "Balanced", false);

        Assert.False(state.IsCurrent);
        Assert.True(state.IsEnabled);
        Assert.False(state.ShowCheckmark);
        Assert.False(state.ShowCurrentBadge);
        Assert.False(state.ShowProgress);
        Assert.Equal(string.Empty, state.BadgeText);
        Assert.Equal("Balanced", state.AutomationName);
        Assert.Equal(string.Empty, state.ItemStatus);
    }

    [Fact]
    public void Evaluate_CurrentModeDuringAnotherSwitchRemainsMarkedButDisabled()
    {
        var state = ModeButtonPresentation.Evaluate(
            "balanced", "balanced", "high", true, "Balanced", false);

        Assert.True(state.IsCurrent);
        Assert.False(state.IsEnabled);
        Assert.True(state.ShowCheckmark);
        Assert.True(state.ShowCurrentBadge);
        Assert.False(state.ShowProgress);
        Assert.Equal("Current", state.BadgeText);
        Assert.Equal("Balanced, Current mode", state.AutomationName);
        Assert.Equal("Current mode", state.ItemStatus);
    }

    [Theory]
    [InlineData(true, "平衡，当前模式，正在切换", "当前模式，正在切换")]
    [InlineData(false, "Balanced, Current mode, Switching", "Current mode, Switching")]
    public void Evaluate_CurrentModeIsAlsoPending_ExposesBothStates(
        bool isChinese,
        string expectedAutomationName,
        string expectedItemStatus)
    {
        var state = ModeButtonPresentation.Evaluate(
            "balanced",
            "balanced",
            "balanced",
            switchInProgress: true,
            isChinese ? "平衡" : "Balanced",
            isChinese);

        Assert.True(state.IsCurrent);
        Assert.True(state.ShowProgress);
        Assert.Equal(expectedAutomationName, state.AutomationName);
        Assert.Equal(expectedItemStatus, state.ItemStatus);
    }

    [Fact]
    public void ActiveProjection_ReadBackTransitionRefreshesModesAndRecommendation()
    {
        var recommendation = new ModeRecommendation(
            "balanced",
            "reason",
            IsComplete: true,
            DateTimeOffset.Now,
            RecommendationReasonCode.DailyAc);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["remote"] = "Remote",
            ["saver"] = "Saver",
            ["balanced"] = "Balanced",
            ["high"] = "High performance"
        };

        var before = ActiveModePresentation.Project(
            "saver", null, false, false, recommendation, names, false);
        var after = ActiveModePresentation.Project(
            "balanced", null, false, false, recommendation, names, false);

        Assert.False(before.ModeButtons["balanced"].IsCurrent);
        Assert.True(before.Recommendation!.IsEnabled);
        Assert.Equal("Apply", before.Recommendation.Text);
        Assert.True(after.ModeButtons["balanced"].IsCurrent);
        Assert.Contains("Current mode", after.ModeButtons["balanced"].AutomationName);
        Assert.False(after.Recommendation!.IsEnabled);
        Assert.Equal("Current mode", after.Recommendation.Text);
    }
}
