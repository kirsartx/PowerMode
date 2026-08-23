using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace PowerModeWinUI;

public sealed partial class MainWindow
{
    private readonly AutomationEngine _automationEngine = new();
    private readonly MonitoringService _monitoringService;
    private readonly SystemIntegrationService _systemIntegration = new(
        "PowerMode",
        dataDirectory: SettingsStore.DirectoryPath);
    private readonly SemaphoreSlim _monitoringConfigurationGate = new(1, 1);
    private PowerTelemetrySample? _lastTelemetry;
    private InsightsWindow? _insightsWindow;
    private RecoveryCenterWindow? _recoveryCenterWindow;
    private RecoveryService? _recoveryService;
    private readonly CancellationTokenSource _recoveryLifetimeCancellation = new();
    private string? _pendingMode;
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
        bool AllowPreview = true,
        bool RecordHistory = true)
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
    internal CancellationToken RecoveryLifetimeToken => _recoveryLifetimeCancellation.Token;

    private async Task RunStartupFeaturesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanPersistSettings)
            return;
        if (!_featureSettings.CheckUpdatesOnStartup || string.IsNullOrWhiteSpace(_featureSettings.UpdateApiUrl))
            return;

        await CheckForUpdatesAndNotifyAsync(
            _featureSettings.UpdateApiUrl,
            silentWhenCurrent: true);
    }

    private async Task ApplyLastModeOnStartupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_featureSettings.ApplyLastModeOnStartup ||
            string.IsNullOrWhiteSpace(_featureSettings.LastMode))
            return;
        await RunModeWithContextAsync(
            _featureSettings.LastMode,
            new SwitchRequestContext(
                "startup",
                AllowPreview: false));
    }

    internal void ApplySystemSettings(PowerModeSettings settings)
    {
        if (!CanPersistSettings)
            return;
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

    internal RecoveryBackupAvailability GetLatestDistinctSettingsBackup() =>
        RecoveryBackupSelector.FindLatestDistinct(
            _systemIntegration.ListConfigurationBackups(),
            SettingsStore.FilePath);

    internal Task<RecoveryActionResult> RestoreSettingsBackupAsync(
        ConfigurationBackupInfo backup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return GetRecoveryService().RestoreConfigurationAsync(
            backup.Path,
            token => _systemIntegration.RestoreConfigurationBackupAsync(
                backup.Path,
                SettingsStore.FilePath,
                createSafetyBackup: true,
                token),
            token =>
                AcceptRecoveredSettingsAndResumeStartupAsync(
                    SettingsStore.Load(),
                    token),
            async (restoreResult, token) =>
            {
                var safetyBackup = restoreResult.SafetyBackup
                    ?? throw new InvalidOperationException(
                        "A safety backup is required to roll back configuration restore.");
                var rollback = await _systemIntegration.RestoreConfigurationBackupAsync(
                    safetyBackup.Path,
                    SettingsStore.FilePath,
                    createSafetyBackup: false,
                    token);
                if (!rollback.Succeeded)
                    throw new IOException(rollback.Error ?? "Configuration rollback failed.");
                await AcceptRecoveredSettingsAndResumeStartupAsync(
                    SettingsStore.Load(),
                    token);
            },
            cancellationToken);
    }

    internal Task<LastOperationAvailability> GetLastOperationAvailabilityAsync(
        CancellationToken cancellationToken) =>
        GetRecoveryService().GetLastOperationAvailabilityAsync(cancellationToken);

    internal async Task<LastOperationVerificationResult> VerifyLastOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var result = await GetRecoveryService().VerifyLastOperationAsync(
            operationId,
            cancellationToken);
        if (result.MatchesCriticalExpectations)
        {
            ClearPersistentRecoveryPresentation();
            await ResumeStartupAfterRecoveryAsync(cancellationToken);
        }
        return result;
    }

    internal async Task<RecoveryActionResult> RestoreBeforeStateAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var result = await GetRecoveryService().RestoreBeforeStateAsync(
            operationId,
            cancellationToken);
        if (result.Succeeded)
        {
            ClearPersistentRecoveryPresentation();
            await ResumeStartupAfterRecoveryAsync(cancellationToken);
        }
        return result;
    }

    private async Task ResumeStartupAfterRecoveryAsync(
        CancellationToken cancellationToken = default)
    {
        await _startupCoordinator.ResumeAfterRecoveryAsync(cancellationToken);
        _startupActivationDeferred = false;
        await RefreshStatusAsync();
    }

    private async Task AcceptRecoveredSettingsAndResumeStartupAsync(
        SettingsLoadResult recovered,
        CancellationToken cancellationToken)
    {
        await _recoveredSettingsActivationFlow.RunAsync(
            recovered,
            cancellationToken);
    }

    internal Task<RecoveryActionResult> ResetSettingsDefaultsAsync(
        CancellationToken cancellationToken) =>
        GetRecoveryService().ResetDefaultsAsync(
            token => AcceptRecoveredSettingsAndResumeStartupAsync(
                SettingsStore.Load(),
                token),
            cancellationToken);

    private RecoveryService GetRecoveryService() =>
        _recoveryService ?? throw new InvalidOperationException(
            "Recovery services are not initialized.");

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
        var targetMode = NormalizeMode(args[0], string.Empty);
        if (targetMode.Length == 0)
            return false;
        var preset = targetMode switch
        {
            "remote" => PowerModePreset.Remote,
            "saver" => PowerModePreset.Saver,
            "balanced" => PowerModePreset.Balanced,
            "high" => PowerModePreset.High,
            _ => throw new ArgumentOutOfRangeException(nameof(args))
        };
        var cpuMaximumPercent = args.Length > 1 && int.TryParse(args[1], out var cpu)
            ? cpu
            : (int?)null;
        var disableWifi = args.Skip(1).Any(argument =>
            string.Equals(argument, "nowifi", StringComparison.OrdinalIgnoreCase));
        return await RunTargetCoreAsync(
            PowerModeTarget.ForPreset(preset),
            cpuMaximumPercent,
            disableWifi,
            context);
    }

    private async Task<bool> RunTargetCoreAsync(
        PowerModeTarget target,
        int? cpuMaximumPercent,
        bool disableWifi,
        SwitchRequestContext context,
        CustomPowerProfile? customProfile = null)
    {
        var attempt = await _startupMutationGate.TryRunAsync(() =>
            RunTargetAfterStartupAsync(
                target,
                cpuMaximumPercent,
                disableWifi,
                context,
                customProfile));
        if (!attempt.Allowed)
        {
            PresentStartupRecovery(new(
                true,
                null,
                null,
                IsChinese
                    ? "请先在恢复中心验证或恢复上次操作。"
                    : "Verify or restore the last operation in Recovery Center first."));
            return false;
        }
        return attempt.Value;
    }

    private async Task<bool> RunTargetAfterStartupAsync(
        PowerModeTarget target,
        int? cpuMaximumPercent,
        bool disableWifi,
        SwitchRequestContext context,
        CustomPowerProfile? customProfile)
    {
        if (_modeSwitchInProgress)
        {
            StatusText.Text = IsChinese
                ? "另一项电源操作正在进行。"
                : "Another power operation is in progress.";
            StatusBar.Severity = InfoBarSeverity.Warning;
            return false;
        }

        var targetMode = target.Preset?.ToString().ToLowerInvariant() ?? target.Key;
        _pendingMode = target.Preset?.ToString().ToLowerInvariant();
        if (context.AllowPreview &&
            string.Equals(context.Trigger, "manual", StringComparison.OrdinalIgnoreCase) &&
            _featureSettings.PreviewManualSwitches)
        {
            var preview = IsChinese
                ? $"切换到“{GetModeDisplayName(targetMode)}”并在完成后验证实际状态？"
                : $"Switch to “{GetModeDisplayName(targetMode)}” and verify the resulting state?";
            if (!await ConfirmAsync(IsChinese ? "切换预览" : "Switch preview", preview))
            {
                _pendingMode = null;
                return false;
            }
        }

        _modeSwitchInProgress = true;
        RenderRecommendation();
        BusyProgress.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        StatusText.Text = IsChinese
            ? $"正在切换到“{GetModeDisplayName(targetMode)}”…"
            : $"Switching to “{GetModeDisplayName(targetMode)}”…";
        StatusBar.Severity = InfoBarSeverity.Informational;

        try
        {
            var result = await _modeSwitchCoordinator.TrySwitchAsync(new ModeSwitchRequest(
                Guid.NewGuid(),
                target,
                cpuMaximumPercent,
                disableWifi,
                context.Trigger,
                context.Reason,
                context.RuleId,
                context.RuleName,
                _featureSettings.OperationHistoryEnabled && context.RecordHistory));
            if (result is null)
            {
                StatusText.Text = IsChinese
                    ? "电源操作被拒绝：另一操作正在进行或需要恢复。"
                    : "Power operation rejected: another operation is active or recovery is required.";
                StatusBar.Severity = InfoBarSeverity.Warning;
                return false;
            }

            AppendLog(result.DiagnosticSummary);
            PresentModeSwitchResult(result);
            var succeeded = result.Outcome is
                ModeSwitchOutcome.Succeeded or ModeSwitchOutcome.Partial;
            if (succeeded)
            {
                if (result.AfterState is { } afterState)
                    ApplyPowerModeState(afterState);
                if (customProfile is not null)
                {
                    _lastCustomProfile = customProfile;
                    _activeModeKey = target.Key;
                    ModeValue.Text = customProfile.Name;
                    RenderRecommendation();
                    _featureSettings.LastMode = "saver";
                }
                else
                {
                    _featureSettings.LastMode = targetMode;
                }
                try
                {
                    TrySaveSettings(_featureSettings);
                    _ = BackupSettingsIfChangedAsync(_featureSettings.ConfigurationBackupCount, "mode-switch");
                }
                catch (Exception settingsError)
                {
                    AppendLog($"Settings save: {settingsError.Message}");
                }
                _systemIntegration.Notify(new UserNotification(
                    IsChinese ? "电源模式已切换" : "Power mode changed",
                    customProfile?.Name ?? GetModeDisplayName(targetMode),
                    NotificationSeverity.Success));
            }
            else if (result.Rollback?.Succeeded == true && result.BeforeState is { } beforeState)
            {
                ApplyPowerModeState(beforeState);
            }
            return succeeded;
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
            StatusText.Text = ex.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            return false;
        }
        finally
        {
            _pendingMode = null;
            _modeSwitchInProgress = false;
            BusyProgress.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            RenderRecommendation();
        }
    }

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
        var activeProjection=ProjectActiveModePresentation();
        ApplyModeButtonPresentation(activeProjection.ModeButtons);
        if (_currentRecommendation is not { } recommendation ||
            activeProjection.Recommendation is not { } applyState ||
            RecommendationTitle is null ||
            RecommendationReason is null ||
            ApplyRecommendationButton is null)
            return;

        var presentation = RecommendationUiLogic.CreatePresentation(
            recommendation,
            GetModeDisplayName(recommendation.Mode),
            IsChinese);
        RecommendationTitle.Text = presentation.Title;
        RecommendationReason.Text = presentation.Reason;
        ApplyRecommendationButton.Content = applyState.Text;
        ApplyRecommendationButton.IsEnabled = applyState.IsEnabled;
        AutomationProperties.SetName(ApplyRecommendationButton, applyState.Text);
        AutomationProperties.SetItemStatus(
            ApplyRecommendationButton,
            applyState.IsEnabled ? string.Empty : applyState.Text);
        AutomationProperties.SetHelpText(
            ApplyRecommendationButton,
            presentation.AutomationHelpText);
    }

    private ActiveModePresentationState ProjectActiveModePresentation()=>
        ActiveModePresentation.Project(
            _activeModeKey,
            _pendingMode,
            _modeSwitchInProgress,
            _recommendationApplyGate.IsEntered,
            _currentRecommendation,
            new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                ["remote"]=T("ModeRemote"),
                ["saver"]=T("ModeSaver"),
                ["balanced"]=T("ModeBalanced"),
                ["high"]=T("ModeHigh")
            },
            IsChinese);

    private async void ApplyRecommendationButton_Click(
        object sender,
        Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_currentRecommendation is not { } recommendation)
            return;

        var state=ProjectActiveModePresentation().Recommendation;
        if (state is null || !state.IsEnabled)
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

    private void OpenRecoveryCenterButton_Click(
        object sender,
        Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_recoveryCenterWindow is not null)
        {
            _recoveryCenterWindow.Activate();
            return;
        }

        var window = new RecoveryCenterWindow(this, IsChinese);
        _recoveryCenterWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_recoveryCenterWindow, window))
                _recoveryCenterWindow = null;
        };
        window.Activate();
    }

    private void DisposeAdvancedFeatures()
    {
        if (!_advancedFeaturesInitialized)
            return;

        _advancedFeaturesInitialized = false;
        _recoveryLifetimeCancellation.Cancel();
        try { _insightsWindow?.Close(); } catch { }
        _insightsWindow = null;
        try { _recoveryCenterWindow?.Close(); } catch { }
        _recoveryCenterWindow = null;
        var recoveryService = _recoveryService;
        _recoveryService = null;
        _monitoringService.SampleAvailable -= MonitoringService_SampleAvailable;
        _monitoringService.SamplingFailed -= MonitoringService_SamplingFailed;
        _ = DisposeAdvancedResourcesWhenRecoveryIdleAsync(recoveryService);
    }

    private async Task DisposeAdvancedResourcesWhenRecoveryIdleAsync(
        RecoveryService? recoveryService)
    {
        try
        {
            if (recoveryService is not null)
                await recoveryService.WaitForIdleAsync();
        }
        catch
        {
            // Shutdown still needs to release owned resources.
        }
        await _monitoringService.DisposeAsync();
        _systemIntegration.Dispose();
        _recoveryLifetimeCancellation.Dispose();
    }
}
