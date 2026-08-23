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
}
