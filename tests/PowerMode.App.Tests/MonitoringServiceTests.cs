using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class MonitoringServiceTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SampleAsync_UnsupportedExpensiveProbes_NeverCallsRunner()
    {
        var runner = new CountingProbeRunner();
        await using var service = CreateService(runner);
        service.UpdateCapabilities(Capabilities(
            battery: CapabilitySupport.Unsupported,
            nvidiaGpu: CapabilitySupport.Supported,
            nvidiaSmi: CapabilitySupport.Unsupported,
            temperature: CapabilitySupport.Unsupported));

        await service.SampleAsync();

        Assert.Equal(0, runner.NvidiaCalls);
        Assert.Equal(0, runner.TemperatureCalls);
        Assert.Equal(0, runner.BatteryHealthCalls);
    }

    [Fact]
    public async Task SampleAsync_UnknownProbes_RetryOnlyAfterFiveMinutes()
    {
        var time = new ManualTimeProvider(Start);
        var runner = new CountingProbeRunner();
        await using var service = new MonitoringService(runner, time);
        service.UpdateCapabilities(Capabilities(
            battery: CapabilitySupport.Unknown,
            nvidiaGpu: CapabilitySupport.Unknown,
            nvidiaSmi: CapabilitySupport.Unknown,
            temperature: CapabilitySupport.Unknown));

        await service.SampleAsync();
        await service.SampleAsync();
        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await service.SampleAsync();

        Assert.Equal((1, 1, 1), runner.ExpensiveCalls);

        time.Advance(TimeSpan.FromSeconds(1));
        await service.SampleAsync();

        Assert.Equal((2, 2, 2), runner.ExpensiveCalls);
    }

    [Fact]
    public async Task SampleAsync_ConcurrentCallers_ShareOneInFlightSample()
    {
        var runner = new CountingProbeRunner { BlockNvidia = true };
        await using var service = CreateService(runner);
        service.UpdateCapabilities(Capabilities(
            battery: CapabilitySupport.Unsupported,
            nvidiaGpu: CapabilitySupport.Supported,
            nvidiaSmi: CapabilitySupport.Supported,
            temperature: CapabilitySupport.Unsupported));

        var first = service.SampleAsync();
        await runner.NvidiaStarted.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.SampleAsync();

        Assert.Equal(1, runner.NvidiaCalls);
        runner.ReleaseNvidia();
        var samples = await Task.WhenAll(first, second);

        Assert.Same(samples[0], samples[1]);
        Assert.Equal(1, runner.NvidiaCalls);
        Assert.Equal(1, service.History.Count);
    }

    [Fact]
    public async Task GetOrSampleAsync_RecentSample_ReusesSnapshot()
    {
        var runner = new CountingProbeRunner();
        await using var service = CreateService(runner);
        service.UpdateCapabilities(Capabilities(
            battery: CapabilitySupport.Unsupported,
            nvidiaGpu: CapabilitySupport.Supported,
            nvidiaSmi: CapabilitySupport.Supported,
            temperature: CapabilitySupport.Unsupported));

        var first = await service.GetOrSampleAsync(TimeSpan.Zero);
        var second = await service.GetOrSampleAsync(TimeSpan.FromSeconds(30));

        Assert.Same(first, second);
        Assert.Equal(1, runner.NvidiaCalls);
        Assert.True(service.TryGetRecentSample(TimeSpan.FromSeconds(30), out var recent));
        Assert.Same(first, recent);
    }

    [Fact]
    public async Task StopMonitoringAsync_CancelsAndAwaitsActiveLoop()
    {
        var runner = new CountingProbeRunner { BlockNvidia = true };
        await using var service = CreateService(runner);
        service.UpdateCapabilities(Capabilities(
            battery: CapabilitySupport.Unsupported,
            nvidiaGpu: CapabilitySupport.Supported,
            nvidiaSmi: CapabilitySupport.Supported,
            temperature: CapabilitySupport.Unsupported));
        service.StartMonitoring(TimeSpan.FromSeconds(1));
        await runner.NvidiaStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = service.StopMonitoringAsync();

        await runner.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(service.IsMonitoring);
    }

    [Fact]
    public async Task GenerateBatteryReportAsync_UnsupportedOrNoBattery_RejectsBeforeRunner()
    {
        var runner = new CountingProbeRunner();
        await using var service = CreateService(runner);
        service.UpdateCapabilities(Capabilities(
            battery: CapabilitySupport.Unsupported,
            nvidiaGpu: CapabilitySupport.Unsupported,
            nvidiaSmi: CapabilitySupport.Unsupported,
            temperature: CapabilitySupport.Unsupported));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GenerateBatteryReportAsync(
                Path.Combine(Path.GetTempPath(), $"PowerMode-no-battery-{Guid.NewGuid():N}.html")));

        Assert.Contains("battery", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.BatteryReportCalls);
    }

    [Fact]
    public async Task WindowsProbeRunner_UsesSharedRunnerWithFiniteExistingDeadlines()
    {
        var processRunner = new RecordingProcessRunner();
        var runner = new WindowsMonitoringProbeRunner(processRunner);

        await runner.QueryNvidiaAsync(CancellationToken.None);
        await runner.QueryTemperatureAsync(CancellationToken.None);
        await runner.QueryBatteryHealthAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.GenerateBatteryReportAsync(
                Path.Combine(Path.GetTempPath(), $"PowerMode-report-{Guid.NewGuid():N}.html"),
                CancellationToken.None));

        Assert.Collection(
            processRunner.Requests,
            request => Assert.Equal(("nvidia-smi.exe", TimeSpan.FromSeconds(2.5)),
                (request.FileName, request.Timeout)),
            request => Assert.Equal(("powershell.exe", TimeSpan.FromSeconds(2.5)),
                (request.FileName, request.Timeout)),
            request => Assert.Equal(("powercfg.exe", TimeSpan.FromSeconds(2.5)),
                (request.FileName, request.Timeout)),
            request => Assert.Equal(("powercfg.exe", TimeSpan.FromSeconds(30)),
                (request.FileName, request.Timeout)));
        Assert.All(processRunner.Requests, request =>
        {
            Assert.True(request.Timeout > TimeSpan.Zero);
            Assert.NotEqual(Timeout.InfiniteTimeSpan, request.Timeout);
        });
    }

    private static MonitoringService CreateService(CountingProbeRunner runner) =>
        new(runner, new ManualTimeProvider(Start));

    private static HardwareCapabilities Capabilities(
        CapabilitySupport battery,
        CapabilitySupport nvidiaGpu,
        CapabilitySupport nvidiaSmi,
        CapabilitySupport temperature) =>
        new(
            battery,
            CapabilitySupport.Unsupported,
            nvidiaGpu,
            nvidiaSmi,
            CapabilitySupport.Unsupported,
            temperature,
            CapabilitySupport.Unsupported,
            CapabilitySupport.Unsupported);

    private sealed class CountingProbeRunner : IMonitoringProbeRunner
    {
        private readonly TaskCompletionSource _nvidiaStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseNvidia =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _nvidiaCalls;
        private int _temperatureCalls;
        private int _batteryHealthCalls;
        private int _batteryReportCalls;

        public bool BlockNvidia { get; init; }
        public int NvidiaCalls => Volatile.Read(ref _nvidiaCalls);
        public int TemperatureCalls => Volatile.Read(ref _temperatureCalls);
        public int BatteryHealthCalls => Volatile.Read(ref _batteryHealthCalls);
        public int BatteryReportCalls => Volatile.Read(ref _batteryReportCalls);
        public (int Nvidia, int Temperature, int Battery) ExpensiveCalls =>
            (NvidiaCalls, TemperatureCalls, BatteryHealthCalls);
        public Task NvidiaStarted => _nvidiaStarted.Task;
        public Task CancellationObserved => _cancellationObserved.Task;

        public async Task<NvidiaTelemetry?> QueryNvidiaAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _nvidiaCalls);
            _nvidiaStarted.TrySetResult();
            if (BlockNvidia)
            {
                try
                {
                    await _releaseNvidia.Task.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    _cancellationObserved.TrySetResult();
                    throw;
                }
            }
            return new NvidiaTelemetry(10, 50, 25);
        }

        public Task<double?> QueryTemperatureAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _temperatureCalls);
            return Task.FromResult<double?>(45);
        }

        public Task<BatteryHealthTelemetry> QueryBatteryHealthAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _batteryHealthCalls);
            return Task.FromResult(new BatteryHealthTelemetry(50_000, 45_000, 90, 100));
        }

        public Task<string> GenerateBatteryReportAsync(
            string outputPath,
            CancellationToken token)
        {
            Interlocked.Increment(ref _batteryReportCalls);
            return Task.FromResult(outputPath);
        }

        public void ReleaseNvidia() => _releaseNvidia.TrySetResult();
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<ProcessExecutionRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessExecutionResult(
                1,
                string.Empty,
                "probe failed",
                TimeSpan.Zero,
                TimedOut: false,
                Cancelled: false,
                StartError: null));
        }
    }
}
