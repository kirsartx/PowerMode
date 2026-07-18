using System.Diagnostics;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class HardwareCapabilityServiceTests
{
    [Fact]
    public async Task DetectAsync_OneProbeThrows_ReturnsOtherResults()
    {
        var service = new HardwareCapabilityService(new FakeProbe
        {
            Battery = CapabilitySupport.Supported,
            WifiControl = CapabilitySupport.Supported,
            ThrowForWifi = true
        });

        var result = await service.DetectAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(CapabilitySupport.Supported, result.Battery);
        Assert.Equal(CapabilitySupport.Unknown, result.WifiControl);
    }

    [Fact]
    public async Task DetectAsync_SlowProbe_TimesOutAsUnknownWithoutDelayingFastResults()
    {
        var service = new HardwareCapabilityService(new FakeProbe
        {
            TemperatureMonitoring = CapabilitySupport.Supported,
            TemperatureDelay = TimeSpan.FromSeconds(2),
            IgnoreTemperatureCancellation = true,
            Notifications = CapabilitySupport.Supported
        });
        var stopwatch = Stopwatch.StartNew();

        var result = await service.DetectAsync(TimeSpan.FromMilliseconds(40));

        Assert.Equal(CapabilitySupport.Unknown, result.TemperatureMonitoring);
        Assert.Equal(CapabilitySupport.Supported, result.Notifications);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DetectAsync_StartsAllProbesConcurrently()
    {
        var probe = new FakeProbe
        {
            WaitForAllProbes = true,
            Battery = CapabilitySupport.Supported,
            BrightnessControl = CapabilitySupport.Supported,
            NvidiaGpu = CapabilitySupport.Supported,
            NvidiaSmi = CapabilitySupport.Supported,
            WifiControl = CapabilitySupport.Supported,
            TemperatureMonitoring = CapabilitySupport.Supported,
            Notifications = CapabilitySupport.Supported,
            GlobalHotkeys = CapabilitySupport.Supported
        };
        var service = new HardwareCapabilityService(probe);

        var result = await service.DetectAsync(TimeSpan.FromMilliseconds(100));

        Assert.DoesNotContain(CapabilitySupport.Unknown, new[]
        {
            result.Battery,
            result.BrightnessControl,
            result.NvidiaGpu,
            result.NvidiaSmi,
            result.WifiControl,
            result.TemperatureMonitoring,
            result.Notifications,
            result.GlobalHotkeys
        });
    }

    [Fact]
    public async Task DetectAsync_SecondCall_ReturnsCachedSnapshot()
    {
        var probe = new FakeProbe { Battery = CapabilitySupport.Supported };
        var service = new HardwareCapabilityService(probe);

        var first = await service.DetectAsync(TimeSpan.FromSeconds(1));
        var second = await service.DetectAsync(TimeSpan.FromSeconds(1));

        Assert.Same(first, second);
        Assert.Equal(8, probe.CallCount);
    }

    private sealed class FakeProbe : IHardwareCapabilityProbe
    {
        private readonly TaskCompletionSource _allProbesStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public CapabilitySupport Battery { get; init; } = CapabilitySupport.Unknown;
        public CapabilitySupport BrightnessControl { get; init; } = CapabilitySupport.Unknown;
        public CapabilitySupport NvidiaGpu { get; init; } = CapabilitySupport.Unknown;
        public CapabilitySupport NvidiaSmi { get; init; } = CapabilitySupport.Unknown;
        public CapabilitySupport WifiControl { get; init; } = CapabilitySupport.Unknown;
        public CapabilitySupport TemperatureMonitoring { get; init; } = CapabilitySupport.Unknown;
        public CapabilitySupport Notifications { get; init; } = CapabilitySupport.Unknown;
        public CapabilitySupport GlobalHotkeys { get; init; } = CapabilitySupport.Unknown;
        public bool ThrowForWifi { get; init; }
        public bool IgnoreTemperatureCancellation { get; init; }
        public bool WaitForAllProbes { get; init; }
        public TimeSpan TemperatureDelay { get; init; }
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CapabilitySupport> HasBatteryAsync(CancellationToken token) =>
            ProbeAsync(Battery, token);

        public Task<CapabilitySupport> SupportsBrightnessAsync(CancellationToken token) =>
            ProbeAsync(BrightnessControl, token);

        public Task<CapabilitySupport> HasNvidiaGpuAsync(CancellationToken token) =>
            ProbeAsync(NvidiaGpu, token);

        public Task<CapabilitySupport> HasNvidiaSmiAsync(CancellationToken token) =>
            ProbeAsync(NvidiaSmi, token);

        public async Task<CapabilitySupport> HasWifiControlAsync(CancellationToken token)
        {
            var result = await ProbeAsync(WifiControl, token);
            if (ThrowForWifi)
                throw new InvalidOperationException("WiFi probe failed.");
            return result;
        }

        public async Task<CapabilitySupport> HasTemperatureAsync(CancellationToken token)
        {
            var result = await ProbeAsync(TemperatureMonitoring, token);
            if (TemperatureDelay > TimeSpan.Zero)
                await Task.Delay(
                    TemperatureDelay,
                    IgnoreTemperatureCancellation ? CancellationToken.None : token);
            return result;
        }

        public Task<CapabilitySupport> SupportsNotificationsAsync(CancellationToken token) =>
            ProbeAsync(Notifications, token);

        public Task<CapabilitySupport> SupportsGlobalHotkeysAsync(CancellationToken token) =>
            ProbeAsync(GlobalHotkeys, token);

        private async Task<CapabilitySupport> ProbeAsync(
            CapabilitySupport result,
            CancellationToken token)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 8)
                _allProbesStarted.TrySetResult();
            if (WaitForAllProbes)
                await _allProbesStarted.Task.WaitAsync(token);
            return result;
        }
    }
}
