using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerModeWinUI;

/// <summary>Windows reports the current battery/AC relationship in these states.</summary>
public enum BatteryChargeState
{
    Unknown,
    NoBattery,
    Discharging,
    Charging,
    PluggedIn,
    FullyCharged
}

/// <summary>
/// One best-effort hardware sample. A null value means that Windows, the firmware,
/// or the installed GPU driver did not expose that measurement.
/// </summary>
public sealed record PowerTelemetrySample
{
    public DateTimeOffset Timestamp { get; init; }
    public double? CpuLoadPercent { get; init; }
    public double? CpuFrequencyMhz { get; init; }
    public double? NvidiaGpuPowerWatts { get; init; }
    public double? NvidiaGpuTemperatureCelsius { get; init; }
    public double? NvidiaGpuUtilizationPercent { get; init; }
    public byte? BatteryPercent { get; init; }
    public BatteryChargeState BatteryState { get; init; }
    public bool? IsOnAcPower { get; init; }
    public bool IsBatteryCritical { get; init; }
    public TimeSpan? EstimatedBatteryTimeRemaining { get; init; }
    public long? BatteryDesignCapacityMWh { get; init; }
    public long? BatteryFullChargeCapacityMWh { get; init; }
    public double? BatteryHealthPercent { get; init; }
    public int? BatteryCycleCount { get; init; }
    public double? ThermalZoneTemperatureCelsius { get; init; }
    public double? HighestTemperatureCelsius { get; init; }
}

/// <summary>A thread-safe, fixed-capacity history ordered from oldest to newest.</summary>
public sealed class PowerTelemetryHistory
{
    private readonly object _sync = new();
    private readonly PowerTelemetrySample?[] _buffer;
    private int _next;
    private int _count;

    public PowerTelemetryHistory(int capacity = 720)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

        _buffer = new PowerTelemetrySample[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get { lock (_sync) return _count; }
    }

    public void Add(PowerTelemetrySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_sync)
        {
            _buffer[_next] = sample;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length)
                _count++;
        }
    }

    public IReadOnlyList<PowerTelemetrySample> Snapshot()
    {
        lock (_sync)
        {
            var result = new PowerTelemetrySample[_count];
            var oldest = (_next - _count + _buffer.Length) % _buffer.Length;
            for (var i = 0; i < _count; i++)
                result[i] = _buffer[(oldest + i) % _buffer.Length]!;
            return result;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_buffer);
            _next = 0;
            _count = 0;
        }
    }
}

/// <summary>
/// Asynchronously samples power telemetry without requiring an additional NuGet package.
/// Process-backed probes are bounded by timeouts and all unavailable metrics remain null.
/// </summary>
public sealed class MonitoringService : IMonitoringSnapshotSource, IAsyncDisposable
{
    private static readonly TimeSpan DefaultSampleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan UnknownProbeRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BatteryHealthCacheDuration = TimeSpan.FromMinutes(30);
    private static readonly IProcessRunner CapabilityProcessRunner = new ProcessRunner();
    private readonly IMonitoringProbeRunner _probeRunner;
    private readonly TimeProvider _timeProvider;
    private readonly Func<MonitoringPowerStatus> _powerStatusReader;
    private readonly object _sampleSync = new();
    private readonly object _monitoringSync = new();
    private readonly CancellationTokenSource _sampleLifetimeCancellation = new();
    private HardwareCapabilities _capabilities = HardwareCapabilities.Unknown;
    private BatteryHealthTelemetry _batteryHealth = BatteryHealthTelemetry.Empty;
    private DateTimeOffset _batteryHealthExpiresAt;
    private DateTimeOffset _nextNvidiaUnknownRetryUtc;
    private DateTimeOffset _nextBatteryUnknownRetryUtc;
    private DateTimeOffset _nextTemperatureUnknownRetryUtc;
    private Task<PowerTelemetrySample>? _inFlightSample;
    private PowerTelemetrySample? _lastSuccessfulSample;
    private DateTimeOffset _lastSuccessfulSampleUtc;
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _monitoringTask;
    private bool _disposed;

    public MonitoringService(int historyCapacity = 720)
        : this(
            new WindowsMonitoringProbeRunner(new ProcessRunner()),
            TimeProvider.System,
            historyCapacity,
            GetPowerStatus)
    {
    }

    internal MonitoringService(
        IProcessRunner processRunner,
        int historyCapacity = 720)
        : this(
            new WindowsMonitoringProbeRunner(processRunner),
            TimeProvider.System,
            historyCapacity,
            GetPowerStatus)
    {
    }

