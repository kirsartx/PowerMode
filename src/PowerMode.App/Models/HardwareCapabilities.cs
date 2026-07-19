namespace PowerModeWinUI;

public enum CapabilitySupport
{
    Unknown,
    Supported,
    Unsupported
}

public sealed record HardwareCapabilities(
    CapabilitySupport Battery,
    CapabilitySupport BrightnessControl,
    CapabilitySupport NvidiaGpu,
    CapabilitySupport NvidiaSmi,
    CapabilitySupport WifiControl,
    CapabilitySupport TemperatureMonitoring,
    CapabilitySupport Notifications,
    CapabilitySupport GlobalHotkeys)
{
    public static HardwareCapabilities Unknown { get; } = new(
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown);
}
