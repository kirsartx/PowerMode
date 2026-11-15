using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ResponsiveLayoutPolicyTests
{
    [Fact]
    public void Constants_ExposeAuxiliaryBreakpoint()
    {
        Assert.Equal(760, ResponsiveLayoutPolicy.MediumMinimumWidth);
    }

    [Theory]
    [InlineData(719, true)]
    [InlineData(759, true)]
    [InlineData(760, false)]
    public void ShouldStackAuxiliaryContent_UsesMediumBreakpoint(double width, bool expected)
    {
        Assert.Equal(expected, ResponsiveLayoutPolicy.ShouldStackAuxiliaryContent(width));
    }
}
