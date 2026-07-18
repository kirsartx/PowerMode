using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace PowerModeWinUI;

public sealed partial class MainWindow
{
    private readonly AutomationEngine _automationEngine = new();
    private readonly MonitoringService _monitoringService = new(720);
    private readonly SystemIntegrationService _systemIntegration = new(
        "PowerMode",
        dataDirectory: SettingsStore.DirectoryPath);
    private readonly SemaphoreSlim _monitoringConfigurationGate = new(1, 1);
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private CancellationTokenSource? _modeSwitchCancellation;
    private PowerTelemetrySample? _lastTelemetry;
    private InsightsWindow? _insightsWindow;
    private long _modeSwitchGeneration;
    private bool _modeSwitchInProgress;
    private bool _advancedFeaturesInitialized;
    private bool _temperatureProtectionActive;
    private bool _handlingTemperature;
    private string _temperatureRestoreMode = "balanced";
    private DateTimeOffset _lastLowBatteryNotification;
    private RecommendationContext? _recommendationContext;
    private ModeRecommendation? _currentRecommendation;
    private readonly RecommendationApplyGate _recommendationApplyGate = new();

    private sealed record SwitchRequestContext(
        string Trigger,
        string Reason = "",
        Guid? RuleId = null,
        string? RuleName = null,
        bool AllowPreview = true)
    {
        public static SwitchRequestContext Manual { get; } = new("manual");
    }

    private void InitializeAdvancedFeatures()
    {
        if (_advancedFeaturesInitialized)
            return;

        _advancedFeaturesInitialized = true;
        _systemIntegration.NotificationSink = notification =>
        {
            if (!_featureSettings.NotificationsEnabled)
                return true;
            if (!_trayAdded)
                return false;

            void Submit() => ShowTrayNotification(notification.Title, notification.Message);
            if (DispatcherQueue.HasThreadAccess)
                Submit();
            else
                DispatcherQueue.TryEnqueue(Submit);
            return true;
        };
        _monitoringService.SampleAvailable += MonitoringService_SampleAvailable;
        _monitoringService.SamplingFailed += MonitoringService_SamplingFailed;
    }

    internal MonitoringService SharedMonitoringService => _monitoringService;
    internal SystemIntegrationService SharedSystemIntegrationService => _systemIntegration;

    private async Task RunStartupFeaturesAsync()
    {
        if (!_featureSettings.CheckUpdatesOnStartup || string.IsNullOrWhiteSpace(_featureSettings.UpdateApiUrl))
            return;

        await CheckForUpdatesAndNotifyAsync(_featureSettings.UpdateApiUrl, silentWhenCurrent: true);
    }

    internal void ApplySystemSettings(PowerModeSettings settings)
    {
        try
        {
            _systemIntegration.ConfigureStartup(settings.StartWithWindows, settings.StartMinimized);
        }
        catch (Exception ex)
        {
            AppendLog($"Startup registration: {ex.Message}");
            StatusText.Text = IsChinese ? "开机启动设置失败" : "Startup setting failed";
            StatusBar.Severity = InfoBarSeverity.Warning;
        }

        _ = BackupSettingsIfChangedAsync(settings.ConfigurationBackupCount, "settings-save");
    }

