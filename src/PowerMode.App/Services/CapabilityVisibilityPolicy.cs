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
        HardwareCapabilities capabilities)
    {
        var professional = mode == ExperienceMode.Professional;
        return new Dictionary<CapabilityFeature, FeaturePresentation>
        {
            [CapabilityFeature.CoreModes] = new(true, true, string.Empty),
            [CapabilityFeature.ProfessionalSurface] =
                new(professional, true, string.Empty),
            [CapabilityFeature.BatterySettings] = Present(professional, capabilities.Battery, "未检测到电池"),
            [CapabilityFeature.Brightness] = Present(
                professional || capabilities.BrightnessControl == CapabilitySupport.Supported,
                capabilities.BrightnessControl,
                "设备不支持内部屏幕亮度控制"),
            [CapabilityFeature.GpuTelemetry] = Present(professional, capabilities.NvidiaSmi, "未检测到可用的 NVIDIA 遥测"),
            [CapabilityFeature.WifiControl] = Present(professional, capabilities.WifiControl, "未检测到可控制的 WiFi 适配器"),
            [CapabilityFeature.TemperatureProtection] = Present(professional, capabilities.TemperatureMonitoring, "未检测到受支持的温度传感器"),
            [CapabilityFeature.Notifications] = Present(professional, capabilities.Notifications, "系统通知不可用"),
            [CapabilityFeature.GlobalHotkeys] = Present(professional, capabilities.GlobalHotkeys, "全局快捷键注册不可用")
        };
    }

    private static FeaturePresentation Present(
        bool visibleWhenSupported,
        CapabilitySupport support,
        string unsupportedReason)
    {
        if (support == CapabilitySupport.Supported)
            return new(visibleWhenSupported, true, string.Empty);

        return new(
            visibleWhenSupported,
            false,
            support == CapabilitySupport.Unknown ? "尚未完成能力检测" : unsupportedReason);
    }
}
