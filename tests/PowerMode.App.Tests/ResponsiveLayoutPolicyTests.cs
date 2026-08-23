using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ResponsiveLayoutPolicyTests
{
    [Fact]
    public void Constants_ExposeApprovedMinimums()
    {
        Assert.Equal(720, ResponsiveLayoutPolicy.MinimumWidth);
        Assert.Equal(560, ResponsiveLayoutPolicy.MinimumHeight);
        Assert.Equal(760, ResponsiveLayoutPolicy.MediumMinimumWidth);
        Assert.Equal(1040, ResponsiveLayoutPolicy.WideMinimumWidth);
        Assert.Equal(1120, ResponsiveLayoutPolicy.DefaultWidth);
        Assert.Equal(760, ResponsiveLayoutPolicy.DefaultHeight);
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
            [StatusCardId.Mode, StatusCardId.Power, StatusCardId.Cpu, StatusCardId.Sleep],
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

    [Theory]
    [InlineData(720, 560, (int)LayoutTier.Narrow, 6)]
    [InlineData(760, 560, (int)LayoutTier.Medium, 3)]
    [InlineData(760, 760, (int)LayoutTier.Medium, 3)]
    public void Project_ConstrainedNarrowAndMediumKeepChromeBoundedAndContentScrollable(
        double width,
        double height,
        int expectedTierValue,
        int expectedStatusRows)
    {
        var projection = ResponsiveLayoutPolicy.Project(
            width,
            height,
            ExperienceMode.Professional,
            Enum.GetValues<StatusCardId>());

        Assert.Equal((LayoutTier)expectedTierValue, projection.Layout.Tier);
        Assert.Equal(
            [GridLengthProjection.Auto, GridLengthProjection.Star, GridLengthProjection.Auto],
            projection.RootRows);
        Assert.True(projection.EnableVerticalContentScroll);
        Assert.Equal(
            [GridLengthProjection.Auto, GridLengthProjection.Auto],
            projection.ContentRows);
        Assert.Equal(expectedStatusRows, projection.StatusCardRows);
        Assert.Equal(
            [GridLengthProjection.Auto, GridLengthProjection.Auto],
            projection.MainRows);
        Assert.True(projection.Layout.ProfessionalLogMinimumHeight is 180 or 220);
        Assert.Equal(6, projection.StatusCards.Count);
    }

    [Fact]
    public void Project_SimpleNarrowPreservesContentHeightInsideScrollableViewport()
    {
        var projection = ResponsiveLayoutPolicy.Project(
            720,
            560,
            ExperienceMode.Simple,
            Enum.GetValues<StatusCardId>());

        Assert.True(projection.EnableVerticalContentScroll);
        Assert.True(projection.Layout.ModePanelUsesContentHeight);
        Assert.Equal(
            [GridLengthProjection.Auto, GridLengthProjection.Fixed(0)],
            projection.MainRows);
    }

    [Theory]
    [InlineData(1040)]
    [InlineData(1120)]
    public void Project_ProfessionalWideUsesFixedPanelAndOneColumnModeButtons(double width)
    {
        var projection = ResponsiveLayoutPolicy.Project(
            width,
            760,
            ExperienceMode.Professional,
            Enum.GetValues<StatusCardId>());

        Assert.Equal(LayoutTier.Wide, projection.Layout.Tier);
        Assert.False(projection.EnableVerticalContentScroll);
        Assert.Equal(
            [GridLengthProjection.Fixed(390), GridLengthProjection.Star],
            projection.MainColumns);
        Assert.Equal(1, projection.ModeButtonColumns);
        Assert.Equal(
            [(0, 0), (1, 0), (2, 0), (3, 0)],
            projection.ModeButtons.Select(item => (item.Row, item.Column)));
    }

    [Fact]
    public void Project_ProjectsToolbarOverflowAndGaplessVisibleCards()
    {
        var projection = ResponsiveLayoutPolicy.Project(
            720,
            560,
            ExperienceMode.Professional,
            [StatusCardId.Mode, StatusCardId.Power, StatusCardId.Cpu, StatusCardId.Sleep]);

        Assert.True(projection.ShowToolbarOverflow);
        Assert.Equal(ToolbarDisplay.Overflow, projection.Layout.Toolbar[ToolbarAction.Auto]);
        Assert.Equal(ToolbarDisplay.IconOnly, projection.Layout.Toolbar[ToolbarAction.Recovery]);
        Assert.Equal(
            [(0, 0), (1, 0), (2, 0), (3, 0)],
            projection.StatusCards.Select(item => (item.Row, item.Column)));
    }
}
