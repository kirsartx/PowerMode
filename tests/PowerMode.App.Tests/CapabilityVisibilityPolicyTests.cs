using System.Reflection;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class CapabilityVisibilityPolicyTests
{
    [Fact]
    public void Evaluate_RequiresLanguageArgumentWithoutDefault()
    {
        var parameter = typeof(CapabilityVisibilityPolicy)
            .GetMethod(
                nameof(CapabilityVisibilityPolicy.Evaluate),
                BindingFlags.Public | BindingFlags.Static)!
            .GetParameters()[2];

        Assert.Equal("isChinese", parameter.Name);
        Assert.Equal(typeof(bool), parameter.ParameterType);
        Assert.False(parameter.HasDefaultValue);
    }

    [Fact]
    public void Evaluate_SimpleMode_HidesUnsupportedProfessionalFeature()
    {
        var capabilities = HardwareCapabilities.Unknown with
        {
            WifiControl = CapabilitySupport.Unsupported
        };

        var result = CapabilityVisibilityPolicy.Evaluate(
            ExperienceMode.Simple,
            capabilities,
            isChinese: true);

        Assert.False(result[CapabilityFeature.WifiControl].IsVisible);
    }

    [Fact]
    public void Evaluate_SimpleMode_HidesUnsupportedBrightness()
    {
        var capabilities = HardwareCapabilities.Unknown with
        {
            BrightnessControl = CapabilitySupport.Unsupported
        };

        var result = CapabilityVisibilityPolicy.Evaluate(
            ExperienceMode.Simple,
            capabilities,
            isChinese: true);

        Assert.False(result[CapabilityFeature.Brightness].IsVisible);
    }

    [Fact]
    public void Evaluate_ProfessionalMode_DisablesUnsupportedFeatureWithReason()
    {
        var capabilities = HardwareCapabilities.Unknown with
        {
            TemperatureMonitoring = CapabilitySupport.Unsupported
        };

        var result = CapabilityVisibilityPolicy.Evaluate(
            ExperienceMode.Professional,
            capabilities,
            isChinese: true);

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
            HardwareCapabilities.Unknown,
            isChinese: true);

        Assert.True(result[CapabilityFeature.CoreModes].IsVisible);
        Assert.True(result[CapabilityFeature.CoreModes].IsEnabled);
    }

    [Fact]
    public void Evaluate_SimpleMode_HidesProfessionalSurface()
    {
        var result = CapabilityVisibilityPolicy.Evaluate(
            ExperienceMode.Simple,
            HardwareCapabilities.Unknown,
            isChinese: true);

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
    public void Evaluate_EnglishDisabledFeatures_UseExactEnglishReasons()
    {
        var unsupported = HardwareCapabilities.Unknown with
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
            unsupported,
            isChinese: false);

        Assert.Equal("No battery detected", result[CapabilityFeature.BatterySettings].Reason);
        Assert.Equal(
            "This device does not support internal display brightness control",
            result[CapabilityFeature.Brightness].Reason);
        Assert.Equal(
            "No usable NVIDIA telemetry was detected",
            result[CapabilityFeature.GpuTelemetry].Reason);
        Assert.Equal(
            "No controllable Wi-Fi adapter was detected",
            result[CapabilityFeature.WifiControl].Reason);
        Assert.Equal(
            "No supported temperature sensor was detected",
            result[CapabilityFeature.TemperatureProtection].Reason);
        Assert.Equal(
            "System notifications are unavailable",
            result[CapabilityFeature.Notifications].Reason);
        Assert.Equal(
            "Global hotkey registration is unavailable",
            result[CapabilityFeature.GlobalHotkeys].Reason);

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
            Assert.False(result[feature].IsEnabled);
        }

        var unknown = CapabilityVisibilityPolicy.Evaluate(
            ExperienceMode.Professional,
            HardwareCapabilities.Unknown,
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
            Assert.False(unknown[feature].IsEnabled);
            Assert.Equal(
                "Capability detection has not finished",
                unknown[feature].Reason);
        }
    }

    [Fact]
    public void Evaluate_ChineseDisabledFeatures_UseExactChineseReasons()
    {
        var unsupported = HardwareCapabilities.Unknown with
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
            unsupported,
            isChinese: true);

        Assert.Equal("未检测到电池", result[CapabilityFeature.BatterySettings].Reason);
        Assert.Equal("设备不支持内部屏幕亮度控制", result[CapabilityFeature.Brightness].Reason);
        Assert.Equal("未检测到可用的 NVIDIA 遥测", result[CapabilityFeature.GpuTelemetry].Reason);
        Assert.Equal("未检测到可控制的 WiFi 适配器", result[CapabilityFeature.WifiControl].Reason);
        Assert.Equal("未检测到受支持的温度传感器", result[CapabilityFeature.TemperatureProtection].Reason);
        Assert.Equal("系统通知不可用", result[CapabilityFeature.Notifications].Reason);
        Assert.Equal("全局快捷键注册不可用", result[CapabilityFeature.GlobalHotkeys].Reason);

        var unknown = CapabilityVisibilityPolicy.Evaluate(
            ExperienceMode.Professional,
            HardwareCapabilities.Unknown,
            isChinese: true);

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
            Assert.Equal("尚未完成能力检测", unknown[feature].Reason);
        }
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
