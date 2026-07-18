using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace PowerModeWinUI;

public sealed partial class InsightsWindow : Window
{
    private const int MaximumChartSamples = 90;
    private readonly MonitoringService _monitoringService;
    private readonly SystemIntegrationService _systemIntegrationService;
    private readonly bool _ownsMonitoringService;
    private readonly bool _ownsSystemIntegrationService;
    private readonly bool _isChinese;
    private readonly Func<string>? _sessionLogProvider;
    private readonly DispatcherTimer _fallbackRefreshTimer = new();
    private bool _refreshing;
    private bool _loaded;
    private PowerTelemetrySample? _latestSample;

    /// <summary>
    /// Creates the system-insights window. Pass the MainWindow's monitoring service so both
    /// surfaces share the same ring buffer. Dependencies are optional for designer/test use.
    /// </summary>
    public InsightsWindow(
        MonitoringService? monitoringService = null,
        SystemIntegrationService? systemIntegrationService = null,
        bool isChinese = true,
        Func<string>? sessionLogProvider = null)
    {
        InitializeComponent();
        _monitoringService = monitoringService ?? new MonitoringService();
        _systemIntegrationService = systemIntegrationService ?? new SystemIntegrationService();
        _ownsMonitoringService = monitoringService is null;
        _ownsSystemIntegrationService = systemIntegrationService is null;
        _isChinese = isChinese;
        _sessionLogProvider = sessionLogProvider;

        DpiAwareWindowSizer.Resize(this, 1080, 720, 900, 620);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
        ApplyLanguage();

        _fallbackRefreshTimer.Interval = TimeSpan.FromSeconds(10);
        _fallbackRefreshTimer.Tick += FallbackRefreshTimer_Tick;
        _monitoringService.SampleAvailable += MonitoringService_SampleAvailable;
        _monitoringService.SamplingFailed += MonitoringService_SamplingFailed;
        Closed += InsightsWindow_Closed;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;

        await RefreshHistoryAsync();
        var previous = _monitoringService.History.Snapshot().LastOrDefault();
        if (previous is not null)
            RenderSample(previous);
        RenderTrend();

        if (!_monitoringService.IsMonitoring)
            _fallbackRefreshTimer.Start();
        await RefreshSampleAsync(showSuccess: false);
    }

    private void ApplyLanguage()
    {
        Title = _isChinese ? "PowerMode 系统洞察" : "PowerMode Insights";
        if (_isChinese)
            return;

        HeaderText.Text = "System insights";
        SubheaderText.Text = "Power, temperature, battery health and switching history";
        RefreshButton.Content = "Refresh";
        ExportCsvButton.Content = "Export CSV";
        CpuMetricLabel.Text = "CPU load";
        GpuPowerLabel.Text = "GPU power";
        TemperatureLabel.Text = "Peak temperature";
        BatteryLabel.Text = "Battery";
        HealthLabel.Text = "Battery health";
        TrendTab.Header = "Trends";
        HistoryTab.Header = "Switch history";
        BatteryTab.Header = "Battery health";
        TrendHint.Text = "Recent GPU power and temperature samples";
        PowerLegend.Text = "GPU power";
        TemperatureLegend.Text = "Temperature";
        TrendEmptyText.Text = "No telemetry samples yet";
        BatteryReportButton.Content = "Generate Windows battery report";
        DiagnosticButton.Content = "Export diagnostics";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSampleAsync(showSuccess: true);
        await RefreshHistoryAsync();
    }

    private async void FallbackRefreshTimer_Tick(object? sender, object e) =>
        await RefreshSampleAsync(showSuccess: false);

    private async Task RefreshSampleAsync(bool showSuccess)
    {
        if (_refreshing)
            return;

        _refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var sample = await _monitoringService.SampleAsync();
            RenderSample(sample);
            if (showSuccess)
                ShowStatus(_isChinese ? "数据已刷新。" : "Telemetry refreshed.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(FormatFailure(_isChinese ? "刷新失败" : "Refresh failed", ex), InfoBarSeverity.Error);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _refreshing = false;
        }
    }

