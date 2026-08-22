using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using System.Globalization;
using Windows.Graphics;

namespace PowerModeWinUI;

public sealed partial class MainWindow : Window
{
    private readonly string _cliPath;
    private readonly string _enginePath;
    private readonly IProcessRunner _processRunner;
    private readonly IPowerModeBackend _powerModeBackend;
    private readonly ILastOperationStore _lastOperationStore;
    private readonly IModeSwitchCoordinator _modeSwitchCoordinator;
    private readonly ISettingsActivationCoordinator _settingsActivationCoordinator;
    private readonly IStartupCoordinator _startupCoordinator;
    private string _language = "zh";
    private bool _syncingCpu;
    private readonly bool _startHidden;
    private bool _startupActivationDeferred;
    private PowerModeState? _startupPowerState;
    private ModeSwitchResult? _lastModeSwitchResult;
    private ModeSwitchPresentationAction _statusAction;
    private bool _persistentModeSwitchPresentation;
    private string _activeModeKey = "unknown";

    private readonly Dictionary<string, Dictionary<string, string>> _texts = new()
    {
        ["zh"] = new()
        {
            ["Subtitle"]="Hermes 远程电源切换器", ["Refresh"]="刷新", ["Language"]="English", ["Features"]="功能中心", ["Insights"]="洞察", ["RecoveryCenter"]="恢复", ["ExperienceModeAutomation"]="切换简单或专业模式",
            ["LastUpdated"]="更新于 {0}", ["Auto"]="自动", ["Live"]="监控",
            ["Mode"]="当前模式", ["Gpu"]="独显功耗", ["Power"]="供电", ["Cpu"]="CPU 上限", ["Brightness"]="亮度", ["Sleep"]="关屏 / 睡眠",
            ["Modes"]="模式", ["ModesHint"]="选择预设方案，核心电源方案会立即切换。", ["Remote"]="远程推荐", ["Saver"]="低功耗", ["Balanced"]="平衡", ["High"]="高性能",
            ["RemoteDesc"]="远程连接", ["SaverDesc"]="安静省电", ["BalancedDesc"]="日常使用", ["HighDesc"]="重负载",
            ["CustomCpu"]="远程自定义 CPU", ["CpuHint"]="20–50%，拖动滑杆或输入数值", ["RemoteCustom"]="应用远程自定义 CPU",
            ["Advanced"]="高级操作", ["Verify"]="校验当前模式", ["Repair"]="一键修复", ["WifiOn"]="恢复 WiFi", ["RemoteNoWifi"]="远程 + 关闭 WiFi",
            ["Log"]="日志", ["LogHint"]="实时显示本次会话的命令输出", ["AutoScroll"]="自动滚动", ["LineCount"]="{0} 行", ["Copy"]="复制", ["Clear"]="清空", ["Ready"]="就绪",
            ["Running"]="执行中：{0}", ["Done"]="完成：{0} · {1:0.0} 秒", ["Failed"]="失败：{0} · {1:0.0} 秒", ["RefreshTimeout"]="刷新超时，请稍后重试", ["CliPath"]="CLI：{0}", ["Unknown"]="未知",
            ["CliMissing"]="找不到 PowerModeSwitcher.bat。请把 GUI 与 CLI 放在同一目录树。", ["ConfirmNoWifiTitle"]="确认关闭 WiFi", ["ConfirmNoWifi"]="这可能中断远程连接。仅在网线已连接且远程访问不依赖 WiFi 时继续。",
            ["Confirm"]="继续", ["Cancel"]="取消", ["Copied"]="日志已复制到剪贴板", ["ModeRemote"]="远程推荐", ["ModeSaver"]="低功耗", ["ModeBalanced"]="平衡", ["ModeHigh"]="高性能"
        },
        ["en"] = new()
        {
            ["Subtitle"]="Hermes remote power switcher", ["Refresh"]="Refresh", ["Language"]="中文", ["Features"]="Features", ["Insights"]="Insights", ["RecoveryCenter"]="Recovery", ["ExperienceModeAutomation"]="Switch between Simple and Professional modes",
            ["LastUpdated"]="Updated {0}", ["Auto"]="Auto", ["Live"]="Live",
            ["Mode"]="Current mode", ["Gpu"]="dGPU power", ["Power"]="Power", ["Cpu"]="CPU max", ["Brightness"]="Brightness", ["Sleep"]="Display / sleep",
            ["Modes"]="Modes", ["ModesHint"]="Choose a preset. The core Windows plan switches immediately.", ["Remote"]="Remote", ["Saver"]="Saver", ["Balanced"]="Balanced", ["High"]="High",
            ["RemoteDesc"]="Remote access", ["SaverDesc"]="Quiet & efficient", ["BalancedDesc"]="Everyday use", ["HighDesc"]="Heavy workloads",
            ["CustomCpu"]="Remote custom CPU", ["CpuHint"]="20–50%; drag or enter a value", ["RemoteCustom"]="Apply custom remote CPU",
            ["Advanced"]="ADVANCED", ["Verify"]="Verify current", ["Repair"]="Repair", ["WifiOn"]="Restore WiFi", ["RemoteNoWifi"]="Remote + WiFi off",
            ["Log"]="Log", ["LogHint"]="Live command output from this session", ["AutoScroll"]="Auto scroll", ["LineCount"]="{0} lines", ["Copy"]="Copy", ["Clear"]="Clear", ["Ready"]="Ready",
            ["Running"]="Running: {0}", ["Done"]="Done: {0} · {1:0.0}s", ["Failed"]="Failed: {0} · {1:0.0}s", ["RefreshTimeout"]="Refresh timed out. Please try again", ["CliPath"]="CLI: {0}", ["Unknown"]="Unknown",
            ["CliMissing"]="PowerModeSwitcher.bat was not found. Keep the GUI and CLI in the same folder tree.", ["ConfirmNoWifiTitle"]="Confirm WiFi off", ["ConfirmNoWifi"]="This may disconnect remote access. Continue only with Ethernet connected and remote access independent of WiFi.",
            ["Confirm"]="Continue", ["Cancel"]="Cancel", ["Copied"]="Log copied to clipboard", ["ModeRemote"]="Remote", ["ModeSaver"]="Saver", ["ModeBalanced"]="Balanced", ["ModeHigh"]="High performance"
        }
    };