    internal MonitoringService(
        IMonitoringProbeRunner probeRunner,
        TimeProvider timeProvider)
        : this(probeRunner, timeProvider, 720, GetPowerStatus)
    {
    }

    internal MonitoringService(
        IMonitoringProbeRunner probeRunner,
        TimeProvider timeProvider,
        Func<MonitoringPowerStatus> powerStatusReader)
        : this(probeRunner, timeProvider, 720, powerStatusReader)
    {
    }

    private MonitoringService(
        IMonitoringProbeRunner probeRunner,
        TimeProvider timeProvider,
        int historyCapacity,
        Func<MonitoringPowerStatus> powerStatusReader)
    {
        _probeRunner = probeRunner ?? throw new ArgumentNullException(nameof(probeRunner));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _powerStatusReader = powerStatusReader ?? throw new ArgumentNullException(nameof(powerStatusReader));
        History = new PowerTelemetryHistory(historyCapacity);
    }

    public PowerTelemetryHistory History { get; }

    /// <summary>
    /// Raised on a worker thread after a sample is stored. WinUI subscribers should dispatch
    /// visual updates through DispatcherQueue.
    /// </summary>
    public event Action<PowerTelemetrySample>? SampleAvailable;

    public event Action<Exception>? SamplingFailed;

    public bool IsMonitoring
    {
        get
        {
            lock (_monitoringSync)
                return _monitoringTask is { IsCompleted: false };
        }
    }

    public void UpdateCapabilities(HardwareCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        lock (_sampleSync)
            _capabilities = capabilities;
    }

    public bool TryGetRecentSample(
        TimeSpan maximumAge,
        out PowerTelemetrySample sample)
    {
        if (maximumAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(maximumAge),
                "Maximum age cannot be negative.");

        lock (_sampleSync)
            return TryGetRecentSampleLocked(maximumAge, out sample);
    }

    /// <summary>Collects and stores one partial-or-complete sample.</summary>
    public Task<PowerTelemetrySample> SampleAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrSampleAsync(TimeSpan.Zero, timeout, cancellationToken);

    public Task<PowerTelemetrySample> GetOrSampleAsync(
        TimeSpan maximumAge,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maximumAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(maximumAge),
                "Maximum age cannot be negative.");
        var effectiveTimeout = timeout ?? DefaultSampleTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");

        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<PowerTelemetrySample>(cancellationToken);

        Task<PowerTelemetrySample> sampleTask;
        lock (_sampleSync)
        {
            if (maximumAge > TimeSpan.Zero &&
                TryGetRecentSampleLocked(maximumAge, out var recent))
            {
                return Task.FromResult(recent);
            }

            sampleTask = _inFlightSample is { IsCompleted: false } inFlight
                ? inFlight
                : StartSampleLocked();
        }

