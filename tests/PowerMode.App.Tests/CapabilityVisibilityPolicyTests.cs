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

    [Theory]
    [InlineData(ExperienceMode.Simple, true)]
    [InlineData(ExperienceMode.Simple, false)]
    [InlineData(ExperienceMode.Professional, true)]
    [InlineData(ExperienceMode.Professional, false)]
    public void Evaluate_EveryDisabledFeature_HasNonEmptyReason(
        ExperienceMode mode,
        bool isChinese)
    {
        foreach (var capabilities in DisabledReasonCapabilityFixtures())
        {
            var result = CapabilityVisibilityPolicy.Evaluate(mode, capabilities, isChinese);
            foreach (var (feature, presentation) in result)
            {
                if (presentation.IsEnabled)
                    continue;

                Assert.False(
                    string.IsNullOrWhiteSpace(presentation.Reason),
                    $"{mode}/{feature}/zh={isChinese} is disabled without a non-empty reason.");
            }
        }
    }

    [Fact]
    public void Evaluate_EnglishDisabledFeatures_UseEnglishReasons()
    {
        var capabilities = HardwareCapabilities.Unknown with
        {
            Battery = CapabilitySupport.Unsupported,
            BrightnessControl = CapabilitySupport.Unsupported,
            NvidiaSmi = CapabilitySupport.Unsupported,
            WifiControl = CapabilitySupport.Unsupported,
            TemperatureMonitoring = CapabilitySupport.Unsupported,
            Notifications = CapabilitySupport.Unsupported,
            GlobalHotkeys = CapabilitySupport.Unsupported
        };

        var result = CapabilityVisibilityPolicy.Evaluate(
            ExperienceMode.Professional,
            capabilities,
            isChinese: false);

        foreach (var feature in new[]
                 {
                     CapabilityFeature.BatterySettings,
                     CapabilityFeature.Brightness,
                     CapabilityFeature.GpuTelemetry,
                     CapabilityFeature.WifiControl,
                     CapabilityFeature.TemperatureProtection,
                     CapabilityFeature.Notifications,
                     CapabilityFeature.GlobalHotkeys
                 })
        {
            var presentation = result[feature];
            Assert.False(presentation.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(presentation.Reason));
            Assert.DoesNotContain("未", presentation.Reason);
            Assert.DoesNotContain("不支持", presentation.Reason);
            Assert.DoesNotContain("不可用", presentation.Reason);
            Assert.DoesNotContain("尚未", presentation.Reason);
        }

        var unknown = CapabilityVisibilityPolicy.Evaluate(
            ExperienceMode.Professional,
            HardwareCapabilities.Unknown,
            isChinese: false);
        Assert.False(unknown[CapabilityFeature.WifiControl].IsEnabled);
        Assert.Contains("detection", unknown[CapabilityFeature.WifiControl].Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<HardwareCapabilities> DisabledReasonCapabilityFixtures()
    {
        yield return HardwareCapabilities.Unknown;
        yield return HardwareCapabilities.Unknown with
        {
            Battery = CapabilitySupport.Unsupported,
            BrightnessControl = CapabilitySupport.Unsupported,
            NvidiaSmi = CapabilitySupport.Unsupported,
            WifiControl = CapabilitySupport.Unsupported,
            TemperatureMonitoring = CapabilitySupport.Unsupported,
            Notifications = CapabilitySupport.Unsupported,
            GlobalHotkeys = CapabilitySupport.Unsupported
        };
        yield return HardwareCapabilities.Unknown with
        {
            Battery = CapabilitySupport.Supported,
            BrightnessControl = CapabilitySupport.Supported,
            NvidiaSmi = CapabilitySupport.Supported,
            WifiControl = CapabilitySupport.Supported,
            TemperatureMonitoring = CapabilitySupport.Supported,
            Notifications = CapabilitySupport.Supported,
            GlobalHotkeys = CapabilitySupport.Supported
        };
        yield return HardwareCapabilities.Unknown with
        {
            Battery = CapabilitySupport.Supported,
            BrightnessControl = CapabilitySupport.Unsupported,
            NvidiaSmi = CapabilitySupport.Unknown,
            WifiControl = CapabilitySupport.Unsupported,
            TemperatureMonitoring = CapabilitySupport.Supported,
            Notifications = CapabilitySupport.Unknown,
            GlobalHotkeys = CapabilitySupport.Unsupported
        };
    }
}
