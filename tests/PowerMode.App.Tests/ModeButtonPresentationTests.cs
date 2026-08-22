using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ModeButtonPresentationTests
{
    [Fact]
    public void Evaluate_CurrentMode_ShowsCheckBadgeAndAutomationState()
    {
        var state = ModeButtonPresentation.Evaluate(
            "balanced", "balanced", null, false, "平衡", true);

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
}
