using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ResponsiveLayoutPolicyTests
{
    [Theory]
    [InlineData(720, (int)LayoutTier.Narrow, 1)]
    [InlineData(759, (int)LayoutTier.Narrow, 1)]
    [InlineData(760, (int)LayoutTier.Medium, 2)]
    [InlineData(1039, (int)LayoutTier.Medium, 2)]
    [InlineData(1040, (int)LayoutTier.Wide, 3)]
    public void Evaluate_UsesApprovedBreakpoints(
        double width,
        int tierValue,
        int columns)
    {
        var state = ResponsiveLayoutPolicy.Evaluate(width, ExperienceMode.Simple);

        Assert.Equal((LayoutTier)tierValue, state.Tier);
        Assert.Equal(columns, state.StatusCardColumns);
    }

    [Fact]
    public void ReflowStatusCards_HiddenCardsLeaveNoGaps()
    {
        var placements = ResponsiveLayoutPolicy.ReflowStatusCards(
            [StatusCardId.Mode, StatusCardId.Power, StatusCardId.Cpu, StatusCardId.Sleep],
            3);

        Assert.Equal(
            [(0, 0), (0, 1), (0, 2), (1, 0)],
            placements.Select(item => (item.Row, item.Column)));
    }

    [Fact]
    public void Evaluate_ProfessionalMediumStacksLogAndKeepsCoreActionsVisible()
    {
        var state = ResponsiveLayoutPolicy.Evaluate(900, ExperienceMode.Professional);

        Assert.True(state.StacksProfessionalContent);
        Assert.True(state.ShowSecondaryProfessionalActions);
        Assert.True(state.KeepCoreNavigationVisible);
    }

    [Fact]
    public void Evaluate_SimpleModeUsesContentHeightAndHidesProfessionalToolbar()
    {
        var state = ResponsiveLayoutPolicy.Evaluate(720, ExperienceMode.Simple);

        Assert.True(state.UseContentHeight);
        Assert.False(state.ShowProfessionalToolbarActions);
        Assert.True(state.KeepCoreNavigationVisible);
    }
}
