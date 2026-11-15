using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class PresetCpuLimitTests
{
    [Theory]
    [InlineData(PowerModePreset.Remote, null, 32)]
    [InlineData(PowerModePreset.Remote, 15, 32)]
    [InlineData(PowerModePreset.Remote, 20, 20)]
    [InlineData(PowerModePreset.Remote, 28, 28)]
    [InlineData(PowerModePreset.Remote, 45, 45)]
    [InlineData(PowerModePreset.Remote, 80, 50)]
    [InlineData(PowerModePreset.Saver, null, 30)]
    [InlineData(PowerModePreset.Saver, 10, 30)]
    [InlineData(PowerModePreset.Saver, 45, 45)]
    [InlineData(PowerModePreset.Saver, 51, 50)]
    [InlineData(PowerModePreset.Balanced, 40, 100)]
    [InlineData(PowerModePreset.High, null, 100)]
    public void Normalize_MatchesEngineLimitCpuMax(PowerModePreset preset, int? requested, int expected)
    {
        Assert.Equal(expected, PresetCpuLimit.Normalize(preset, requested));
    }
}