    private async Task BackupSettingsIfChangedAsync(int retention, string reason)
    {
        try
        {
            await _systemIntegration.BackupConfigurationIfChangedAsync(
                SettingsStore.FilePath,
                reason,
                Math.Clamp(retention, 1, 50));
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => AppendLog($"Configuration backup: {ex.Message}"));
        }
    }

    internal Task<ConfigurationBackupInfo> CreateSettingsBackupAsync() =>
        _systemIntegration.CreateConfigurationBackupAsync(
            SettingsStore.FilePath,
            "manual",
            Math.Clamp(_featureSettings.ConfigurationBackupCount, 1, 50));

    internal IReadOnlyList<ConfigurationBackupInfo> ListSettingsBackups() =>
        _systemIntegration.ListConfigurationBackups();

    internal async Task<ConfigurationRestoreResult?> RestoreLatestSettingsBackupAsync()
    {
        var backups = _systemIntegration.ListConfigurationBackups();
        ConfigurationBackupInfo? latest;
        try
        {
            var currentHash = File.Exists(SettingsStore.FilePath)
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SettingsStore.FilePath)))
                : string.Empty;
            latest = backups.FirstOrDefault(backup =>
                !string.Equals(backup.Sha256, currentHash, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            latest = backups.FirstOrDefault();
        }
        if (latest is null)
            return null;

        var result = await _systemIntegration.RestoreConfigurationBackupAsync(
            latest.Path,
            SettingsStore.FilePath,
            createSafetyBackup: true);
        if (result.Succeeded)
            ApplyFeatureSettings(SettingsStore.Load());
        return result;
    }

    internal ChargingLimitCapability GetChargingLimitCapability() =>
        _systemIntegration.GetChargingLimitCapability();

    internal async Task<UpdateCheckResult> CheckForUpdatesAsync(string apiUrl)
    {
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException(IsChinese ? "请输入有效的更新 API 地址。" : "Enter a valid update API URL.", nameof(apiUrl));
        return await _systemIntegration.CheckForUpdatesAsync(uri);
    }

    private async Task CheckForUpdatesAndNotifyAsync(string apiUrl, bool silentWhenCurrent)
    {
        try
        {
            var result = await CheckForUpdatesAsync(apiUrl);
            if (!result.Succeeded)
            {
                AppendLog($"Update check: {result.Error}");
                return;
            }

            if (result.IsUpdateAvailable)
            {
                _systemIntegration.Notify(new UserNotification(
                    IsChinese ? "PowerMode 有新版本" : "PowerMode update available",
                    IsChinese
                        ? $"最新版本 {result.LatestVersion}，当前版本 {result.CurrentVersion}"
                        : $"Latest {result.LatestVersion}; current {result.CurrentVersion}",
                    NotificationSeverity.Information));
            }
            else if (!silentWhenCurrent)
            {
                StatusText.Text = IsChinese ? "当前已是最新版本" : "PowerMode is up to date";
                StatusBar.Severity = InfoBarSeverity.Success;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Update check: {ex.Message}");
        }
    }

    private async Task ConfigureMonitoringAsync()
    {
        await _monitoringConfigurationGate.WaitAsync();
        try
        {
            await _monitoringService.StopMonitoringAsync();
            if (_featureSettings.RealTimeMonitoringEnabled || _featureSettings.TemperatureProtectionEnabled)
            {
                _monitoringService.StartMonitoring(TimeSpan.FromSeconds(
                    Math.Max(10, _featureSettings.MonitorIntervalSeconds)));
            }
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AppendLog($"Monitoring: {ex.Message}");
                StatusText.Text = IsChinese ? "硬件监控启动失败" : "Hardware monitoring failed to start";
                StatusBar.Severity = InfoBarSeverity.Warning;
            });
        }
        finally
        {
            _monitoringConfigurationGate.Release();
        }
    }

    private void MonitoringService_SampleAvailable(PowerTelemetrySample sample)
    {
        _lastTelemetry = sample;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (sample.NvidiaGpuPowerWatts is { } gpuPower)
                GpuValue.Text = $"{gpuPower:F1} W";
            else if (_featureSettings.RealTimeMonitoringEnabled)
                GpuValue.Text = IsChinese ? "不可用" : "Unavailable";

            if (sample.BatteryPercent is { } battery)
                PowerValue.Text = sample.IsOnAcPower == true
                    ? (IsChinese ? $"交流电 · {battery}%" : $"AC · {battery}%")
                    : (IsChinese ? $"电池 · {battery}%" : $"Battery · {battery}%");

            LastUpdatedText.Text = string.Format(T("LastUpdated"), sample.Timestamp.ToLocalTime().ToString("HH:mm:ss"));
            NotifyLowBatteryIfNeeded(sample);
            _ = HandleTemperatureSampleAsync(sample);
        });
    }

    private void MonitoringService_SamplingFailed(Exception exception)
    {
        DispatcherQueue.TryEnqueue(() => AppendLog($"Telemetry sample: {exception.Message}"));
    }

    private void NotifyLowBatteryIfNeeded(PowerTelemetrySample sample)
    {
        if (sample.IsOnAcPower != false || sample.BatteryPercent is not { } percent ||
            percent > _featureSettings.LowBatteryThreshold ||
            DateTimeOffset.Now - _lastLowBatteryNotification < TimeSpan.FromMinutes(30))
            return;

        _lastLowBatteryNotification = DateTimeOffset.Now;
        _systemIntegration.Notify(new UserNotification(
            IsChinese ? "电量偏低" : "Low battery",
            IsChinese ? $"当前电量 {percent}%，已达到设定阈值。" : $"Battery is at {percent}%, at or below your threshold.",
            sample.IsBatteryCritical ? NotificationSeverity.Error : NotificationSeverity.Warning));
    }

    private async Task HandleTemperatureSampleAsync(PowerTelemetrySample sample)
    {
        if (!_featureSettings.TemperatureProtectionEnabled || _handlingTemperature ||
            sample.HighestTemperatureCelsius is not { } temperature)
            return;

        if (!_temperatureProtectionActive && temperature >= _featureSettings.TemperatureLimitCelsius)
        {
            _handlingTemperature = true;
            _temperatureProtectionActive = true;
            await RefreshRecommendationAsync();
            _temperatureRestoreMode = NormalizeMode(_featureSettings.LastMode, "balanced");
            _systemIntegration.Notify(new UserNotification(
                IsChinese ? "温度保护已触发" : "Temperature protection triggered",
                IsChinese ? $"检测到 {temperature:F1} °C，立即切换到低功耗。" : $"Detected {temperature:F1} °C; switching to Saver.",
                NotificationSeverity.Warning));
            try
            {
                var succeeded = await RunModeWithContextAsync(
                    "saver",
                    new SwitchRequestContext("temperature", $"{temperature:F1} °C ≥ {_featureSettings.TemperatureLimitCelsius:F1} °C", AllowPreview: false));
                if (!succeeded)
                {
                    _temperatureProtectionActive = false;
                    await RefreshRecommendationAsync();
                }
            }
            finally
            {
                _handlingTemperature = false;
            }
            return;
        }

        if (_temperatureProtectionActive &&
            temperature > _featureSettings.TemperatureRecoveryCelsius &&
            !string.Equals(_featureSettings.LastMode, "saver", StringComparison.OrdinalIgnoreCase) &&
            !_modeSwitchInProgress)
        {
            _handlingTemperature = true;
            try
            {
                await RunModeWithContextAsync(
                    "saver",
                    new SwitchRequestContext("temperature-enforcement", $"{temperature:F1} °C", AllowPreview: false));
            }
            finally
            {
                _handlingTemperature = false;
            }
            return;
        }

        if (_temperatureProtectionActive && temperature <= _featureSettings.TemperatureRecoveryCelsius)
        {
            _handlingTemperature = true;
            try
            {
                var restoreMode = NormalizeMode(_temperatureRestoreMode, "balanced");
                var succeeded = await RunModeWithContextAsync(
                    restoreMode,
                    new SwitchRequestContext("temperature-recovery", $"{temperature:F1} °C ≤ {_featureSettings.TemperatureRecoveryCelsius:F1} °C", AllowPreview: false));
                if (succeeded)
                {
                    _temperatureProtectionActive = false;
                    await RefreshRecommendationAsync();
                    _systemIntegration.Notify(new UserNotification(
                        IsChinese ? "温度已恢复" : "Temperature recovered",
                        IsChinese ? $"已恢复到 {restoreMode} 模式。" : $"Restored the {restoreMode} mode.",
                        NotificationSeverity.Success));
                }
            }
            finally
            {
                _handlingTemperature = false;
            }
        }
    }

    private async Task<bool> RunAutomationTickAsync()
    {
        if (!_featureSettings.AutoSwitchEnabled || _modeSwitchInProgress || _temperatureProtectionActive)
            return false;

        AutomationSnapshot snapshot;
        try
        {
            snapshot = _automationEngine.CaptureSnapshot();
        }
        catch (Exception ex)
        {
            AppendLog($"Automation snapshot: {ex.Message}");
            return false;
        }

        if (snapshot.PowerSource == AutomationPowerSource.Battery &&
            snapshot.BatteryPercent is { } battery &&
            battery <= _featureSettings.LowBatteryThreshold &&
            !string.Equals(_featureSettings.LastMode, "saver", StringComparison.OrdinalIgnoreCase))
        {
            return await RunModeWithContextAsync(
                "saver",
                new SwitchRequestContext(
                    "low-battery",
                    IsChinese ? $"电量 {battery}% ≤ {_featureSettings.LowBatteryThreshold}%" : $"Battery {battery}% ≤ {_featureSettings.LowBatteryThreshold}%",
                    AllowPreview: false));
        }

        var rules = _featureSettings.Rules.Count > 0
            ? _featureSettings.Rules
            : AutomationEngine.CreateDefaultRules(_featureSettings.RemoteProcesses, _featureSettings.PerformanceProcesses);
        var decision = _automationEngine.Evaluate(rules, _featureSettings.LastMode, snapshot);
        if (decision is null || !decision.RequiresSwitch)
            return false;

        return await RunModeWithContextAsync(
            decision.TargetMode,
            new SwitchRequestContext(
                "automation",
                decision.Reason,
                decision.RuleId,
                decision.RuleName,
                AllowPreview: false));
    }

    private Task<bool> RunModeWithContextAsync(string mode, SwitchRequestContext context, params string[] extraArguments)
    {
        var arguments = new string[1 + extraArguments.Length];
        arguments[0] = mode;
        Array.Copy(extraArguments, 0, arguments, 1, extraArguments.Length);
        return RunModeCoreAsync(arguments, context);
    }

    private async Task<bool> RunModeCoreAsync(string[] args, SwitchRequestContext context)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            return false;
        if (_customProfileInProgress)
        {
            StatusText.Text = IsChinese ? "自定义预设正在应用，请稍候" : "A custom profile is still being applied";
            StatusBar.Severity = InfoBarSeverity.Warning;
            return false;
        }

        var targetMode = NormalizeMode(args[0], args[0]);
        args[0] = targetMode;
        if (context.AllowPreview &&
            string.Equals(context.Trigger, "manual", StringComparison.OrdinalIgnoreCase) &&
            _featureSettings.PreviewManualSwitches)
        {
            var preview = IsChinese
                ? $"将立即切换到“{GetModeDisplayName(targetMode)}”。\n\n核心电源方案会先完成切换，CPU、亮度等细项随后在后台同步。"
                : $"Switch to “{GetModeDisplayName(targetMode)}” now?\n\nThe core power plan changes first; CPU, brightness and other details sync in the background.";
            if (!await ConfirmAsync(IsChinese ? "切换预览" : "Switch preview", preview))
                return false;
        }

        var generation = Interlocked.Increment(ref _modeSwitchGeneration);
        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _modeSwitchCancellation, cancellation);
        try { previousCancellation?.Cancel(); } catch (ObjectDisposedException) { }

        var previousPlan = GetActivePlanGuidFast();
        var previousMode = NormalizeMode(_featureSettings.LastMode, "unknown");
        var stopwatch = Stopwatch.StartNew();
        var activated = false;
        var succeeded = false;
        string? error = null;
        _modeSwitchInProgress = true;
        RenderRecommendation();
        BusyProgress.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        StatusText.Text = IsChinese
            ? $"正在切换到“{GetModeDisplayName(targetMode)}”…"
            : $"Switching to “{GetModeDisplayName(targetMode)}”…";
        StatusBar.Severity = InfoBarSeverity.Informational;

        try
        {
            await _activationGate.WaitAsync();
            try
            {
                if (generation != Volatile.Read(ref _modeSwitchGeneration))
                {
                    error = "Superseded by a newer switch request.";
                    return false;
                }
                activated = await ActivatePlanImmediatelyAsync(targetMode);
            }
            finally
            {
                _activationGate.Release();
            }

            if (activated)
            {
                ApplyOptimisticMode(args);
                StatusText.Text = IsChinese ? "核心模式已切换，正在后台同步设置…" : "Core mode switched; syncing settings in the background…";
                StatusBar.Severity = InfoBarSeverity.Informational;
            }

            var result = await RunCliAsync(args, manageBusy: false, cancellation.Token, updateStatus: false);
            if (generation != Volatile.Read(ref _modeSwitchGeneration) || result.ExitCode == -2)
            {
                error = "Superseded by a newer switch request.";
                return false;
            }

            succeeded = result.ExitCode == 0;
            error = succeeded ? null : FirstNonEmpty(result.Error, result.Output, "PowerModeSwitcher failed.");
            if (succeeded)
            {
                if (!activated)
                    ApplyOptimisticMode(args);
                _featureSettings.LastMode = targetMode;
                try
                {
                    SettingsStore.Save(_featureSettings);
                    _ = BackupSettingsIfChangedAsync(_featureSettings.ConfigurationBackupCount, "mode-switch");
                }
                catch (Exception settingsError)
                {
                    AppendLog($"Settings save: {settingsError.Message}");
                }
                StatusText.Text = IsChinese
                    ? $"已切换：{GetModeDisplayName(targetMode)} · {stopwatch.Elapsed.TotalSeconds:0.0} 秒"
                    : $"Switched: {GetModeDisplayName(targetMode)} · {stopwatch.Elapsed.TotalSeconds:0.0}s";
                StatusBar.Severity = InfoBarSeverity.Success;
                _systemIntegration.Notify(new UserNotification(
                    IsChinese ? "电源模式已切换" : "Power mode changed",
                    GetModeDisplayName(targetMode),
                    NotificationSeverity.Success));
            }
            else
            {
                await RestorePlanAsync(previousPlan);
                StatusText.Text = IsChinese ? "切换失败，已恢复之前的电源方案" : "Switch failed; previous plan restored";
                StatusBar.Severity = InfoBarSeverity.Error;
            }
            return succeeded;
        }
        catch (OperationCanceledException)
        {
            error = "Cancelled";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (generation == Volatile.Read(ref _modeSwitchGeneration))
            {
                await RestorePlanAsync(previousPlan);
                StatusText.Text = IsChinese ? "切换失败，已恢复之前的电源方案" : "Switch failed; previous plan restored";
                StatusBar.Severity = InfoBarSeverity.Error;
            }
            AppendLog(ex.ToString());
            return false;
        }
        finally
        {
            stopwatch.Stop();
            if (_featureSettings.OperationHistoryEnabled)
            {
                await RecordSwitchHistoryAsync(new SwitchHistoryEntry
                {
                    Timestamp = DateTimeOffset.Now,
                    OperationKind = "mode-switch",
                    PreviousMode = previousMode,
                    TargetMode = targetMode,
                    Trigger = context.Trigger,
                    Reason = context.Reason,
                    RuleId = context.RuleId,
                    RuleName = context.RuleName,
                    Succeeded = succeeded,
                    DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                    ErrorMessage = error
                });
            }

            if (generation == Volatile.Read(ref _modeSwitchGeneration))
            {
                _modeSwitchInProgress = false;
                Interlocked.CompareExchange(ref _modeSwitchCancellation, null, cancellation);
                BusyProgress.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                RenderRecommendation();
                if (succeeded)
                    _ = RefreshStatusAfterModeSwitchAsync(generation);
            }
            cancellation.Dispose();
        }
    }

    private async Task RefreshStatusAfterModeSwitchAsync(long generation)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var result = await RunCliAsync(["status"], manageBusy: false, timeout.Token, updateStatus: false);
            if (result.ExitCode == 0 &&
                generation == Volatile.Read(ref _modeSwitchGeneration) &&
                !_modeSwitchInProgress)
                ApplyStatus(result.Output);
        }
        catch (Exception ex)
        {
            AppendLog($"Status refresh after mode switch: {ex.Message}");
        }
    }

    private async Task RecordSwitchHistoryAsync(SwitchHistoryEntry entry)
    {
        try
        {
            await HistoryStore.Default.RecordAsync(entry);
            if (DateTimeOffset.Now.Minute == 0)
                await HistoryStore.Default.TrimAsync();
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => AppendLog($"Switch history: {ex.Message}"));
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string NormalizeMode(string? mode, string fallback)
    {
        var normalized = mode?.Trim().ToLowerInvariant();
        return normalized is "remote" or "saver" or "balanced" or "high" ? normalized : fallback;
    }

    private string GetModeDisplayName(string mode) => mode switch
    {
        "remote" => T("ModeRemote"),
        "saver" => T("ModeSaver"),
        "balanced" => T("ModeBalanced"),
        "high" => T("ModeHigh"),
        _ => mode
    };

    private RecommendationContext CreateRecommendationContext()
    {
        RecommendationPowerState powerState;
        try
        {
            var succeeded = Native.GetSystemPowerStatus(out var power);
            powerState = RecommendationUiLogic.CreatePowerState(
                succeeded,
                power.ACLineStatus,
                power.BatteryLifePercent);
        }
        catch
        {
            powerState = new RecommendationPowerState(null, null);
        }

        var runningProcessNames = new List<string>();
        var runningProcessesAvailable = true;
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            processes = [];
            runningProcessesAvailable = false;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    runningProcessNames.Add(process.ProcessName);
                }
                catch
                {
                    // Processes can exit while their names are being read.
                }
            }
        }

        return RecommendationUiLogic.CreateContext(
            _featureSettings,
            _hardwareCapabilities,
            _temperatureProtectionActive,
            powerState,
            runningProcessNames,
            DateTimeOffset.Now,
            runningProcessesAvailable);
    }

    private Task RefreshRecommendationAsync()
    {
        var context = CreateRecommendationContext();
        if (!RecommendationUiLogic.NeedsRefresh(_recommendationContext, context))
            return Task.CompletedTask;

        _recommendationContext = context;
        _currentRecommendation = ModeRecommendationService.Recommend(context);
        RenderRecommendation();
        return Task.CompletedTask;
    }

    private void RenderRecommendation()
    {
        if (_currentRecommendation is not { } recommendation ||
            RecommendationTitle is null ||
            RecommendationReason is null ||
            ApplyRecommendationButton is null)
            return;

        var presentation = RecommendationUiLogic.CreatePresentation(
            recommendation,
            GetModeDisplayName(recommendation.Mode),
            IsChinese);
        var applyState = RecommendationUiLogic.CreateApplyButtonState(
            recommendation,
            _featureSettings.LastMode,
            _recommendationApplyGate.IsEntered || _modeSwitchInProgress,
            IsChinese);
        RecommendationTitle.Text = presentation.Title;
        RecommendationReason.Text = presentation.Reason;
        ApplyRecommendationButton.Content = applyState.Text;
        ApplyRecommendationButton.IsEnabled = applyState.IsEnabled;
        AutomationProperties.SetName(ApplyRecommendationButton, applyState.Text);
        AutomationProperties.SetHelpText(
            ApplyRecommendationButton,
            presentation.AutomationHelpText);
    }

    private async void ApplyRecommendationButton_Click(
        object sender,
        Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_currentRecommendation is not { } recommendation)
            return;

        var state = RecommendationUiLogic.CreateApplyButtonState(
            recommendation,
            _featureSettings.LastMode,
            _recommendationApplyGate.IsEntered || _modeSwitchInProgress,
            IsChinese);
        if (!state.IsEnabled)
            return;

        try
        {
            await _recommendationApplyGate.TryRunAsync(async () =>
            {
                RenderRecommendation();
                var presentation = RecommendationUiLogic.CreatePresentation(
                    recommendation,
                    GetModeDisplayName(recommendation.Mode),
                    IsChinese);
                var request = RecommendationUiLogic.CreateApplyRequest(
                    recommendation,
                    presentation.Reason);
                await RunModeWithContextAsync(
                    request.Mode,
                    new SwitchRequestContext(
                        request.Trigger,
                        request.Reason,
                        AllowPreview: request.AllowPreview));
            });
        }
        finally
        {
            RenderRecommendation();
        }
    }

    private void InsightsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_insightsWindow is not null)
        {
            _insightsWindow.Activate();
            return;
        }

        _insightsWindow = new InsightsWindow(
            _monitoringService,
            _systemIntegration,
            IsChinese,
            () => LogBox.Text);
        _insightsWindow.Closed += (_, _) => _insightsWindow = null;
        _insightsWindow.Activate();
    }

    private void DisposeAdvancedFeatures()
    {
        if (!_advancedFeaturesInitialized)
            return;

        _advancedFeaturesInitialized = false;
        try { _modeSwitchCancellation?.Cancel(); } catch { }
        try { _insightsWindow?.Close(); } catch { }
        _insightsWindow = null;
        _monitoringService.SampleAvailable -= MonitoringService_SampleAvailable;
        _monitoringService.SamplingFailed -= MonitoringService_SamplingFailed;
        _ = _monitoringService.DisposeAsync();
        _systemIntegration.Dispose();
    }
}