    public MainWindow(bool startHidden = false)
    {
        _startHidden = startHidden;
        _settingsLoadResult = SettingsStore.Load();
        _featureSettings = _settingsLoadResult.Settings;
        InitializeComponent();
        ConfigureWindow();
        _cliPath = FindCliPath();
        _enginePath = FindEnginePath();
        _processRunner = new ProcessRunner();
        _powerModeBackend = new PowerModeBackend(_processRunner, _enginePath);
        _lastOperationStore = new LastOperationStore();
        _modeSwitchCoordinator = new ModeSwitchCoordinator(
            _powerModeBackend,
            _lastOperationStore,
            HistoryStore.Default,
            TimeProvider.System);
        _settingsActivationCoordinator = new SettingsActivationCoordinator(
            _settingsLoadResult,
            new MainWindowSettingsActivationEffects(this));
        _startupCoordinator = new StartupCoordinator(
            _lastOperationStore,
            _powerModeBackend,
            (load, token) => _settingsActivationCoordinator.ActivateAsync(
                SettingsActivationPolicy.For(load.State),
                token));
        _recoveryService = new RecoveryService(
            _lastOperationStore,
            _modeSwitchCoordinator,
            HistoryStore.Default,
            new ProductionRecoveryBackend(
                _systemIntegration,
                () => _featureSettings.ConfigurationBackupCount));
        _language = ReadLanguage();
        ApplyLanguage();
        InitializeFeatures();
        Activated += MainWindow_Activated;
    }

