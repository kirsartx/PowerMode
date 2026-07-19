namespace PowerModeWinUI;

public enum CapabilityFeature
{
    CoreModes,
    ProfessionalSurface,
    BatterySettings,
    Brightness,
    GpuTelemetry,
    WifiControl,
    TemperatureProtection,
    Notifications,
    GlobalHotkeys
}

public sealed record FeaturePresentation(bool IsVisible, bool IsEnabled, string Reason);

public static class CapabilityVisibilityPolicy
{
    public static IReadOnlyDictionary<CapabilityFeature, FeaturePresentation> Evaluate(
        ExperienceMode mode,
        HardwareCapabilities capabilities,
        bool isChinese = true)
    {
        var professional = mode == ExperienceMode.Professional;
        return new Dictionary<CapabilityFeature, FeaturePresentation>
        {
            [CapabilityFeature.CoreModes] = new(true, true, string.Empty),
            [CapabilityFeature.ProfessionalSurface] =
                new(professional, true, string.Empty),
            [CapabilityFeature.BatterySettings] = Present(
                professional,
                capabilities.Battery,
                isChinese ? "未检测到电池" : "No battery detected",
                isChinese),
            [CapabilityFeature.Brightness] = Present(
                professional || capabilities.BrightnessControl == CapabilitySupport.Supported,
                capabilities.BrightnessControl,
                isChinese
                    ? "设备不支持内部屏幕亮度控制"
                    : "This device does not support internal display brightness control",
                isChinese),
            [CapabilityFeature.GpuTelemetry] = Present(
                professional,
                capabilities.NvidiaSmi,
                isChinese
                    ? "未检测到可用的 NVIDIA 遥测"
                    : "No usable NVIDIA telemetry was detected",
                isChinese),
            [CapabilityFeature.WifiControl] = Present(
                professional,
                capabilities.WifiControl,
                isChinese
                    ? "未检测到可控制的 WiFi 适配器"
                    : "No controllable Wi-Fi adapter was detected",
                isChinese),
            [CapabilityFeature.TemperatureProtection] = Present(
                professional,
                capabilities.TemperatureMonitoring,
                isChinese
                    ? "未检测到受支持的温度传感器"
                    : "No supported temperature sensor was detected",
                isChinese),
            [CapabilityFeature.Notifications] = Present(
                professional,
                capabilities.Notifications,
                isChinese ? "系统通知不可用" : "System notifications are unavailable",
                isChinese),
            [CapabilityFeature.GlobalHotkeys] = Present(
                professional,
                capabilities.GlobalHotkeys,
                isChinese
                    ? "全局快捷键注册不可用"
                    : "Global hotkey registration is unavailable",
                isChinese)
        };
    }

    private static FeaturePresentation Present(
        bool visibleWhenSupported,
        CapabilitySupport support,
        string unsupportedReason,
        bool isChinese)
    {
        if (support == CapabilitySupport.Supported)
            return new(visibleWhenSupported, true, string.Empty);

        return new(
            visibleWhenSupported,
            false,
            support == CapabilitySupport.Unknown
                ? (isChinese ? "尚未完成能力检测" : "Capability detection has not finished")
                : unsupportedReason);
    }
}
