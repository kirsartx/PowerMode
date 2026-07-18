using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

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
public sealed class MonitoringService : IAsyncDisposable
{
    private static readonly TimeSpan DefaultSampleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan BatteryHealthCacheDuration = TimeSpan.FromMinutes(30);
    private static readonly Encoding NativeCommandEncoding = CreateNativeCommandEncoding();
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly object _monitoringSync = new();
    private BatteryHealthSnapshot _batteryHealth = BatteryHealthSnapshot.Empty;
    private DateTimeOffset _batteryHealthExpiresAt;
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _monitoringTask;
    private bool _disposed;

    public MonitoringService(int historyCapacity = 720)
    {
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

    /// <summary>Collects and stores one partial-or-complete sample.</summary>
    public async Task<PowerTelemetrySample> SampleAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var effectiveTimeout = timeout ?? DefaultSampleTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");

        await _sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCancellation = new CancellationTokenSource(effectiveTimeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCancellation.Token);
            var probeToken = linkedCancellation.Token;

            var cpuLoadTask = MeasureCpuLoadAsync(probeToken);
            var gpuTask = QueryNvidiaAsync(probeToken);
            var thermalTask = QueryThermalZoneAsync(probeToken);
            var batteryHealthTask = GetBatteryHealthAsync(probeToken);
            var cpuFrequencyMhz = GetCpuFrequencyMhz();
            var power = GetPowerStatus();

            await Task.WhenAll(cpuLoadTask, gpuTask, thermalTask, batteryHealthTask)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var gpu = await gpuTask.ConfigureAwait(false);
            var thermalTemperature = await thermalTask.ConfigureAwait(false);
            var batteryHealth = await batteryHealthTask.ConfigureAwait(false);
            var gpuTemperature = gpu?.TemperatureCelsius;
            var highestTemperature = MaxNullable(gpuTemperature, thermalTemperature);

            var sample = new PowerTelemetrySample
            {
                Timestamp = DateTimeOffset.Now,
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

            History.Add(sample);
            RaiseSampleAvailable(sample);
            return sample;
        }
        finally
        {
            _sampleGate.Release();
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
    public async Task<string> GenerateBatteryReportAsync(
        string outputPath,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var result = await RunProcessAsync(
            "powercfg.exe",
            ["/batteryreport", "/output", fullPath],
            timeout ?? TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
            throw new TimeoutException("Windows battery report generation timed out.");
        if (result.ExitCode != 0 || !File.Exists(fullPath))
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Error)
                    ? "Windows could not generate a battery report."
                    : result.Error.Trim());

        return fullPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopMonitoringAsync().ConfigureAwait(false);
        _sampleGate.Dispose();
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

    private static async Task<NvidiaSnapshot?> QueryNvidiaAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunProcessAsync(
                "nvidia-smi.exe",
                ["--query-gpu=power.draw,temperature.gpu,utilization.gpu", "--format=csv,noheader,nounits"],
                ProbeTimeout,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || result.TimedOut)
                return null;

            // A laptop normally has one NVIDIA GPU. For multi-GPU systems use total power,
            // maximum temperature, and maximum utilization so protection errs on the safe side.
            double totalPower = 0;
            double? maximumTemperature = null;
            double? maximumUtilization = null;
            var hasPower = false;
            foreach (var line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split(',', StringSplitOptions.TrimEntries);
                if (fields.Length < 3)
                    continue;
                if (TryParseNumber(fields[0], out var power) && power is >= 0 and < 10_000)
                {
                    totalPower += power;
                    hasPower = true;
                }
                if (TryParseNumber(fields[1], out var temperature) && temperature is > 0 and <= 150)
                    maximumTemperature = MaxNullable(maximumTemperature, temperature);
                if (TryParseNumber(fields[2], out var utilization) && utilization is >= 0 and <= 100)
                    maximumUtilization = MaxNullable(maximumUtilization, utilization);
            }

            if (!hasPower && maximumTemperature is null && maximumUtilization is null)
                return null;
            return new NvidiaSnapshot(
                hasPower ? Math.Round(totalPower, 2) : null,
                maximumTemperature,
                maximumUtilization);
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

    private static async Task<double?> QueryThermalZoneAsync(CancellationToken cancellationToken)
    {
        const string command =
            "$ErrorActionPreference='SilentlyContinue';" +
            "Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature | " +
            "ForEach-Object { (($_.CurrentTemperature / 10.0) - 273.15).ToString(" +
            "[Globalization.CultureInfo]::InvariantCulture) }";
        try
        {
            var result = await RunProcessAsync(
                "powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
                ProbeTimeout,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || result.TimedOut)
                return null;

            double? maximum = null;
            foreach (var line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseNumber(line.Trim(), out var temperature) && temperature is > 0 and <= 150)
                    maximum = MaxNullable(maximum, temperature);
            }
            return maximum is null ? null : Math.Round(maximum.Value, 1);
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

    private async Task<BatteryHealthSnapshot> GetBatteryHealthAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _batteryHealthExpiresAt)
            return _batteryHealth;

        var reportPath = Path.Combine(Path.GetTempPath(), $"PowerMode-battery-{Guid.NewGuid():N}.xml");
        try
        {
            var result = await RunProcessAsync(
                "powercfg.exe",
                ["/batteryreport", "/xml", "/output", reportPath],
                ProbeTimeout,
                cancellationToken).ConfigureAwait(false);
            var succeeded = result.ExitCode == 0 && !result.TimedOut && File.Exists(reportPath);
            if (succeeded)
                _batteryHealth = ParseBatteryHealth(reportPath);

            _batteryHealthExpiresAt = DateTimeOffset.UtcNow +
                (succeeded ? BatteryHealthCacheDuration : TimeSpan.FromMinutes(5));
            return _batteryHealth;
        }
        catch (OperationCanceledException)
        {
            return _batteryHealth;
        }
        catch
        {
            _batteryHealthExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
            return _batteryHealth;
        }
        finally
        {
            try { File.Delete(reportPath); }
            catch { }
        }
    }

    private static BatteryHealthSnapshot ParseBatteryHealth(string reportPath)
    {
        try
        {
            var document = XDocument.Load(reportPath, LoadOptions.None);
            var batteries = document.Descendants()
                .Where(element => element.Name.LocalName == "Battery")
                .ToArray();
            if (batteries.Length == 0)
                return BatteryHealthSnapshot.Empty;

            long designTotal = 0;
            long fullChargeTotal = 0;
            var hasDesign = false;
            var hasFullCharge = false;
            int? maximumCycleCount = null;
            foreach (var battery in batteries)
            {
                var design = ParseLongElement(battery, "DesignCapacity");
                var fullCharge = ParseLongElement(battery, "FullChargeCapacity");
                var cycleCount = ParseIntElement(battery, "CycleCount");
                if (design is > 0)
                {
                    designTotal += design.Value;
                    hasDesign = true;
                }
                if (fullCharge is > 0)
                {
                    fullChargeTotal += fullCharge.Value;
                    hasFullCharge = true;
                }
                if (cycleCount is >= 0)
                    maximumCycleCount = Math.Max(maximumCycleCount ?? 0, cycleCount.Value);
            }

            long? designCapacity = hasDesign ? designTotal : null;
            long? fullChargeCapacity = hasFullCharge ? fullChargeTotal : null;
            double? health = designCapacity is > 0 && fullChargeCapacity is not null
                ? Math.Round(fullChargeCapacity.Value * 100d / designCapacity.Value, 1)
                : null;
            return new BatteryHealthSnapshot(
                designCapacity,
                fullChargeCapacity,
                health,
                maximumCycleCount);
        }
        catch
        {
            return BatteryHealthSnapshot.Empty;
        }
    }

    private static PowerStatusSnapshot GetPowerStatus()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status))
                return PowerStatusSnapshot.Empty;

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

            return new PowerStatusSnapshot(
                percent,
                state,
                onAc,
                batteryFlagKnown && (status.BatteryFlag & 4) != 0,
                remaining);
        }
        catch
        {
            return PowerStatusSnapshot.Empty;
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
            "$value=Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness " +
            "-ErrorAction SilentlyContinue | Select-Object -First 1;" +
            "if($null -ne $value){'supported'}";
        var result = await RunProcessAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
            ProbeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
            return CapabilitySupport.Unknown;
        if (result.ExitCode != 0)
            return CapabilitySupport.Unknown;
        return result.Output.Contains("supported", StringComparison.OrdinalIgnoreCase)
            ? CapabilitySupport.Supported
            : CapabilitySupport.Unsupported;
    }

    internal static async Task<CapabilitySupport> ProbeNvidiaSmiCapabilityAsync(
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            "nvidia-smi.exe",
            ["--query-gpu=name", "--format=csv,noheader"],
            ProbeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
            return CapabilitySupport.Unknown;
        if (result.ExitCode == -1)
            return CapabilitySupport.Unsupported;
        if (result.ExitCode != 0)
            return CapabilitySupport.Unknown;
        return string.IsNullOrWhiteSpace(result.Output)
            ? CapabilitySupport.Unsupported
            : CapabilitySupport.Supported;
    }

    internal static async Task<CapabilitySupport> ProbeTemperatureCapabilityAsync(
        CancellationToken cancellationToken)
    {
        const string command =
            "$value=Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature " +
            "-ErrorAction SilentlyContinue | Select-Object -First 1;" +
            "if($null -ne $value){'supported'}";
        var result = await RunProcessAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
            ProbeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
            return CapabilitySupport.Unknown;
        if (result.ExitCode != 0)
            return CapabilitySupport.Unknown;
        return result.Output.Contains("supported", StringComparison.OrdinalIgnoreCase)
            ? CapabilitySupport.Supported
            : CapabilitySupport.Unsupported;
    }

    private static bool TryReadCpuTimes(out CpuTimes times)
    {
        times = default;
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return false;
        times = new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
        return true;
    }

    private static async Task<ProcessExecutionResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Windows console utilities (notably powercfg) write using the active OEM code
            // page when redirected. Reading them as UTF-8 corrupts localized diagnostics.
            StandardOutputEncoding = NativeCommandEncoding,
            StandardErrorEncoding = NativeCommandEncoding
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new ProcessExecutionResult(-1, string.Empty, "Process failed to start.", false);
        }
        catch (Exception ex)
        {
            return new ProcessExecutionResult(-1, string.Empty, ex.Message, false);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCancellation.Token);
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new ProcessExecutionResult(process.ExitCode, output, error, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await DrainRedirectedOutputAsync(outputTask, errorTask).ConfigureAwait(false);
            return new ProcessExecutionResult(-1, string.Empty, "Process timed out.", true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainRedirectedOutputAsync(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DrainRedirectedOutputAsync(Task<string> outputTask, Task<string> errorTask)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask)
                .WaitAsync(TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
        }
        catch { }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
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

    private static long? ParseLongElement(XElement parent, string name) =>
        long.TryParse(
            parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static int? ParseIntElement(XElement parent, string name) =>
        int.TryParse(
            parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static bool TryParseNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
        double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number);

    private static double? MaxNullable(double? first, double? second) =>
        first is null ? second : second is null ? first : Math.Max(first.Value, second.Value);

    private static Encoding CreateNativeCommandEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

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
    private readonly record struct NvidiaSnapshot(double? PowerWatts, double? TemperatureCelsius, double? UtilizationPercent);
    private readonly record struct BatteryHealthSnapshot(
        long? DesignCapacityMWh,
        long? FullChargeCapacityMWh,
        double? HealthPercent,
        int? CycleCount)
    {
        public static BatteryHealthSnapshot Empty { get; } = new(null, null, null, null);
    }

    private readonly record struct PowerStatusSnapshot(
        byte? Percent,
        BatteryChargeState State,
        bool? IsOnAcPower,
        bool IsCritical,
        TimeSpan? EstimatedRemaining)
    {
        public static PowerStatusSnapshot Empty { get; } =
            new(null, BatteryChargeState.Unknown, null, false, null);
    }

    private readonly record struct ProcessExecutionResult(
        int ExitCode,
        string Output,
        string Error,
        bool TimedOut);

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

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        nint inputBuffer,
        uint inputBufferSize,
        nint outputBuffer,
        uint outputBufferSize);
}
