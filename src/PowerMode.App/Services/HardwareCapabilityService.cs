using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PowerModeWinUI;

public interface IHardwareCapabilityProbe
{
    Task<CapabilitySupport> HasBatteryAsync(CancellationToken token);
    Task<CapabilitySupport> SupportsBrightnessAsync(CancellationToken token);
    Task<CapabilitySupport> HasNvidiaGpuAsync(CancellationToken token);
    Task<CapabilitySupport> HasNvidiaSmiAsync(CancellationToken token);
    Task<CapabilitySupport> HasWifiControlAsync(CancellationToken token);
    Task<CapabilitySupport> HasTemperatureAsync(CancellationToken token);
    Task<CapabilitySupport> SupportsNotificationsAsync(CancellationToken token);
    Task<CapabilitySupport> SupportsGlobalHotkeysAsync(CancellationToken token);
}

public sealed class HardwareCapabilityService(IHardwareCapabilityProbe probe) : IDisposable
{
    private readonly object _cacheSync = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task<HardwareCapabilities>? _detection;
    private bool _disposed;

    public Task<HardwareCapabilities> DetectAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");

        lock (_cacheSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var detection = _detection ??= DetectCoreAsync(timeout, _lifetimeCancellation.Token);
            return cancellationToken.CanBeCanceled
                ? detection.WaitAsync(cancellationToken)
                : detection;
        }
    }

    private async Task<HardwareCapabilities> DetectCoreAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var battery = SafeProbeAsync(probe.HasBatteryAsync, timeout, cancellationToken);
        var brightness = SafeProbeAsync(probe.SupportsBrightnessAsync, timeout, cancellationToken);
        var nvidiaGpu = SafeProbeAsync(probe.HasNvidiaGpuAsync, timeout, cancellationToken);
        var nvidiaSmi = SafeProbeAsync(probe.HasNvidiaSmiAsync, timeout, cancellationToken);
        var wifi = SafeProbeAsync(probe.HasWifiControlAsync, timeout, cancellationToken);
        var temperature = SafeProbeAsync(probe.HasTemperatureAsync, timeout, cancellationToken);
        var notifications = SafeProbeAsync(probe.SupportsNotificationsAsync, timeout, cancellationToken);
        var globalHotkeys = SafeProbeAsync(probe.SupportsGlobalHotkeysAsync, timeout, cancellationToken);

        await Task.WhenAll(
            battery,
            brightness,
            nvidiaGpu,
            nvidiaSmi,
            wifi,
            temperature,
            notifications,
            globalHotkeys).ConfigureAwait(false);

        return new HardwareCapabilities(
            await battery.ConfigureAwait(false),
            await brightness.ConfigureAwait(false),
            await nvidiaGpu.ConfigureAwait(false),
            await nvidiaSmi.ConfigureAwait(false),
            await wifi.ConfigureAwait(false),
            await temperature.ConfigureAwait(false),
            await notifications.ConfigureAwait(false),
            await globalHotkeys.ConfigureAwait(false));
    }

    private static async Task<CapabilitySupport> SafeProbeAsync(
        Func<CancellationToken, Task<CapabilitySupport>> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            var probeTask = Task.Run(() => probe(linked.Token), CancellationToken.None);
            try
            {
                return await probeTask.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = ObserveLateCompletionAsync(probeTask);
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CapabilitySupport.Unknown;
        }
        catch
        {
            return CapabilitySupport.Unknown;
        }
    }

    private static async Task ObserveLateCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The bounded caller has already degraded this late result to Unknown.
        }
    }

    public void Dispose()
    {
        lock (_cacheSync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetimeCancellation.Cancel();
        }
    }
}

internal sealed class CapabilityPresentationLifetime : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private bool _closed;

    public CancellationToken Token => _cancellation.Token;

    public bool IsClosing
    {
        get
        {
            lock (_sync)
                return _closed;
        }
    }

    public bool TryApply(Action action)
    {
        lock (_sync)
        {
            if (_closed)
                return false;
            try
            {
                action();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (_closed)
                return;
            _closed = true;
            _cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        Close();
        _cancellation.Dispose();
    }
}

public sealed class WindowsHardwareCapabilityProbe(
    Func<bool>? notificationsAvailable = null,
    Func<bool>? globalHotkeysAvailable = null) : IHardwareCapabilityProbe
{
    public Task<CapabilitySupport> HasBatteryAsync(CancellationToken token) =>
        Task.FromResult(MonitoringService.ProbeBatteryCapability());

    public Task<CapabilitySupport> SupportsBrightnessAsync(CancellationToken token) =>
        MonitoringService.ProbeBrightnessCapabilityAsync(token);

    public Task<CapabilitySupport> HasNvidiaGpuAsync(CancellationToken token) =>
        Task.FromResult(HasDisplayAdapter("NVIDIA"));

    public Task<CapabilitySupport> HasNvidiaSmiAsync(CancellationToken token) =>
        MonitoringService.ProbeNvidiaSmiCapabilityAsync(token);

    public Task<CapabilitySupport> HasWifiControlAsync(CancellationToken token)
    {
        var hasWifi = NetworkInterface.GetAllNetworkInterfaces()
            .Any(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        return Task.FromResult(hasWifi
            ? CapabilitySupport.Supported
            : CapabilitySupport.Unsupported);
    }

    public Task<CapabilitySupport> HasTemperatureAsync(CancellationToken token) =>
        MonitoringService.ProbeTemperatureCapabilityAsync(token);

    public Task<CapabilitySupport> SupportsNotificationsAsync(CancellationToken token) =>
        Task.FromResult(ToSupport(notificationsAvailable?.Invoke() ?? OperatingSystem.IsWindows()));

    public Task<CapabilitySupport> SupportsGlobalHotkeysAsync(CancellationToken token) =>
        Task.FromResult(ToSupport(globalHotkeysAvailable?.Invoke() ?? OperatingSystem.IsWindows()));

    private static CapabilitySupport HasDisplayAdapter(string vendor)
    {
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0))
                return CapabilitySupport.Unsupported;
            if (device.DeviceString.Contains(vendor, StringComparison.OrdinalIgnoreCase)
                || device.DeviceId.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                return CapabilitySupport.Supported;
        }
    }

    private static CapabilitySupport ToSupport(bool supported) =>
        supported ? CapabilitySupport.Supported : CapabilitySupport.Unsupported;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? device,
        uint deviceNumber,
        ref DisplayDevice displayDevice,
        uint flags);
}