    private void MonitoringService_SampleAvailable(PowerTelemetrySample sample)
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => RenderSample(sample));
    }

    private void MonitoringService_SamplingFailed(Exception exception)
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            ShowStatus(FormatFailure(_isChinese ? "后台采样失败" : "Background sampling failed", exception),
                InfoBarSeverity.Warning));
    }

    private void RenderSample(PowerTelemetrySample sample)
    {
        _latestSample = sample;
        CpuMetricValue.Text = sample.CpuLoadPercent is { } cpu
            ? $"{cpu:F1}%" + (sample.CpuFrequencyMhz is { } mhz ? $" · {mhz:F0} MHz" : string.Empty)
            : "—";
        GpuPowerValue.Text = sample.NvidiaGpuPowerWatts is { } power ? $"{power:F1} W" : "—";
        TemperatureValue.Text = sample.HighestTemperatureCelsius is { } temperature
            ? $"{temperature:F1} °C"
            : "—";
        BatteryValue.Text = sample.BatteryPercent is { } battery
            ? $"{battery}% · {FormatBatteryState(sample.BatteryState)}"
            : FormatBatteryState(sample.BatteryState);
        HealthValue.Text = sample.BatteryHealthPercent is { } health ? $"{health:F1}%" : "—";
        BatteryDetails.Text = BuildBatteryDetails(sample);
        RenderTrend();
    }

    private void RenderTrend()
    {
        var samples = _monitoringService.History.Snapshot()
            .TakeLast(MaximumChartSamples)
            .ToArray();
        var width = TrendCanvas.ActualWidth;
        var height = TrendCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
            return;

        var hasPower = samples.Any(x => x.NvidiaGpuPowerWatts.HasValue);
        var hasTemperature = samples.Any(x => x.HighestTemperatureCelsius.HasValue);
        TrendEmptyText.Visibility = hasPower || hasTemperature ? Visibility.Collapsed : Visibility.Visible;
        var maximumPower = samples.Length == 0
            ? 10
            : Math.Max(10, samples.Max(x => x.NvidiaGpuPowerWatts ?? 0) * 1.1);
        PowerLine.Points = BuildSeries(samples, width, height, x => x.NvidiaGpuPowerWatts,
            maximumPower);
        TemperatureLine.Points = BuildSeries(samples, width, height, x => x.HighestTemperatureCelsius, 100);

        if (samples.Length == 0)
            return;
        var start = samples[0].Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
        var end = samples[^1].Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
        TrendHint.Text = _isChinese
            ? $"最近 {samples.Length} 次采样 · {start}–{end}"
            : $"Latest {samples.Length} samples · {start}–{end}";
    }

    private static PointCollection BuildSeries(
        IReadOnlyList<PowerTelemetrySample> samples,
        double width,
        double height,
        Func<PowerTelemetrySample, double?> selector,
        double maximum)
    {
        var points = new PointCollection();
        if (samples.Count == 0 || maximum <= 0)
            return points;

        var divisor = Math.Max(1, samples.Count - 1);
        for (var index = 0; index < samples.Count; index++)
        {
            var value = selector(samples[index]);
            if (!value.HasValue)
                continue;
            var x = width * index / divisor;
            var y = height - Math.Clamp(value.Value / maximum, 0, 1) * height;
            points.Add(new Point(x, y));
        }
        return points;
    }

    private void TrendCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderTrend();

    private async Task RefreshHistoryAsync()
    {
        try
        {
            var history = await HistoryStore.Default.GetRecentAsync(150);
            HistoryList.ItemsSource = history.Count == 0
                ? [_isChinese ? "暂无切换记录" : "No switching history yet"]
                : history.Select(FormatHistoryEntry).ToArray();
        }
        catch (Exception ex)
        {
            ShowStatus(FormatFailure(_isChinese ? "读取切换历史失败" : "Could not read switch history", ex),
                InfoBarSeverity.Warning);
        }
    }

    private string FormatHistoryEntry(SwitchHistoryEntry entry)
    {
        var timestamp = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        var state = entry.Succeeded ? (_isChinese ? "成功" : "Succeeded") : (_isChinese ? "失败" : "Failed");
        var trigger = string.IsNullOrWhiteSpace(entry.RuleName) ? entry.Trigger : entry.RuleName;
        var reason = string.IsNullOrWhiteSpace(entry.Reason) ? string.Empty : $" · {entry.Reason}";
        return $"{timestamp}  {entry.PreviousMode} → {entry.TargetMode}  [{state}] · {trigger}{reason} · {entry.DurationMilliseconds} ms";
    }

    private async void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = CreateSavePicker(_isChinese ? "PowerMode-遥测历史" : "PowerMode-telemetry", "CSV", ".csv");
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        await RunExportAsync(
            () => _monitoringService.ExportHistoryCsvAsync(file.Path),
            _isChinese ? $"CSV 已导出到 {file.Path}" : $"CSV exported to {file.Path}");
    }

    private async void BatteryReportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = CreateSavePicker(_isChinese ? "PowerMode-电池报告" : "PowerMode-battery-report", "HTML", ".html");
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        await RunExportAsync(
            () => _monitoringService.GenerateBatteryReportAsync(file.Path),
            _isChinese ? $"电池报告已生成到 {file.Path}" : $"Battery report generated at {file.Path}");
    }

    private async void DiagnosticButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = CreateSavePicker(
            $"PowerMode-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}", "ZIP", ".zip");
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        SetExportBusy(true);
        var telemetryPath = Path.Combine(Path.GetTempPath(), $"PowerMode-telemetry-{Guid.NewGuid():N}.csv");
        try
        {
            await _monitoringService.ExportHistoryCsvAsync(telemetryPath);
            var result = await _systemIntegrationService.ExportDiagnosticPackageAsync(new DiagnosticExportRequest
            {
                DestinationZipPath = file.Path,
                ConfigurationPath = SettingsStore.FilePath,
                HistoryFiles = [HistoryStore.Default.FilePath, telemetryPath],
                SessionLog = _sessionLogProvider?.Invoke(),
                CommandTimeout = TimeSpan.FromSeconds(20)
            });
            var warning = result.HadTimeouts || result.MissingFiles.Count > 0;
            ShowStatus(
                _isChinese
                    ? $"诊断包已导出到 {result.ZipPath}" + (warning ? "（部分项目不可用）" : string.Empty)
                    : $"Diagnostics exported to {result.ZipPath}" + (warning ? " (some items unavailable)" : string.Empty),
                warning ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(FormatFailure(_isChinese ? "诊断包导出失败" : "Diagnostics export failed", ex),
                InfoBarSeverity.Error);
        }
        finally
        {
            try { File.Delete(telemetryPath); } catch { }
            SetExportBusy(false);
        }
    }

    private async Task RunExportAsync(Func<Task> operation, string successMessage)
    {
        SetExportBusy(true);
        try
        {
            await operation();
            ShowStatus(successMessage, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(FormatFailure(_isChinese ? "导出失败" : "Export failed", ex), InfoBarSeverity.Error);
        }
        finally
        {
            SetExportBusy(false);
        }
    }

    private async Task RunExportAsync(Func<Task<string>> operation, string successMessage)
    {
        SetExportBusy(true);
        try
        {
            await operation();
            ShowStatus(successMessage, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(FormatFailure(_isChinese ? "导出失败" : "Export failed", ex), InfoBarSeverity.Error);
        }
        finally
        {
            SetExportBusy(false);
        }
    }

    private FileSavePicker CreateSavePicker(string suggestedFileName, string displayName, string extension)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedFileName };
        picker.FileTypeChoices.Add(displayName, [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return picker;
    }

    private string BuildBatteryDetails(PowerTelemetrySample sample)
    {
        var separator = Environment.NewLine;
        if (_isChinese)
        {
            return string.Join(separator,
            [
                $"状态：{FormatBatteryState(sample.BatteryState)}",
                $"电量：{FormatPercent(sample.BatteryPercent)}",
                $"预计剩余时间：{FormatDuration(sample.EstimatedBatteryTimeRemaining)}",
                $"设计容量：{FormatCapacity(sample.BatteryDesignCapacityMWh)}",
                $"满充容量：{FormatCapacity(sample.BatteryFullChargeCapacityMWh)}",
                $"健康度：{FormatPercent(sample.BatteryHealthPercent)}",
                $"循环次数：{sample.BatteryCycleCount?.ToString(CultureInfo.CurrentCulture) ?? "不可用"}",
                $"GPU 温度：{FormatTemperature(sample.NvidiaGpuTemperatureCelsius)}",
                $"系统热区：{FormatTemperature(sample.ThermalZoneTemperatureCelsius)}",
                "",
                "说明：部分固件或驱动不会向 Windows 暴露容量、循环次数、温度或 GPU 功耗；显示“不可用”不代表刷新失败。"
            ]);
        }

        return string.Join(separator,
        [
            $"State: {FormatBatteryState(sample.BatteryState)}",
            $"Charge: {FormatPercent(sample.BatteryPercent)}",
            $"Estimated remaining: {FormatDuration(sample.EstimatedBatteryTimeRemaining)}",
            $"Design capacity: {FormatCapacity(sample.BatteryDesignCapacityMWh)}",
            $"Full-charge capacity: {FormatCapacity(sample.BatteryFullChargeCapacityMWh)}",
            $"Health: {FormatPercent(sample.BatteryHealthPercent)}",
            $"Cycle count: {sample.BatteryCycleCount?.ToString(CultureInfo.CurrentCulture) ?? "Unavailable"}",
            $"GPU temperature: {FormatTemperature(sample.NvidiaGpuTemperatureCelsius)}",
            $"Thermal zone: {FormatTemperature(sample.ThermalZoneTemperatureCelsius)}",
            "",
            "Some firmware and drivers do not expose capacity, cycle count, temperature or GPU power to Windows. “Unavailable” does not mean refresh failed."
        ]);
    }

    private string FormatBatteryState(BatteryChargeState state) => state switch
    {
        BatteryChargeState.NoBattery => _isChinese ? "未检测到电池" : "No battery",
        BatteryChargeState.Discharging => _isChinese ? "放电中" : "Discharging",
        BatteryChargeState.Charging => _isChinese ? "充电中" : "Charging",
        BatteryChargeState.PluggedIn => _isChinese ? "已接通电源" : "Plugged in",
        BatteryChargeState.FullyCharged => _isChinese ? "已充满" : "Fully charged",
        _ => _isChinese ? "未知" : "Unknown"
    };

    private string FormatPercent(byte? value) => value.HasValue ? $"{value}%" : (_isChinese ? "不可用" : "Unavailable");
    private string FormatPercent(double? value) => value.HasValue ? $"{value:F1}%" : (_isChinese ? "不可用" : "Unavailable");
    private string FormatCapacity(long? value) => value.HasValue ? $"{value:N0} mWh" : (_isChinese ? "不可用" : "Unavailable");
    private string FormatTemperature(double? value) => value.HasValue ? $"{value:F1} °C" : (_isChinese ? "不可用" : "Unavailable");

    private string FormatDuration(TimeSpan? value)
    {
        if (!value.HasValue)
            return _isChinese ? "不可用" : "Unavailable";
        return _isChinese
            ? $"{(int)value.Value.TotalHours} 小时 {value.Value.Minutes} 分钟"
            : $"{(int)value.Value.TotalHours} h {value.Value.Minutes} min";
    }

    private void SetExportBusy(bool busy)
    {
        ExportCsvButton.IsEnabled = !busy;
        BatteryReportButton.IsEnabled = !busy;
        DiagnosticButton.IsEnabled = !busy;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfo.Message = message;
        StatusInfo.Severity = severity;
        StatusInfo.IsOpen = true;
    }

    private static string FormatFailure(string title, Exception exception)
    {
        var message = exception.Message.Trim();
        return string.IsNullOrEmpty(message) ? title : $"{title}：{message}";
    }

    private async void InsightsWindow_Closed(object sender, WindowEventArgs args)
    {
        _fallbackRefreshTimer.Stop();
        _fallbackRefreshTimer.Tick -= FallbackRefreshTimer_Tick;
        _monitoringService.SampleAvailable -= MonitoringService_SampleAvailable;
        _monitoringService.SamplingFailed -= MonitoringService_SamplingFailed;
        if (_ownsMonitoringService)
            await _monitoringService.DisposeAsync();
        if (_ownsSystemIntegrationService)
            _systemIntegrationService.Dispose();
    }
}
