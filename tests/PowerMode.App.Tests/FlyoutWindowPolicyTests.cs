using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class FlyoutWindowPolicyTests
{
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1032);

    [Fact]
    public void Constants_MatchApprovedFlyoutGeometry()
    {
        Assert.Equal(360, FlyoutWindowPolicy.ContentWidth);
        Assert.Equal(12, FlyoutWindowPolicy.EdgeMargin);
        Assert.Equal(200, FlyoutWindowPolicy.MinimumContentHeight);
    }

    [Theory]
    [InlineData(360, 1.0, 360)]
    [InlineData(360, 1.5, 540)]
    [InlineData(12, 1.25, 15)]
    [InlineData(10, 1.25, 13)]
    public void ToPixels_RoundsAwayFromZero(double logical, double scale, int expected)
    {
        Assert.Equal(expected, FlyoutWindowPolicy.ToPixels(logical, scale));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void ToPixels_InvalidScaleFallsBackToOne(double scale)
    {
        Assert.Equal(360, FlyoutWindowPolicy.ToPixels(360, scale));
    }

    [Theory]
    [InlineData(480, 1.0, 480)]
    [InlineData(480, 1.5, 720)]
    [InlineData(50, 1.0, 200)]
    [InlineData(5000, 1.0, 1008)]
    [InlineData(5000, 2.0, 984)]
    public void ClampHeight_KeepsBetweenMinimumAndWorkArea(
        double logicalHeight,
        double scale,
        int expected)
    {
        Assert.Equal(
            expected,
            FlyoutWindowPolicy.ClampHeight(logicalHeight, scale, WorkArea));
    }

    [Fact]
    public void ClampHeight_TinyWorkAreaNeverReturnsLessThanOnePixel()
    {
        Assert.Equal(
            1,
            FlyoutWindowPolicy.ClampHeight(400, 1.0, new PixelRect(0, 0, 300, 10)));
    }

    [Theory]
    [InlineData(1.0, 1548, 540)]
    [InlineData(1.5, 1362, 294)]
    public void AnchorBottomRight_LeavesEdgeMarginInsideWorkArea(
        double scale,
        int expectedX,
        int expectedY)
    {
        var width = FlyoutWindowPolicy.ToPixels(FlyoutWindowPolicy.ContentWidth, scale);
        var height = FlyoutWindowPolicy.ToPixels(480, scale);

        var bounds = FlyoutWindowPolicy.AnchorBottomRight(WorkArea, width, height, scale);

        Assert.Equal(new PixelRect(expectedX, expectedY, width, height), bounds);
    }

    [Fact]
    public void AnchorBottomRight_RespectsWorkAreaOffsetOnSecondaryMonitor()
    {
        var area = new PixelRect(-1280, 200, 1280, 984);

        var bounds = FlyoutWindowPolicy.AnchorBottomRight(area, 360, 400, 1.0);

        Assert.Equal(new PixelRect(-372, 772, 360, 400), bounds);
    }

    [Fact]
    public void KeepInside_LeavesVisiblePositionUntouched()
    {
        Assert.Equal(
            new PixelRect(100, 120, 360, 500),
            FlyoutWindowPolicy.KeepInside(WorkArea, 100, 120, 360, 500));
    }

    [Theory]
    [InlineData(1800, 900, 1560, 532)]
    [InlineData(-500, -40, 0, 0)]
    public void KeepInside_ClampsOffscreenPosition(
        int x,
        int y,
        int expectedX,
        int expectedY)
    {
        Assert.Equal(
            new PixelRect(expectedX, expectedY, 360, 500),
            FlyoutWindowPolicy.KeepInside(WorkArea, x, y, 360, 500));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void ShouldAutoHide_OnlyHidesUnpinnedFlyoutWhenFocusLeavesApp(
        bool isPinned,
        bool foregroundBelongsToApp,
        bool expected)
    {
        Assert.Equal(
            expected,
            FlyoutWindowPolicy.ShouldAutoHide(isPinned, foregroundBelongsToApp));
    }

    [Fact]
    public void TrayClickFilter_SingleClickToggles()
    {
        var filter = new TrayClickFilter();

        Assert.Equal(TrayClickAction.Toggle, filter.OnLeftButtonUp(1_000));
        Assert.Equal(TrayClickAction.Toggle, filter.OnLeftButtonUp(3_000));
    }

    [Fact]
    public void TrayClickFilter_DoubleClickShowsAndSwallowsTrailingButtonUp()
    {
        var filter = new TrayClickFilter();

        Assert.Equal(TrayClickAction.Toggle, filter.OnLeftButtonUp(1_000));
        Assert.Equal(TrayClickAction.Show, filter.OnDoubleClick(1_150));
        Assert.Equal(TrayClickAction.None, filter.OnLeftButtonUp(1_200));
        Assert.Equal(TrayClickAction.Toggle, filter.OnLeftButtonUp(3_000));
    }

    [Theory]
    [InlineData(1_399, (int)TrayClickAction.None)]
    [InlineData(1_400, (int)TrayClickAction.Toggle)]
    public void TrayClickFilter_IgnoresClickThatCausedAutoHide(
        long clickTime,
        int expected)
    {
        var filter = new TrayClickFilter();
        filter.NotifyAutoHidden(1_000);

        Assert.Equal((TrayClickAction)expected, filter.OnLeftButtonUp(clickTime));
    }
}