    private bool _firstActivation = true;
    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (!_firstActivation) return;
        _firstActivation = false;
        if (_startHidden) AppWindow.Hide();
        try
        {
            var initialization = await _startupCoordinator.InitializeAsync(
                _settingsLoadResult);
            _startupActivationDeferred = initialization.ActivationDeferred;
            if (initialization.CurrentState is { } currentState)
            {
                _startupPowerState ??= currentState;
                ApplyPowerModeState(currentState);
            }

            if (initialization.ActivationDeferred)
            {
                PresentStartupRecovery(initialization);
                return;
            }

            await RefreshStatusAsync();
            _ = DetectCapabilitiesAndRefreshPresentationAsync();
        }
        catch (Exception exception)
        {
            PresentStartupRecovery(new(
                true,
                null,
                null,
                exception.Message));
        }
    }

    private void ConfigureWindow()
    {
        if(!DpiAwareWindowSizer.TryRestore(this,960,650))
            DpiAwareWindowSizer.Resize(this,1120,760,960,650,center:true);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var layout=ResponsiveLayoutPolicy.Evaluate(e.NewSize.Width,_featureSettings.ExperienceMode);
        RootGrid.Padding=layout.Tier switch
        {
            LayoutTier.Narrow=>new Thickness(16,14,16,14),
            LayoutTier.Medium=>new Thickness(20,18,20,18),
            _=>new Thickness(28,22,28,22)
        };
        var visibility=layout.Tier==LayoutTier.Narrow?Visibility.Collapsed:Visibility.Visible;
        AutoQuickText.Visibility=visibility;LiveQuickText.Visibility=visibility;RefreshButtonText.Visibility=visibility;
        FeaturesButtonText.Visibility=visibility;InsightsButtonText.Visibility=visibility;RecoveryCenterButtonText.Visibility=visibility;
    }

    internal string T(string key) => _texts[_language].TryGetValue(key, out var value) ? value : key;
    internal bool IsChinese => _language == "zh";

    private void ApplyLanguage()
    {
        SubtitleText.Text=T("Subtitle"); RefreshButtonText.Text=T("Refresh"); FeaturesButtonText.Text=T("Features");InsightsButtonText.Text=T("Insights");RecoveryCenterButtonText.Text=T("RecoveryCenter"); LanguageButton.Content=T("Language");LastUpdatedText.Text=string.Format(T("LastUpdated"),"—");
        AutoQuickText.Text=T("Auto");LiveQuickText.Text=T("Live");
        AutomationProperties.SetName(ExperienceModeButton,T("ExperienceModeAutomation"));AutomationProperties.SetName(RefreshButton,T("Refresh"));AutomationProperties.SetName(FeaturesButton,T("Features"));AutomationProperties.SetName(InsightsButton,T("Insights"));AutomationProperties.SetName(RecoveryCenterButton,T("RecoveryCenter"));
        ModeTitle.Text=T("Mode"); GpuTitle.Text=T("Gpu"); PowerTitle.Text=T("Power"); CpuTitle.Text=T("Cpu"); BrightnessTitle.Text=T("Brightness"); SleepTitle.Text=T("Sleep");
        ModesTitle.Text=T("Modes"); ModesHintText.Text=T("ModesHint"); RemoteButtonText.Text=T("Remote"); SaverButtonText.Text=T("Saver"); BalancedButtonText.Text=T("Balanced"); HighButtonText.Text=T("High");
        RemoteDescription.Text=T("RemoteDesc");SaverDescription.Text=T("SaverDesc");BalancedDescription.Text=T("BalancedDesc");HighDescription.Text=T("HighDesc");
        AutomationProperties.SetName(RemoteButton,T("Remote"));AutomationProperties.SetHelpText(RemoteButton,T("RemoteDesc"));
        AutomationProperties.SetName(SaverButton,T("Saver"));AutomationProperties.SetHelpText(SaverButton,T("SaverDesc"));
        AutomationProperties.SetName(BalancedButton,T("Balanced"));AutomationProperties.SetHelpText(BalancedButton,T("BalancedDesc"));
        AutomationProperties.SetName(HighButton,T("High"));AutomationProperties.SetHelpText(HighButton,T("HighDesc"));
        AutomationProperties.SetName(AutoQuickToggle,T("Auto"));AutomationProperties.SetName(LiveQuickToggle,T("Live"));
        CustomCpuTitle.Text=T("CustomCpu"); CpuHintText.Text=T("CpuHint"); RemoteCustomButton.Content=T("RemoteCustom"); AdvancedTitle.Text=T("Advanced");
        VerifyButton.Content=T("Verify"); RepairButton.Content=T("Repair"); WifiOnButton.Content=T("WifiOn"); RemoteNoWifiButton.Content=T("RemoteNoWifi");
        LogTitle.Text=T("Log"); LogHintText.Text=T("LogHint"); AutoScrollText.Text=T("AutoScroll"); CopyLogText.Text=T("Copy"); ClearLogText.Text=T("Clear"); StatusText.Text=T("Ready"); CliPathText.Text=string.Format(T("CliPath"),_cliPath); UpdateLogStats();
        LogBox.FontFamily=new FontFamily(_language=="zh"?"Microsoft YaHei UI":"Cascadia Mono");
        LogBox.FontSize=_language=="zh"?13.5:14;
        ToolTipService.SetToolTip(RefreshButton, _language == "zh" ? "重新读取状态（F5）" : "Reload status (F5)");
        ToolTipService.SetToolTip(AutoQuickToggle,_language=="zh"?"根据电源和运行程序自动切换":"Switch automatically based on power and running apps");
        ToolTipService.SetToolTip(LiveQuickToggle,_language=="zh"?"定时刷新硬件与电源状态":"Refresh hardware and power status periodically");
        ToolTipService.SetToolTip(FeaturesButton,_language=="zh"?"打开自动化、托盘和保护设置":"Open automation, tray and protection settings");
        ToolTipService.SetToolTip(InsightsButton,_language=="zh"?"查看硬件监控和系统洞察":"View hardware monitoring and system insights");
        ToolTipService.SetToolTip(RecoveryCenterButton,_language=="zh"?"撤销模式操作或安全恢复配置":"Undo a mode operation or safely restore configuration");
        ToolTipService.SetToolTip(LanguageButton,_language=="zh"?"切换到 English":"Switch to Chinese");
        ToolTipService.SetToolTip(CopyLogButton,_language=="zh"?"复制本次会话日志":"Copy this session log");
        ToolTipService.SetToolTip(ClearLogButton,_language=="zh"?"清空本次会话日志":"Clear this session log");
        ToolTipService.SetToolTip(RemoteButton,_language=="zh"?"切换到远程推荐（快捷键 1）":"Switch to Remote (shortcut 1)");
        ToolTipService.SetToolTip(SaverButton,_language=="zh"?"切换到低功耗（快捷键 2）":"Switch to Saver (shortcut 2)");
        ToolTipService.SetToolTip(BalancedButton,_language=="zh"?"切换到平衡（快捷键 3）":"Switch to Balanced (shortcut 3)");
        ToolTipService.SetToolTip(HighButton,_language=="zh"?"切换到高性能（快捷键 4）":"Switch to High performance (shortcut 4)");
        RenderRecommendation();
    }

    private static string ReadLanguage()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\PowerModeSwitcher");
        return key?.GetValue("Language") as string is "en" ? "en" : "zh";
    }

    private static string FindCliPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("POWERMODE_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return Path.GetFullPath(configuredPath);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidates = new[]
            {
                Path.Combine(current.FullName, "PowerMode.Cli", "PowerModeSwitcher.bat"),
                Path.Combine(current.FullName, "src", "PowerMode.Cli", "PowerModeSwitcher.bat"),
                Path.Combine(current.FullName, "PowerModeSwitcher.bat")
            };
            foreach (var candidate in candidates)
                if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return string.Empty;
    }

    private static string FindEnginePath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("POWERMODE_ENGINE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return Path.GetFullPath(configuredPath);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidates = new[]
            {
                Path.Combine(current.FullName, "PowerMode.Engine.ps1"),
                Path.Combine(current.FullName, "PowerMode.Cli", "PowerMode.Engine.ps1"),
                Path.Combine(current.FullName, "src", "PowerMode.Cli", "PowerMode.Engine.ps1")
            };
            foreach (var candidate in candidates)
                if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return string.Empty;
    }

    private async Task<bool> RunLanguageUtilityAsync(
        string language,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_cliPath) || !File.Exists(_cliPath))
        {
            await ShowMessageAsync("PowerMode", T("CliMissing"));
            return false;
        }
        var execution = await _processRunner.RunAsync(
            new ProcessExecutionRequest(
                "cmd.exe",
                ["/d", "/s", "/c", $"\"\"{_cliPath}\" lang {language}\""],
                TimeSpan.FromSeconds(10),
                Path.GetDirectoryName(_cliPath),
                new Dictionary<string, string?> { ["PM_NO_PAUSE"] = "1" }),
            cancellationToken);
        AppendProcessDiagnostics("lang", execution);
        return execution.Succeeded;
    }

    private void AppendProcessDiagnostics(string action, ProcessExecutionResult execution)
    {
        AppendLogLine(
            $"[{DateTime.Now:HH:mm:ss}] {action} exit={execution.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} " +
            $"timeout={execution.TimedOut} cancelled={execution.Cancelled} duration={execution.Duration.TotalSeconds:0.0}s");
        if (!string.IsNullOrWhiteSpace(execution.StandardOutput))
            AppendLog(execution.StandardOutput);
        if (!string.IsNullOrWhiteSpace(execution.StandardError))
            AppendLog(execution.StandardError);
        if (!string.IsNullOrWhiteSpace(execution.StartError))
            AppendLog(execution.StartError);
    }

    private void AppendBackendDiagnostics(BackendOperationResult operation)
    {
        AppendLogLine(
            $"[{DateTime.Now:HH:mm:ss}] {operation.Action} operation={operation.OperationId:D} outcome={operation.Outcome}");
        if (!string.IsNullOrWhiteSpace(operation.ContractError))
            AppendLog(operation.ContractError);
        if (operation.ProcessDiagnostics is { } process)
        {
            if (!string.IsNullOrWhiteSpace(process.StandardError))
                AppendLog(process.StandardError);
            if (!string.IsNullOrWhiteSpace(process.StartError))
                AppendLog(process.StartError);
        }
    }

    private int _logLineCount;
    private void AppendLog(string text)
    {
        if(string.IsNullOrWhiteSpace(text))return;
        var addition=text.TrimEnd()+Environment.NewLine+Environment.NewLine;
        LogBox.Text+=addition;_logLineCount+=CountLines(addition);TrimLog();UpdateLogStats();ScrollLogToEnd();
    }
    private void AppendLogLine(string line)
    {
        var addition=line+Environment.NewLine;
        LogBox.Text+=addition;_logLineCount+=CountLines(addition);TrimLog();UpdateLogStats();ScrollLogToEnd();
    }
    private static int CountLines(string value)=>value.Count(character=>character=='\n');
    private void TrimLog()
    {
        if(LogBox.Text.Length<=100_000)return;
        var start=Math.Max(0,LogBox.Text.Length-75_000);var nextLine=LogBox.Text.IndexOf('\n',start);if(nextLine>=0)start=nextLine+1;
        LogBox.Text=LogBox.Text[start..];_logLineCount=CountLines(LogBox.Text)+(LogBox.Text.Length>0&&!LogBox.Text.EndsWith('\n')?1:0);
    }
    private void UpdateLogStats()
    {
        LogStatsText.Text=string.Format(T("LineCount"),_logLineCount);
        var hasLog=!string.IsNullOrWhiteSpace(LogBox.Text);
        CopyLogButton.IsEnabled=hasLog;ClearLogButton.IsEnabled=hasLog;
    }
    private void ScrollLogToEnd(){if(AutoScrollToggle.IsChecked==true)LogBox.Select(LogBox.Text.Length,0);}

    private void SetControlsEnabled(bool enabled, bool includeModeControls = true)
    {
        if(includeModeControls)foreach(var control in new Control[]{RemoteButton,SaverButton,BalancedButton,HighButton,ApplyRecommendationButton,RemoteCustomButton,RemoteNoWifiButton,CpuSlider,CpuBox})control.IsEnabled=enabled;
        foreach(var control in new Control[]{VerifyButton,RepairButton,WifiOnButton,RefreshButton,FeaturesButton,InsightsButton,RecoveryCenterButton,LanguageButton,AutoQuickToggle,LiveQuickToggle})control.IsEnabled=enabled;
        BusyProgress.Visibility=enabled?Visibility.Collapsed:Visibility.Visible;
        if(enabled){ApplyCapabilityPresentation();RenderRecommendation();}
    }

    private async Task RefreshStatusAsync()
    {
        if (_modeSwitchInProgress)
            return;
        RefreshIcon.Visibility=Visibility.Collapsed;RefreshProgressRing.Visibility=Visibility.Visible;RefreshProgressRing.IsActive=true;
        try
        {
            var result = await _powerModeBackend.ReadStateAsync(Guid.NewGuid());
            AppendBackendDiagnostics(result.Operation);
            if (result.State is null)
            {
                if (!_persistentModeSwitchPresentation)
                {
                    StatusText.Text = result.Operation.Outcome == BackendOperationOutcome.TimedOut
                        ? T("RefreshTimeout")
                        : (IsChinese ? "无法可靠读取当前电源状态" : "The current power state could not be read reliably");
                    StatusBar.Severity = result.Operation.Outcome == BackendOperationOutcome.TimedOut
                        ? InfoBarSeverity.Warning
                        : InfoBarSeverity.Error;
                    StatusBar.IsOpen = true;
                }
                return;
            }

            _startupPowerState ??= result.State;
            ApplyPowerModeState(result.State);
            if (!_persistentModeSwitchPresentation)
            {
                StatusText.Text = IsChinese ? "电源状态已刷新" : "Power state refreshed";
                StatusBar.Severity = result.Operation.Outcome == BackendOperationOutcome.Partial
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success;
                StatusBar.IsOpen = true;
            }
        }
        catch (Exception exception)
        {
            if (!_persistentModeSwitchPresentation)
            {
                StatusText.Text = exception.Message;
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.IsOpen = true;
            }
        }
        finally
        {
            RefreshProgressRing.IsActive=false;RefreshProgressRing.Visibility=Visibility.Collapsed;RefreshIcon.Visibility=Visibility.Visible;
        }
    }

    private void ApplyPowerModeState(PowerModeState state)
    {
        var onBattery = state.PowerSource == PowerSourceKind.Battery;
        var modeKey = state.DetectedMode?.ToString().ToLowerInvariant();
        _activeModeKey = modeKey ?? "unknown";
        ModeValue.Text = state.DetectedMode switch
        {
            PowerModePreset.Remote => T("ModeRemote"),
            PowerModePreset.Saver => T("ModeSaver"),
            PowerModePreset.Balanced => T("ModeBalanced"),
            PowerModePreset.High => T("ModeHigh"),
            _ => state.ActiveSchemeName ?? T("Unknown")
        };
        GpuValue.Text = state.DiscreteGpuPowerWatts.HasValue
            ? $"{state.DiscreteGpuPowerWatts.Value:0.##} W"
            : T("Unknown");
        PowerValue.Text = FormatPowerSource(state.PowerSource);
        CpuValue.Text = FormatPercent(onBattery
            ? state.CpuMaximumDcPercent
            : state.CpuMaximumAcPercent);
        BrightnessValue.Text = FormatPercent(onBattery
            ? state.BrightnessDcPercent
            : state.BrightnessAcPercent);
        var display = onBattery
            ? state.DisplayTimeoutDcSeconds
            : state.DisplayTimeoutAcSeconds;
        var sleep = onBattery
            ? state.SleepTimeoutDcSeconds
            : state.SleepTimeoutAcSeconds;
        SleepValue.Text = $"{FormatDuration(display)} / {FormatDuration(sleep)}";

        ToolTipService.SetToolTip(ModeValue, state.ActiveSchemeName ?? ModeValue.Text);
        ToolTipService.SetToolTip(GpuValue, GpuValue.Text);
        ToolTipService.SetToolTip(
            CpuValue,
            $"AC {FormatPercent(state.CpuMaximumAcPercent)} / DC {FormatPercent(state.CpuMaximumDcPercent)}");
        ToolTipService.SetToolTip(
            BrightnessValue,
            $"AC {FormatPercent(state.BrightnessAcPercent)} / DC {FormatPercent(state.BrightnessDcPercent)}");
        ToolTipService.SetToolTip(
            SleepValue,
            $"AC {FormatDuration(state.DisplayTimeoutAcSeconds)} / {FormatDuration(state.SleepTimeoutAcSeconds)}\n" +
            $"DC {FormatDuration(state.DisplayTimeoutDcSeconds)} / {FormatDuration(state.SleepTimeoutDcSeconds)}");
        LastUpdatedText.Text = string.Format(T("LastUpdated"), DateTime.Now.ToString("HH:mm:ss"));
        UpdateActiveMode(modeKey);
    }

    private string FormatPowerSource(PowerSourceKind source) => source switch
    {
        PowerSourceKind.Ac => IsChinese ? "交流电" : "AC power",
        PowerSourceKind.Battery => IsChinese ? "电池" : "Battery",
        PowerSourceKind.Charging => IsChinese ? "充电中" : "Charging",
        PowerSourceKind.Full => IsChinese ? "已充满" : "Full",
        _ => T("Unknown")
    };

    private string FormatDuration(int? seconds)
    {
        if (!seconds.HasValue || seconds.Value < 0) return T("Unknown");
        if(seconds.Value==0)return _language=="zh"?"永不":"Never";
        if(seconds.Value%3600==0){var hours=seconds.Value/3600;return _language=="zh"?$"{hours} 小时":$"{hours} {(hours==1?"hour":"hours")}";}
        if(seconds.Value%60==0){var minutes=seconds.Value/60;return _language=="zh"?$"{minutes} 分钟":$"{minutes} {(minutes==1?"minute":"minutes")}";}
        return _language=="zh"?$"{seconds.Value} 秒":$"{seconds.Value} {(seconds.Value==1?"second":"seconds")}";
    }

    private string FormatPercent(int? value) => value.HasValue ? $"{value.Value}%" : T("Unknown");

    private Task<bool> RunModeAsync(params string[] args)=>RunModeCoreAsync(args,SwitchRequestContext.Manual);
    private void UpdateActiveMode(string? activeMode)
    {
        activeMode = NormalizeMode(activeMode, "unknown");
        var buttons=new[]
        {
            (Mode:"remote",Button:RemoteButton,Name:T("ModeRemote")),
            (Mode:"saver",Button:SaverButton,Name:T("ModeSaver")),
            (Mode:"balanced",Button:BalancedButton,Name:T("ModeBalanced")),
            (Mode:"high",Button:HighButton,Name:T("ModeHigh"))
        };
        foreach(var item in buttons)
        {
            var state=ModeButtonPresentation.Evaluate(item.Mode,activeMode,_pendingMode,_modeSwitchInProgress,item.Name,IsChinese);
            var button=item.Button;
            AutomationProperties.SetItemStatus(button,state.ItemStatus);
            AutomationProperties.SetName(button,state.AutomationName);
            var isActive=state.ShowCheckmark;
            if(isActive)
            {
                button.Background=(Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                button.Foreground=(Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
            }
            else{button.ClearValue(Control.BackgroundProperty);button.ClearValue(Control.ForegroundProperty);}
        }
    }

    private void PresentModeSwitchResult(ModeSwitchResult result)
    {
        _lastModeSwitchResult = result;
        var state = ModeSwitchResultPresentationPolicy.Create(
            result,
            _featureSettings.ExperienceMode,
            IsChinese);
        _statusAction = state.Action;
        _persistentModeSwitchPresentation = state.IsPersistent;
        StatusText.Text = state.Summary;
        StatusBar.Severity = state.Severity switch
        {
            ModeSwitchPresentationSeverity.Success => InfoBarSeverity.Success,
            ModeSwitchPresentationSeverity.Warning => InfoBarSeverity.Warning,
            ModeSwitchPresentationSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational
        };
        StatusBar.IsOpen = true;
        StatusBar.IsClosable = true;
        AutomationProperties.SetName(StatusBar, state.AccessibleName);
        StatusActionButton.Content = state.ActionText;
        StatusActionButton.Visibility = state.Action == ModeSwitchPresentationAction.None
            ? Visibility.Collapsed
            : Visibility.Visible;
        ModeSwitchDiagnosticText.Text = state.ShowDiagnosticDetails
            ? result.DiagnosticSummary
            : string.Empty;
        var diagnosticVisibility = state.ShowDiagnosticDetails
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModeSwitchDiagnosticText.Visibility = diagnosticVisibility;
        CopyModeSwitchDiagnosticButton.Visibility = diagnosticVisibility;
    }

    private void PresentStartupRecovery(StartupInitializationResult result)
    {
        _statusAction = ModeSwitchPresentationAction.OpenRecoveryCenter;
        _persistentModeSwitchPresentation = true;
        StatusText.Text = result.Error ?? (IsChinese
            ? "上次电源操作需要验证或恢复。"
            : "The last power operation must be verified or restored.");
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.IsOpen = true;
        StatusBar.IsClosable = true;
        StatusActionButton.Content = IsChinese ? "立即打开恢复中心" : "Open Recovery Center now";
        StatusActionButton.Visibility = Visibility.Visible;
        ModeSwitchDiagnosticText.Visibility = Visibility.Collapsed;
        CopyModeSwitchDiagnosticButton.Visibility = Visibility.Collapsed;
    }

    private void ClearPersistentRecoveryPresentation()
    {
        _persistentModeSwitchPresentation = false;
        _lastModeSwitchResult = null;
        _statusAction = ModeSwitchPresentationAction.None;
        StatusBar.IsClosable = true;
        StatusBar.IsOpen = false;
        StatusActionButton.Visibility = Visibility.Collapsed;
        ModeSwitchDiagnosticText.Text = string.Empty;
        ModeSwitchDiagnosticText.Visibility = Visibility.Collapsed;
        CopyModeSwitchDiagnosticButton.Visibility = Visibility.Collapsed;
    }

    private async void StatusActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_statusAction == ModeSwitchPresentationAction.OpenRecoveryCenter)
        {
            OpenRecoveryCenterButton_Click(sender, e);
            return;
        }
        if (_statusAction != ModeSwitchPresentationAction.ReviewPartialDetails ||
            _lastModeSwitchResult is not { } result)
            return;

        var failedItems = result.Steps
            .Where(step => !step.Critical &&
                (step.TimedOut || step.ExitCode is not null and not 0 ||
                    !string.IsNullOrWhiteSpace(step.Error)))
            .Select(step => step.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var details = failedItems.Length == 0
            ? (IsChinese ? "没有可显示的可选项目详情。" : "No optional item details are available.")
            : string.Join(Environment.NewLine, failedItems.Select(name => $"• {name}"));
        await ShowMessageAsync(
            IsChinese ? "未应用的项目" : "Items not applied",
            details);
    }

    private void CopyModeSwitchDiagnosticButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastModeSwitchResult is not { DiagnosticSummary.Length: > 0 } result)
            return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(result.DiagnosticSummary);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void StatusBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        _persistentModeSwitchPresentation = false;
    }

    private async Task ShowMessageAsync(string title,string content)
    {
        var dialog=new ContentDialog{Title=title,Content=content,CloseButtonText=T("Confirm"),XamlRoot=RootGrid.XamlRoot};await dialog.ShowAsync();
    }
    private async Task<bool> ConfirmAsync(string title,string content)
    {
        var dialog=new ContentDialog{Title=title,Content=content,PrimaryButtonText=T("Confirm"),CloseButtonText=T("Cancel"),DefaultButton=ContentDialogButton.Close,XamlRoot=RootGrid.XamlRoot};return await dialog.ShowAsync()==ContentDialogResult.Primary;
    }

    private async void RefreshButton_Click(object sender,RoutedEventArgs e)=>await RefreshStatusAsync();
    private async void RemoteButton_Click(object sender,RoutedEventArgs e)=>await RunModeAsync("remote");
    private async void SaverButton_Click(object sender,RoutedEventArgs e)=>await RunModeAsync("saver");
    private async void BalancedButton_Click(object sender,RoutedEventArgs e)=>await RunModeAsync("balanced");
    private async void HighButton_Click(object sender,RoutedEventArgs e)=>await RunModeAsync("high");
    private async void RemoteCustomButton_Click(object sender,RoutedEventArgs e)=>await RunModeAsync("remote",((int)CpuBox.Value).ToString());
    private async void RemoteNoWifiButton_Click(object sender,RoutedEventArgs e){if(await ConfirmAsync(T("ConfirmNoWifiTitle"),T("ConfirmNoWifi")))await RunModeAsync("remote",((int)CpuBox.Value).ToString(),"nowifi");}
    private async void VerifyButton_Click(object sender,RoutedEventArgs e)=>await VerifyWithSummaryAsync();
    private async void LanguageButton_Click(object sender,RoutedEventArgs e)
    {
        var next=_language=="zh"?"en":"zh";
        if (!await RunLanguageUtilityAsync(next))
        {
            StatusText.Text = IsChinese ? "语言切换失败" : "Language switch failed";
            StatusBar.Severity = InfoBarSeverity.Error;
            return;
        }
        _language=next;ApplyLanguage();ApplyExperienceMode(_featureSettings.ExperienceMode);await RefreshStatusAsync();
    }
    private void ClearLogButton_Click(object sender,RoutedEventArgs e){LogBox.Text=string.Empty;_logLineCount=0;UpdateLogStats();}
    private void CopyLogButton_Click(object sender,RoutedEventArgs e){if(string.IsNullOrWhiteSpace(LogBox.Text))return;var package=new Windows.ApplicationModel.DataTransfer.DataPackage();package.SetText(LogBox.Text);Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);StatusText.Text=T("Copied");StatusBar.Severity=InfoBarSeverity.Success;}
    private void CpuSlider_ValueChanged(object sender,RangeBaseValueChangedEventArgs e){if(_syncingCpu||CpuBox is null)return;_syncingCpu=true;CpuBox.Value=Math.Round(e.NewValue);_syncingCpu=false;}
    private void CpuBox_ValueChanged(NumberBox sender,NumberBoxValueChangedEventArgs args){if(_syncingCpu||CpuSlider is null||double.IsNaN(args.NewValue))return;_syncingCpu=true;CpuSlider.Value=Math.Clamp(args.NewValue,20,50);_syncingCpu=false;}
}
