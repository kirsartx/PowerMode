using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class CapabilityVisibilityPolicyTests
{
    [Fact]
    public void Evaluate_SimpleMode_HidesUnsupportedProfessionalFeature()
    {
        var capabilities = HardwareCapabilities.Unknown with
        {
            WifiControl = CapabilitySupport.Unsupported
        };

        var result = CapabilityVisibilityPolicy.Evaluate(ExperienceMode.Simple, capabilities);

        Assert.False(result[CapabilityFeature.WifiControl].IsVisible);
    }

    [Fact]
    public void Evaluate_SimpleMode_HidesUnsupportedBrightness()
    {
        var capabilities = HardwareCapabilities.Unknown with
        {
            BrightnessControl = CapabilitySupport.Unsupported
        };

        var result = CapabilityVisibilityPolicy.Evaluate(ExperienceMode.Simple, capabilities);

        Assert.False(result[CapabilityFeature.Brightness].IsVisible);
    }

    [Fact]
    public void Evaluate_ProfessionalMode_DisablesUnsupportedFeatureWithReason()
    {
        var capabilities = HardwareCapabilities.Unknown with
        {
            TemperatureMonitoring = CapabilitySupport.Unsupported
        };

        var result = CapabilityVisibilityPolicy.Evaluate(ExperienceMode.Professional, capabilities);

        var temperature = result[CapabilityFeature.TemperatureProtection];
        Assert.True(temperature.IsVisible);
        Assert.False(temperature.IsEnabled);
        Assert.NotEmpty(temperature.Reason);
    }

    [Fact]
    public void Evaluate_CoreModes_AreAlwaysVisibleAndEnabled()
    {
        var result = CapabilityVisibilityPolicy.Evaluate(
            ExperienceMode.Simple,
            HardwareCapabilities.Unknown);

        Assert.True(result[CapabilityFeature.CoreModes].IsVisible);
        Assert.True(result[CapabilityFeature.CoreModes].IsEnabled);
    }

    [Fact]
    public void Evaluate_SimpleMode_HidesProfessionalSurface()
    {
        var result = CapabilityVisibilityPolicy.Evaluate(
            ExperienceMode.Simple,
            HardwareCapabilities.Unknown);

        Assert.False(result[CapabilityFeature.ProfessionalSurface].IsVisible);
    }
}
