using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ResponsiveLayoutPolicyTests
{
    [Fact]
    public void Constants_ExposeApprovedMinimums()
    {
        Assert.Equal(720, ResponsiveLayoutPolicy.MinimumWidth);
        Assert.Equal(760, ResponsiveLayoutPolicy.MediumMinimumWidth);
        Assert.Equal(1040, ResponsiveLayoutPolicy.WideMinimumWidth);
    }

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
            [StatusCardId.Mode, StatusCardId.Gpu, StatusCardId.Cpu, StatusCardId.Sleep],
            3);

        Assert.Equal(
            [(0, 0), (0, 1), (0, 2), (1, 0)],
            placements.Select(item => (item.Row, item.Column)));
    }

    [Theory]
    [InlineData(760)]
    [InlineData(1039)]
    public void Evaluate_ProfessionalMediumStacksLogAndUsesIconSecondaryActions(double width)
    {
        var state = ResponsiveLayoutPolicy.Evaluate(width, ExperienceMode.Professional);

        Assert.True(state.StackMainContent);
        Assert.False(state.ModePanelUsesContentHeight);
        Assert.Equal(220, state.ProfessionalLogMinimumHeight);
        Assert.Equal(ToolbarDisplay.IconOnly, state.Toolbar[ToolbarAction.Auto]);
        Assert.Equal(ToolbarDisplay.IconOnly, state.Toolbar[ToolbarAction.Live]);
        Assert.Equal(ToolbarDisplay.IconOnly, state.Toolbar[ToolbarAction.Features]);
        Assert.Equal(ToolbarDisplay.IconOnly, state.Toolbar[ToolbarAction.Insights]);
    }

    [Fact]
    public void Evaluate_ProfessionalNarrowStacksLogAndMovesSecondaryActionsToOverflow()
    {
        var state = ResponsiveLayoutPolicy.Evaluate(720, ExperienceMode.Professional);

        Assert.True(state.StackMainContent);
        Assert.Equal(180, state.ProfessionalLogMinimumHeight);
        Assert.Equal(ToolbarDisplay.Overflow, state.Toolbar[ToolbarAction.Auto]);
        Assert.Equal(ToolbarDisplay.Overflow, state.Toolbar[ToolbarAction.Live]);
        Assert.Equal(ToolbarDisplay.Overflow, state.Toolbar[ToolbarAction.Features]);
        Assert.Equal(ToolbarDisplay.Overflow, state.Toolbar[ToolbarAction.Insights]);
        Assert.Equal(ToolbarDisplay.IconOnly, state.Toolbar[ToolbarAction.Refresh]);
    }

    [Fact]
    public void Evaluate_ProfessionalWideKeepsSideBySideContentAndFullToolbar()
    {
        var state = ResponsiveLayoutPolicy.Evaluate(1040, ExperienceMode.Professional);

        Assert.False(state.StackMainContent);
        Assert.False(state.ModePanelUsesContentHeight);
        Assert.Equal(220, state.ProfessionalLogMinimumHeight);
        Assert.All(state.Toolbar.Values, display => Assert.Equal(ToolbarDisplay.Full, display));
    }

    [Theory]
    [InlineData(720)]
    [InlineData(760)]
    [InlineData(1040)]
    public void Evaluate_SimpleModeUsesContentHeightAndHidesProfessionalToolbar(double width)
    {
        var state = ResponsiveLayoutPolicy.Evaluate(width, ExperienceMode.Simple);

        Assert.False(state.StackMainContent);
        Assert.True(state.ModePanelUsesContentHeight);
        Assert.Equal(ToolbarDisplay.Hidden, state.Toolbar[ToolbarAction.Auto]);
        Assert.Equal(ToolbarDisplay.Hidden, state.Toolbar[ToolbarAction.Live]);
        Assert.Equal(ToolbarDisplay.Hidden, state.Toolbar[ToolbarAction.Features]);
        Assert.Equal(ToolbarDisplay.Hidden, state.Toolbar[ToolbarAction.Insights]);
    }

    [Theory]
    [InlineData(720, (int)ToolbarDisplay.IconOnly)]
    [InlineData(760, (int)ToolbarDisplay.IconOnly)]
    [InlineData(1040, (int)ToolbarDisplay.Full)]
    public void Evaluate_EveryTierKeepsCoreNavigationVisible(
        double width,
        int expectedDisplayValue)
    {
        var state = ResponsiveLayoutPolicy.Evaluate(width, ExperienceMode.Simple);
        var expectedDisplay = (ToolbarDisplay)expectedDisplayValue;

        Assert.Equal(expectedDisplay, state.Toolbar[ToolbarAction.Experience]);
        Assert.Equal(expectedDisplay, state.Toolbar[ToolbarAction.Recovery]);
        Assert.Equal(expectedDisplay, state.Toolbar[ToolbarAction.Language]);
    }

    [Theory]
    [InlineData(719, true)]
    [InlineData(759, true)]
    [InlineData(760, false)]
    public void ShouldStackAuxiliaryContent_UsesMediumBreakpoint(double width, bool expected)
    {
        Assert.Equal(expected, ResponsiveLayoutPolicy.ShouldStackAuxiliaryContent(width));
    }

    [Fact]
    public void ReflowStatusCards_RejectsInvalidColumnCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ResponsiveLayoutPolicy.ReflowStatusCards([StatusCardId.Mode], 0));
    }
}