        return sampleTask.WaitAsync(effectiveTimeout, cancellationToken);
    }

    private bool TryGetRecentSampleLocked(
        TimeSpan maximumAge,
        out PowerTelemetrySample sample)
    {
        if (_lastSuccessfulSample is { } recent)
        {
            var age = _timeProvider.GetUtcNow() - _lastSuccessfulSampleUtc;
            if (age >= TimeSpan.Zero && age <= maximumAge)
            {
                sample = recent;
                return true;
            }
        }

        sample = null!;
        return false;
    }

    private Task<PowerTelemetrySample> StartSampleLocked()
    {
        var task = SampleCoreAsync(_sampleLifetimeCancellation.Token);
        _inFlightSample = task;
        _ = ClearInFlightSampleAsync(task);
        return task;
    }

    private async Task ClearInFlightSampleAsync(Task<PowerTelemetrySample> sampleTask)
    {
        try
        {
            await sampleTask.ConfigureAwait(false);
        }
        catch
        {
            // The initiating caller and monitoring loop observe the original failure.
        }
        finally
        {
            lock (_sampleSync)
            {
                if (ReferenceEquals(_inFlightSample, sampleTask))
                    _inFlightSample = null;
            }
        }
    }

    private async Task<PowerTelemetrySample> SampleCoreAsync(
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(DefaultSampleTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        var probeToken = linkedCancellation.Token;
        var now = _timeProvider.GetUtcNow();
        HardwareCapabilities capabilities;
        lock (_sampleSync)
            capabilities = _capabilities;

        var power = _powerStatusReader();
        var cpuLoadTask = MeasureCpuLoadAsync(probeToken);
        var gpuTask = QueryNvidiaAsync(capabilities, now, probeToken);
        var thermalTask = QueryTemperatureAsync(capabilities, now, probeToken);
        var batteryHealthTask = GetBatteryHealthAsync(
            capabilities,
            power.State,
            now,
            probeToken);
        var cpuFrequencyMhz = GetCpuFrequencyMhz();

        await Task.WhenAll(cpuLoadTask, gpuTask, thermalTask, batteryHealthTask)
            .ConfigureAwait(false);
        _sampleLifetimeCancellation.Token.ThrowIfCancellationRequested();

        var gpu = await gpuTask.ConfigureAwait(false);
        var thermalTemperature = await thermalTask.ConfigureAwait(false);
        var batteryHealth = await batteryHealthTask.ConfigureAwait(false);
        var gpuTemperature = gpu?.TemperatureCelsius;
        var highestTemperature = MaxNullable(gpuTemperature, thermalTemperature);
        var completedAt = _timeProvider.GetUtcNow();
        var sample = new PowerTelemetrySample
        {
            Timestamp = completedAt,
            CpuLoadPercent = await cpuLoadTask.ConfigureAwait(false),
            CpuFrequencyMhz = cpuFrequencyMhz,
            NvidiaGpuPowerWatts = gpu?.PowerWatts,
            NvidiaGpuTemperatureCelsius = gpuTemperature,
            NvidiaGpuUtilizationPercent = gpu?.UtilizationPercent,
            BatteryPercent = power.Percent,
            BatteryState = power.State,
            IsOnAcPower = power.IsOnAcPower,
            IsBatteryCritical = power.IsCritical,
            EstimatedBatteryTimeRemaining = power.EstimatedRemaining,
            BatteryDesignCapacityMWh = batteryHealth.DesignCapacityMWh,
            BatteryFullChargeCapacityMWh = batteryHealth.FullChargeCapacityMWh,
            BatteryHealthPercent = batteryHealth.HealthPercent,
            BatteryCycleCount = batteryHealth.CycleCount,
            ThermalZoneTemperatureCelsius = thermalTemperature,
            HighestTemperatureCelsius = highestTemperature
        };

        lock (_sampleSync)
        {
            _lastSuccessfulSample = sample;
            _lastSuccessfulSampleUtc = completedAt;
        }
        History.Add(sample);
        RaiseSampleAvailable(sample);
        return sample;
    }

    private async Task<NvidiaTelemetry?> QueryNvidiaAsync(
        HardwareCapabilities capabilities,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var support = MonitoringProbePolicy.CombineNvidia(
            capabilities.NvidiaGpu,
            capabilities.NvidiaSmi);
        if (!ReserveProbe(ExpensiveProbeKind.Nvidia, support, now))
            return null;
        try
        {
            return await _probeRunner.QueryNvidiaAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<double?> QueryTemperatureAsync(
        HardwareCapabilities capabilities,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!ReserveProbe(
                ExpensiveProbeKind.Temperature,
                capabilities.TemperatureMonitoring,
                now))
        {
            return null;
        }
        try
        {
            return await _probeRunner.QueryTemperatureAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<BatteryHealthTelemetry> GetBatteryHealthAsync(
        HardwareCapabilities capabilities,
        BatteryChargeState powerState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var support = capabilities.Battery;
        if (support == CapabilitySupport.Unsupported ||
            powerState == BatteryChargeState.NoBattery)
            return BatteryHealthTelemetry.Empty;

        lock (_sampleSync)
        {
            if (now < _batteryHealthExpiresAt)
                return _batteryHealth;
        }
        if (!ReserveProbe(ExpensiveProbeKind.BatteryHealth, support, now))
            return BatteryHealthTelemetry.Empty;

        BatteryHealthTelemetry result;
        try
        {
            result = await _probeRunner.QueryBatteryHealthAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            result = BatteryHealthTelemetry.Empty;
        }

        lock (_sampleSync)
        {
            _batteryHealth = result;
            _batteryHealthExpiresAt = now +
                (result.HasData && support == CapabilitySupport.Supported
                    ? BatteryHealthCacheDuration
                    : UnknownProbeRetry);
        }
        return result;
    }

    private bool ReserveProbe(
        ExpensiveProbeKind kind,
        CapabilitySupport support,
        DateTimeOffset now)
    {
        lock (_sampleSync)
        {
            var nextRetry = kind switch
            {
                ExpensiveProbeKind.Nvidia => _nextNvidiaUnknownRetryUtc,
                ExpensiveProbeKind.BatteryHealth => _nextBatteryUnknownRetryUtc,
                ExpensiveProbeKind.Temperature => _nextTemperatureUnknownRetryUtc,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            if (!MonitoringProbePolicy.ShouldRun(support, now, nextRetry))
                return false;
            if (support != CapabilitySupport.Unknown)
                return true;

            var retryAt = now + UnknownProbeRetry;
            switch (kind)
            {
                case ExpensiveProbeKind.Nvidia:
                    _nextNvidiaUnknownRetryUtc = retryAt;
                    break;
                case ExpensiveProbeKind.BatteryHealth:
                    _nextBatteryUnknownRetryUtc = retryAt;
                    break;
                case ExpensiveProbeKind.Temperature:
                    _nextTemperatureUnknownRetryUtc = retryAt;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
            return true;
        }
    }

    /// <summary>Starts an immediate sample followed by periodic samples.</summary>
    public void StartMonitoring(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (interval < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be at least one second.");

        lock (_monitoringSync)
        {
            if (_monitoringTask is { IsCompleted: false })
                return;

            _monitoringCancellation?.Dispose();
            _monitoringCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _monitoringTask = MonitorLoopAsync(interval, _monitoringCancellation.Token);
        }
    }

    public async Task StopMonitoringAsync()
    {
        Task? task;
        lock (_monitoringSync)
        {
            _monitoringCancellation?.Cancel();
            task = _monitoringTask;
        }

        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        lock (_monitoringSync)
        {
            _monitoringCancellation?.Dispose();
            _monitoringCancellation = null;
            _monitoringTask = null;
        }
    }

    /// <summary>Writes the current ring-buffer snapshot as an Excel-friendly UTF-8 CSV.</summary>
    public async Task ExportHistoryCsvAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var csv = BuildCsv(History.Snapshot());
        await File.WriteAllTextAsync(fullPath, csv, new UTF8Encoding(true), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Generates the standard Windows HTML battery report and returns its full path.</summary>
    public Task<string> GenerateBatteryReportAsync(
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var currentPower = _powerStatusReader();
        lock (_sampleSync)
        {
            if (_capabilities.Battery == CapabilitySupport.Unsupported ||
                currentPower.State == BatteryChargeState.NoBattery ||
                _lastSuccessfulSample?.BatteryState == BatteryChargeState.NoBattery)
            {
                throw new InvalidOperationException(
                    "A battery report is unavailable because no battery was detected.");
            }
        }

        return _probeRunner.GenerateBatteryReportAsync(
            Path.GetFullPath(outputPath),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopMonitoringAsync().ConfigureAwait(false);
        _sampleLifetimeCancellation.Cancel();
        Task<PowerTelemetrySample>? sampleTask;
        lock (_sampleSync)
            sampleTask = _inFlightSample;
        if (sampleTask is not null)
        {
            try
            {
                await sampleTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown cancellation is expected for an in-flight sample.
            }
        }
        _sampleLifetimeCancellation.Dispose();
    }

    private async Task MonitorLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                await SampleAsync(DefaultSampleTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RaiseSamplingFailed(ex);
            }

            var remaining = interval - Stopwatch.GetElapsedTime(startedAt);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<double?> MeasureCpuLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!TryReadCpuTimes(out var first))
                return null;
            await Task.Delay(180, cancellationToken).ConfigureAwait(false);
            if (!TryReadCpuTimes(out var second))
                return null;

            if (second.Idle < first.Idle || second.Kernel < first.Kernel || second.User < first.User)
                return null;
            var idle = second.Idle - first.Idle;
            var total = (second.Kernel - first.Kernel) + (second.User - first.User);
            if (total == 0 || idle > total)
                return null;

            return Math.Round(Math.Clamp((total - idle) * 100d / total, 0d, 100d), 1);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static double? GetCpuFrequencyMhz()
    {
        nint buffer = 0;
        try
        {
            var processorCount = GetActiveProcessorCount(ushort.MaxValue);
            if (processorCount == 0)
                processorCount = (uint)Environment.ProcessorCount;
            var itemSize = Marshal.SizeOf<ProcessorPowerInformation>();
            var bufferSize = checked(itemSize * (int)processorCount);
            buffer = Marshal.AllocHGlobal(bufferSize);
            if (CallNtPowerInformation(11, 0, 0, buffer, (uint)bufferSize) != 0)
                return null;

            double sum = 0;
            var valid = 0;
            for (var i = 0; i < processorCount; i++)
            {
                var item = Marshal.PtrToStructure<ProcessorPowerInformation>(buffer + (i * itemSize));
                if (item.CurrentMhz is > 0 and < 20_000)
                {
                    sum += item.CurrentMhz;
                    valid++;
                }
            }

            return valid == 0 ? null : Math.Round(sum / valid);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != 0)
                Marshal.FreeHGlobal(buffer);
        }
    }

    private static MonitoringPowerStatus GetPowerStatus()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status))
                return MonitoringPowerStatus.Empty;

            var batteryFlagKnown = status.BatteryFlag != byte.MaxValue;
            var noBattery = batteryFlagKnown && (status.BatteryFlag & 128) != 0;
            var percent = noBattery || status.BatteryLifePercent == byte.MaxValue
                ? null
                : (byte?)status.BatteryLifePercent;
            bool? onAc = status.ACLineStatus switch
            {
                0 => false,
                1 => true,
                _ => null
            };
            var isCharging = batteryFlagKnown && (status.BatteryFlag & 8) != 0;
            var state = noBattery
                ? BatteryChargeState.NoBattery
                : isCharging
                    ? BatteryChargeState.Charging
                    : onAc == false
                        ? BatteryChargeState.Discharging
                        : onAc == true && percent == 100
                            ? BatteryChargeState.FullyCharged
                            : onAc == true
                                ? BatteryChargeState.PluggedIn
                                : BatteryChargeState.Unknown;

            TimeSpan? remaining = null;
            if (state == BatteryChargeState.Discharging && status.BatteryLifeTime != uint.MaxValue)
                remaining = TimeSpan.FromSeconds(status.BatteryLifeTime);

            return new MonitoringPowerStatus(
                percent,
                state,
                onAc,
                batteryFlagKnown && (status.BatteryFlag & 4) != 0,
                remaining);
        }
        catch
        {
            return MonitoringPowerStatus.Empty;
        }
    }

    internal static CapabilitySupport ProbeBatteryCapability()
    {
        var power = GetPowerStatus();
        if (power.State == BatteryChargeState.NoBattery)
            return CapabilitySupport.Unsupported;
        return power.Percent is not null || power.State != BatteryChargeState.Unknown
            ? CapabilitySupport.Supported
            : CapabilitySupport.Unknown;
    }

    internal static async Task<CapabilitySupport> ProbeBrightnessCapabilityAsync(
        CancellationToken cancellationToken)
    {
        const string command =
            "$ErrorActionPreference='Stop';try{" +
            "$value=Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness " +
            "-ErrorAction Stop | Select-Object -First 1;" +
            "if($null -eq $value){'unsupported'}else{'supported'}" +
            "}catch{[Console]::Error.WriteLine($_.Exception.Message);exit 1}";
        var result = await CapabilityProcessRunner.RunAsync(
            new ProcessExecutionRequest(
                "powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
                ProbeTimeout),
            cancellationToken).ConfigureAwait(false);
        return ClassifyWmiCapabilityProbeResult(
            result.ExitCode ?? -1,
            result.TimedOut,
            result.StandardOutput);
    }

    internal static async Task<CapabilitySupport> ProbeNvidiaSmiCapabilityAsync(
        CancellationToken cancellationToken)
    {
        var result = await CapabilityProcessRunner.RunAsync(
            new ProcessExecutionRequest(
                "nvidia-smi.exe",
                ["--query-gpu=name", "--format=csv,noheader"],
                ProbeTimeout),
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut || result.Cancelled)
            return CapabilitySupport.Unknown;
        if (result.StartError is not null)
            return CapabilitySupport.Unsupported;
        if (result.ExitCode != 0)
            return CapabilitySupport.Unknown;
        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? CapabilitySupport.Unsupported
            : CapabilitySupport.Supported;
    }

    internal static async Task<CapabilitySupport> ProbeTemperatureCapabilityAsync(
        CancellationToken cancellationToken)
    {
        const string command =
            "$ErrorActionPreference='Stop';try{" +
            "$value=Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature " +
            "-ErrorAction Stop | Select-Object -First 1;" +
            "if($null -eq $value){'unsupported'}else{'supported'}" +
            "}catch{[Console]::Error.WriteLine($_.Exception.Message);exit 1}";
        var result = await CapabilityProcessRunner.RunAsync(
            new ProcessExecutionRequest(
                "powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
                ProbeTimeout),
            cancellationToken).ConfigureAwait(false);
        return ClassifyWmiCapabilityProbeResult(
            result.ExitCode ?? -1,
            result.TimedOut,
            result.StandardOutput);
    }

    internal static CapabilitySupport ClassifyWmiCapabilityProbeResult(
        int exitCode,
        bool timedOut,
        string output)
    {
        if (timedOut || exitCode != 0)
            return CapabilitySupport.Unknown;
        foreach (var line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Equals("supported", StringComparison.OrdinalIgnoreCase))
                return CapabilitySupport.Supported;
            if (line.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
                return CapabilitySupport.Unsupported;
        }
        return CapabilitySupport.Unknown;
    }

    private static bool TryReadCpuTimes(out CpuTimes times)
    {
        times = default;
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return false;
        times = new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
        return true;
    }

    private static string BuildCsv(IReadOnlyList<PowerTelemetrySample> samples)
    {
        var builder = new StringBuilder(Math.Max(1024, samples.Count * 180));
        builder.AppendLine(
            "Timestamp,CpuLoadPercent,CpuFrequencyMHz,NvidiaGpuPowerWatts," +
            "NvidiaGpuTemperatureCelsius,NvidiaGpuUtilizationPercent,BatteryPercent," +
            "BatteryState,IsOnAcPower,IsBatteryCritical,EstimatedBatterySeconds," +
            "BatteryDesignCapacityMWh,BatteryFullChargeCapacityMWh,BatteryHealthPercent," +
            "BatteryCycleCount,ThermalZoneTemperatureCelsius,HighestTemperatureCelsius");
        foreach (var sample in samples)
        {
            AppendCsv(builder, sample.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            AppendCsv(builder, FormatNumber(sample.CpuLoadPercent));
            AppendCsv(builder, FormatNumber(sample.CpuFrequencyMhz));
            AppendCsv(builder, FormatNumber(sample.NvidiaGpuPowerWatts));
            AppendCsv(builder, FormatNumber(sample.NvidiaGpuTemperatureCelsius));
            AppendCsv(builder, FormatNumber(sample.NvidiaGpuUtilizationPercent));
            AppendCsv(builder, sample.BatteryPercent?.ToString(CultureInfo.InvariantCulture));
            AppendCsv(builder, sample.BatteryState.ToString());
            AppendCsv(builder, sample.IsOnAcPower?.ToString(CultureInfo.InvariantCulture));
            AppendCsv(builder, sample.IsBatteryCritical.ToString(CultureInfo.InvariantCulture));
            AppendCsv(builder, sample.EstimatedBatteryTimeRemaining?.TotalSeconds.ToString("0", CultureInfo.InvariantCulture));
            AppendCsv(builder, sample.BatteryDesignCapacityMWh?.ToString(CultureInfo.InvariantCulture));
            AppendCsv(builder, sample.BatteryFullChargeCapacityMWh?.ToString(CultureInfo.InvariantCulture));
            AppendCsv(builder, FormatNumber(sample.BatteryHealthPercent));
            AppendCsv(builder, sample.BatteryCycleCount?.ToString(CultureInfo.InvariantCulture));
            AppendCsv(builder, FormatNumber(sample.ThermalZoneTemperatureCelsius));
            AppendCsv(builder, FormatNumber(sample.HighestTemperatureCelsius), isLast: true);
        }
        return builder.ToString();
    }

    private static void AppendCsv(StringBuilder builder, string? value, bool isLast = false)
    {
        value ??= string.Empty;
        if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            builder.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
        else
            builder.Append(value);
        builder.Append(isLast ? Environment.NewLine : ',');
    }

    private static string? FormatNumber(double? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture);

    private static double? MaxNullable(double? first, double? second) =>
        first is null ? second : second is null ? first : Math.Max(first.Value, second.Value);

    private void RaiseSampleAvailable(PowerTelemetrySample sample)
    {
        var handlers = SampleAvailable;
        if (handlers is null)
            return;
        foreach (Action<PowerTelemetrySample> handler in handlers.GetInvocationList())
        {
            try { handler(sample); }
            catch { }
        }
    }

    private void RaiseSamplingFailed(Exception exception)
    {
        var handlers = SamplingFailed;
        if (handlers is null)
            return;
        foreach (Action<Exception> handler in handlers.GetInvocationList())
        {
            try { handler(exception); }
            catch { }
        }
    }

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
        public readonly ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        nint inputBuffer,
        uint inputBufferSize,
        nint outputBuffer,
        uint outputBufferSize);
}
