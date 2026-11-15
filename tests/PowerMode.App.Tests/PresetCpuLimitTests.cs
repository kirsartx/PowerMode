using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class PresetCpuLimitTests
{
    [Theory]
    [InlineData("Remote", null, 32)]
    [InlineData("Remote", 15, 32)]
    [InlineData("Remote", 20, 20)]
    [InlineData("Remote", 28, 28)]
    [InlineData("Remote", 45, 45)]
    [InlineData("Remote", 80, 50)]
    [InlineData("Saver", null, 30)]
    [InlineData("Saver", 10, 30)]
    [InlineData("Saver", 45, 45)]
    [InlineData("Saver", 51, 50)]
    [InlineData("Balanced", 40, 100)]
    [InlineData("High", null, 100)]
    public void Normalize_MatchesEngineLimitCpuMax(string presetName, int? requested, int expected)
    {
        var preset = Enum.Parse<PowerModePreset>(presetName);
        Assert.Equal(expected, PresetCpuLimit.Normalize(preset, requested));
    }
}
