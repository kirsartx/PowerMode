using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class AutomationCurrentModeTests
{
    [Theory]
    [InlineData("high", "saver", "high")]
    [InlineData(" Remote ", "balanced", "remote")]
    [InlineData("unknown", "saver", "saver")]
    [InlineData("custom:Quiet", "balanced", "balanced")]
    [InlineData(null, "high", "high")]
    [InlineData(null, "nope", "balanced")]
    [InlineData("", null, "balanced")]
    public void ResolveCurrentMode_PrefersObservedStandardPreset(
        string? observed,
        string? remembered,
        string expected)
    {
        Assert.Equal(expected, AutomationEngine.ResolveCurrentMode(observed, remembered));
    }
}
