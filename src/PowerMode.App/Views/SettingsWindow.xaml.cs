using System.Globalization;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace PowerModeWinUI;

public sealed partial class SettingsWindow : Window
{
    private readonly MainWindow _owner;
    private readonly bool _zh;
    private readonly SystemIntegrationService _systemIntegration;
    private PowerModeSettings _settings;
    private bool _loading;

    private sealed record ConditionOption(RuleConditionType Type, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ModeOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record RuleListItem(Guid Id, string Display)
    {
        public override string ToString() => Display;
    }

    public SettingsWindow(MainWindow owner, PowerModeSettings settings, bool chinese)
    {
        _owner = owner;
        _settings = settings;
        _zh = chinese;
        _systemIntegration = new SystemIntegrationService(dataDirectory: SettingsStore.DirectoryPath);

        InitializeComponent();
        DpiAwareWindowSizer.Resize(this, 760, 680, 680, 580);
        ApplyLanguage();
        InitializeRuleEditor();
        LoadSettings();
        ApplyCapabilityPresentation(settings.ExperienceMode, owner.HardwareCapabilities);
        UpdateVendorInfo();
        Closed += (_, _) => _systemIntegration.Dispose();
    }

    internal void ApplyCapabilityPresentation(
        ExperienceMode mode,
        HardwareCapabilities capabilities)
    {
        var policy = CapabilityVisibilityPolicy.Evaluate(mode, capabilities);
        ApplyCapabilityPresentation(BrightnessSlider, policy[CapabilityFeature.Brightness]);
        ApplyCapabilityPresentation(BatteryValuesExpander, policy[CapabilityFeature.BatterySettings]);
        ApplyCapabilityPresentation(LowBatteryLabel, policy[CapabilityFeature.BatterySettings]);
        ApplyCapabilityPresentation(LowBatteryBox, policy[CapabilityFeature.BatterySettings]);
        ApplyCapabilityPresentation(
            TemperatureProtectionToggle,
            policy[CapabilityFeature.TemperatureProtection]);
        ApplyCapabilityPresentation(
            TemperatureLimitLabel,
            policy[CapabilityFeature.TemperatureProtection]);
        ApplyCapabilityPresentation(
            TemperatureLimitBox,
            policy[CapabilityFeature.TemperatureProtection]);
        ApplyCapabilityPresentation(
            TemperatureRecoveryLabel,
            policy[CapabilityFeature.TemperatureProtection]);
        ApplyCapabilityPresentation(
            TemperatureRecoveryBox,
            policy[CapabilityFeature.TemperatureProtection]);
        ApplyCapabilityPresentation(NotificationsToggle, policy[CapabilityFeature.Notifications]);
        ApplyCapabilityPresentation(HotkeyHint, policy[CapabilityFeature.GlobalHotkeys]);
    }

    private static void ApplyCapabilityPresentation(
        FrameworkElement element,
        FeaturePresentation presentation)
    {
        element.Visibility = presentation.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (element is Control control) control.IsEnabled = presentation.IsEnabled;
        element.Opacity = presentation.IsEnabled ? 1 : 0.55;
        ToolTipService.SetToolTip(
            element,
            string.IsNullOrEmpty(presentation.Reason) ? null : presentation.Reason);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
            element,
            presentation.Reason);
    }

    private void ApplyLanguage()
    {
        if (_zh) return;
        Title = "PowerMode Feature Center";
        HeaderText.Text = "Feature Center";
        SubheaderText.Text = "Custom profiles, automation and safety";
        ProfilesTab.Header = "Custom profiles";
        AutomationTab.Header = "Automation & safety";
        SavedProfileLabel.Text = "Saved profiles";
        ProfileNameLabel.Text = "Profile name";
        CpuMaxLabel.Text = "CPU maximum";
        BrightnessLabel.Text = "Brightness";
        CpuMinLabel.Text = "CPU minimum state (%)";
        DisplayOffLabel.Text = "Display off (seconds, 0 = never)";
        DisableBoostCheck.Content = "Disable CPU boost";
        ProfileHintText.Text = "Custom profiles use the power saver plan and keep sleep and hibernation disabled.";
        SaveProfileButton.Content = "Save profile";
        DeleteProfileButton.Content = "Delete";
        ApplyProfileButton.Content = "Apply this profile";
        AutoSwitchToggle.Header = "Automatic mode switching";
        RealtimeToggle.Header = "Live status monitoring";
        RestoreExitToggle.Header = "Restore startup plan on exit";
        AutoHintText.Text = "Remote apps use Remote; heavy apps use High; battery uses Saver; otherwise Balanced.";
        IntervalLabel.Text = "Monitor interval (seconds, minimum 10)";
        RemoteProcessesLabel.Text = "Remote process names (comma-separated)";
        PerformanceProcessesLabel.Text = "Performance process names (comma-separated)";
        HotkeyHint.Message = "Global shortcuts: Ctrl + Alt + 1/2/3/4";
        ImportButton.Content = "Import";
        ExportButton.Content = "Export";
        CloseButton.Content = "Save and close";
        BatteryValuesExpander.Header = "Battery-specific values";
        SeparateBatteryToggle.Header = "Use separate DC values";
        BatteryCpuLabel.Text = "CPU maximum";
        BatteryBrightnessLabel.Text = "Brightness";
        BatteryDisplayLabel.Text = "Display seconds";
        TemperatureProtectionToggle.Header = "Temperature protection";
        TemperatureLimitLabel.Text = "Limit temperature (°C)";
        TemperatureRecoveryLabel.Text = "Recovery temperature (°C)";
        LowBatteryLabel.Text = "Low battery threshold (%)";
        RulesTab.Header = "Rules";
        RuleEditorTitle.Text = "New automation rule";
        RulesListTitle.Text = "Rule priority (top to bottom)";
        RuleNameBox.Header = "Name";
        RuleNameBox.PlaceholderText = "For example: save power on low battery";
        RuleConditionCombo.Header = "Condition";
        RuleValueBox.Header = "Condition value";
        RuleModeCombo.Header = "Target mode";
        AddRuleButton.Content = "Add rule";
        ToggleRuleButton.Content = "Enable / disable";
        DeleteRuleButton.Content = "Delete";
        SystemTab.Header = "System & maintenance";
        NotificationsToggle.Header = "Show mode change notifications";
        PreviewManualSwitchesToggle.Header = "Preview and confirm manual switches";
        StartWithWindowsToggle.Header = "Start when signing in";
        StartMinimizedToggle.Header = "Start minimized to tray with Windows";
        ApplyLastModeToggle.Header = "Apply last mode at startup";
        HistoryToggle.Header = "Record switch history";
        UpdateCheckToggle.Header = "Check updates at startup";
        UpdateUrlBox.Header = "GitHub Releases API URL";
        BackupCountBox.Header = "Configuration backups to keep";
        BackupButton.Content = "Back up now";
        RestoreBackupButton.Content = "Restore latest";
        CheckUpdateButton.Content = "Check for updates";
    }

    private void InitializeRuleEditor()
    {
        RuleConditionCombo.ItemsSource = new[]
        {
            new ConditionOption(RuleConditionType.PowerSource, _zh ? "供电来源" : "Power source"),
            new ConditionOption(RuleConditionType.BatteryLevel, _zh ? "电池电量" : "Battery level"),
            new ConditionOption(RuleConditionType.TimeOfDay, _zh ? "时间段" : "Time range"),
            new ConditionOption(RuleConditionType.ProcessRunning, _zh ? "进程正在运行" : "Process running"),
            new ConditionOption(RuleConditionType.UserIdleSeconds, _zh ? "用户空闲时间" : "User idle time"),
            new ConditionOption(RuleConditionType.ForegroundFullscreen, _zh ? "前台全屏" : "Foreground fullscreen"),
            new ConditionOption(RuleConditionType.RemoteSession, _zh ? "远程桌面会话" : "Remote desktop session")
        };
        RuleModeCombo.ItemsSource = new[]
        {
            new ModeOption("balanced", _zh ? "平衡" : "Balanced"),
            new ModeOption("saver", _zh ? "低功耗" : "Saver"),
            new ModeOption("high", _zh ? "高性能" : "High performance"),
            new ModeOption("remote", _zh ? "远程" : "Remote")
        };
        RuleConditionCombo.SelectedIndex = 0;
        RuleModeCombo.SelectedIndex = 0;
        UpdateRuleHelp();
    }

    private void LoadSettings()
    {
        _loading = true;
        ProfileCombo.ItemsSource = null;
        ProfileCombo.ItemsSource = _settings.Profiles;
        if (_settings.Profiles.Count > 0) ProfileCombo.SelectedIndex = 0;
        AutoSwitchToggle.IsOn = _settings.AutoSwitchEnabled;
        RealtimeToggle.IsOn = _settings.RealTimeMonitoringEnabled;
        RestoreExitToggle.IsOn = _settings.RestorePlanOnExit;
        TemperatureProtectionToggle.IsOn = _settings.TemperatureProtectionEnabled;
        TemperatureLimitBox.Value = _settings.TemperatureLimitCelsius;
        TemperatureRecoveryBox.Value = _settings.TemperatureRecoveryCelsius;
        LowBatteryBox.Value = _settings.LowBatteryThreshold;
        IntervalBox.Value = _settings.MonitorIntervalSeconds;
        RemoteProcessesBox.Text = _settings.RemoteProcesses;
        PerformanceProcessesBox.Text = _settings.PerformanceProcesses;
        NotificationsToggle.IsOn = _settings.NotificationsEnabled;
        PreviewManualSwitchesToggle.IsOn = _settings.PreviewManualSwitches;
        StartWithWindowsToggle.IsOn = _settings.StartWithWindows;
        StartMinimizedToggle.IsOn = _settings.StartMinimized;
        ApplyLastModeToggle.IsOn = _settings.ApplyLastModeOnStartup;
        HistoryToggle.IsOn = _settings.OperationHistoryEnabled;
        UpdateCheckToggle.IsOn = _settings.CheckUpdatesOnStartup;
        UpdateUrlBox.Text = _settings.UpdateApiUrl;
        BackupCountBox.Value = _settings.ConfigurationBackupCount;
        _loading = false;

        if (ProfileCombo.SelectedItem is CustomPowerProfile profile) ShowProfile(profile);
        RefreshRulesList();
    }

    private void ShowProfile(CustomPowerProfile profile)
    {
        _loading = true;
        ProfileNameBox.Text = profile.Name;
        CpuMaxSlider.Value = profile.CpuMax;
        CpuMinBox.Value = profile.CpuMin;
        BrightnessSlider.Value = profile.Brightness;
        DisplayOffBox.Value = profile.DisplayOffSeconds;
        DisableBoostCheck.IsChecked = profile.DisableBoost;
        SeparateBatteryToggle.IsOn = profile.UseSeparateBatteryValues;
        BatteryCpuBox.Value = profile.BatteryCpuMax;
        BatteryBrightnessBox.Value = profile.BatteryBrightness;
        BatteryDisplayBox.Value = profile.BatteryDisplayOffSeconds;
        SetBatteryValuesEnabled(profile.UseSeparateBatteryValues);
        _loading = false;
        UpdateValues();
    }

    private CustomPowerProfile ReadProfile()
    {
        var max = (int)CpuMaxSlider.Value;
        return new CustomPowerProfile
        {
            Name = string.IsNullOrWhiteSpace(ProfileNameBox.Text)
                ? (_zh ? "自定义模式" : "Custom profile")
                : ProfileNameBox.Text.Trim(),
            CpuMax = max,
            CpuMin = Math.Clamp((int)CpuMinBox.Value, 0, max),
            Brightness = (int)BrightnessSlider.Value,
            DisplayOffSeconds = Math.Max(0, (int)DisplayOffBox.Value),
            DisableBoost = DisableBoostCheck.IsChecked == true,
            UseSeparateBatteryValues = SeparateBatteryToggle.IsOn,
            BatteryCpuMax = Math.Clamp((int)BatteryCpuBox.Value, 5, 100),
            BatteryBrightness = Math.Clamp((int)BatteryBrightnessBox.Value, 0, 100),
            BatteryDisplayOffSeconds = Math.Max(0, (int)BatteryDisplayBox.Value)
        };
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ProfileCombo.SelectedItem is CustomPowerProfile profile) ShowProfile(profile);
    }

    private void ProfileSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading) UpdateValues();
    }

    private void UpdateValues()
    {
        if (CpuMaxValue is null || BrightnessValue is null) return;
        CpuMaxValue.Text = $"{(int)CpuMaxSlider.Value}%";
        BrightnessValue.Text = $"{(int)BrightnessSlider.Value}%";
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var profile = ReadProfile();
        var existing = _settings.Profiles.FirstOrDefault(x =>
            x.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _settings.Profiles.Add(profile);
        }
        else
        {
            existing.CpuMax = profile.CpuMax;
            existing.CpuMin = profile.CpuMin;
            existing.Brightness = profile.Brightness;
            existing.DisplayOffSeconds = profile.DisplayOffSeconds;
            existing.DisableBoost = profile.DisableBoost;
            existing.UseSeparateBatteryValues = profile.UseSeparateBatteryValues;
            existing.BatteryCpuMax = profile.BatteryCpuMax;
            existing.BatteryBrightness = profile.BatteryBrightness;
            existing.BatteryDisplayOffSeconds = profile.BatteryDisplayOffSeconds;
        }

        await PersistSettingsAsync("profile-save");
        LoadSettings();
        ShowSaved(_zh ? "预设已保存" : "Profile saved");
    }

    private async void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not CustomPowerProfile profile) return;
        _settings.Profiles.Remove(profile);
        await PersistSettingsAsync("profile-delete");
        LoadSettings();
        ShowSaved(_zh ? "预设已删除" : "Profile deleted");
    }

    private async void ApplyProfileButton_Click(object sender, RoutedEventArgs e)
    {
        Root.IsHitTestVisible = false;
        try
        {
            await _owner.ApplyCustomProfileAsync(ReadProfile());
            ShowSaved(_zh ? "预设已应用" : "Profile applied");
        }
        finally
        {
            Root.IsHitTestVisible = true;
        }
    }

    private void CaptureSettingsFromUi()
    {
        _settings.AutoSwitchEnabled = AutoSwitchToggle.IsOn;
        _settings.RealTimeMonitoringEnabled = RealtimeToggle.IsOn;
        _settings.RestorePlanOnExit = RestoreExitToggle.IsOn;
        _settings.TemperatureProtectionEnabled = TemperatureProtectionToggle.IsOn;
        _settings.TemperatureLimitCelsius = TemperatureLimitBox.Value;
        _settings.TemperatureRecoveryCelsius = Math.Min(TemperatureRecoveryBox.Value,
            TemperatureLimitBox.Value - 1);
        _settings.LowBatteryThreshold = Math.Clamp((int)LowBatteryBox.Value, 5, 95);
        _settings.MonitorIntervalSeconds = Math.Max(10, (int)IntervalBox.Value);
        _settings.RemoteProcesses = RemoteProcessesBox.Text.Trim();
        _settings.PerformanceProcesses = PerformanceProcessesBox.Text.Trim();
        _settings.NotificationsEnabled = NotificationsToggle.IsOn;
        _settings.PreviewManualSwitches = PreviewManualSwitchesToggle.IsOn;
        _settings.StartWithWindows = StartWithWindowsToggle.IsOn;
        _settings.StartMinimized = StartMinimizedToggle.IsOn;
        _settings.ApplyLastModeOnStartup = ApplyLastModeToggle.IsOn;
        _settings.OperationHistoryEnabled = HistoryToggle.IsOn;
        _settings.CheckUpdatesOnStartup = UpdateCheckToggle.IsOn;
        _settings.UpdateApiUrl = UpdateUrlBox.Text.Trim();
        _settings.ConfigurationBackupCount = Math.Clamp((int)BackupCountBox.Value, 1, 50);
    }

    private async Task PersistSettingsAsync(string backupReason, bool createVersionBackup = true)
    {
        SettingsStore.Save(_settings);
        _owner.ApplyFeatureSettings(_settings);

        if (createVersionBackup)
        {
            try
            {
                await _systemIntegration.BackupConfigurationIfChangedAsync(SettingsStore.FilePath,
                    backupReason, _settings.ConfigurationBackupCount);
            }
            catch
            {
                // Saving the live settings is more important than an optional historical copy.
            }
        }
    }

    private void ApplyStartupRegistration()
    {
        _systemIntegration.ConfigureStartup(_settings.StartWithWindows, _settings.StartMinimized);
    }

    private async Task SaveAutomationAsync()
    {
        CaptureSettingsFromUi();
        ApplyStartupRegistration();
        await PersistSettingsAsync("settings-save");
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            _settings = SettingsStore.Import(file.Path);
            ApplyStartupRegistration();
            await PersistSettingsAsync("import");
            LoadSettings();
            ShowSaved(_zh ? "配置已导入" : "Settings imported");
        }
        catch (Exception ex)
        {
            ShowSaved(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveAutomationAsync();
            var picker = new FileSavePicker { SuggestedFileName = "PowerMode-settings" };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            SettingsStore.Export(_settings, file.Path);
            ShowSaved(_zh ? "配置已导出" : "Settings exported");
        }
        catch (Exception ex)
        {
            ShowSaved(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Root.IsHitTestVisible = false;
        try
        {
            await SaveAutomationAsync();
            Close();
        }
        catch (Exception ex)
        {
            Root.IsHitTestVisible = true;
            ShowSaved(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SeparateBatteryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (BatteryValuesPanel is not null) SetBatteryValuesEnabled(SeparateBatteryToggle.IsOn);
    }

    private void SetBatteryValuesEnabled(bool enabled)
    {
        BatteryValuesPanel.IsHitTestVisible = enabled;
        BatteryValuesPanel.Opacity = enabled ? 1 : 0.45;
    }

    private void RuleConditionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuleHelpText is null) return;
        UpdateRuleHelp();
    }

    private void UpdateRuleHelp()
    {
        if (RuleConditionCombo.SelectedItem is not ConditionOption option) return;
        RuleValueBox.PlaceholderText = option.Type switch
        {
            RuleConditionType.PowerSource => _zh ? "电池 或 交流电" : "Battery or AC",
            RuleConditionType.BatteryLevel => _zh ? "百分比，例如 30" : "Percent, for example 30",
            RuleConditionType.TimeOfDay => _zh ? "例如 22:00-07:30" : "For example 22:00-07:30",
            RuleConditionType.ProcessRunning => _zh ? "进程名，多个用逗号分隔" : "Process names separated by commas",
            RuleConditionType.UserIdleSeconds => _zh ? "秒数，例如 600" : "Seconds, for example 600",
            RuleConditionType.ForegroundFullscreen or RuleConditionType.RemoteSession =>
                _zh ? "true/false（留空表示 true）" : "true/false (empty means true)",
            _ => string.Empty
        };
        RuleHelpText.Text = option.Type switch
        {
            RuleConditionType.BatteryLevel => _zh ? "电量小于或等于该值时触发。" : "Triggers when battery level is at or below this value.",
            RuleConditionType.TimeOfDay => _zh ? "支持跨午夜时间段，例如 22:00-07:30。" : "Overnight ranges such as 22:00-07:30 are supported.",
            RuleConditionType.UserIdleSeconds => _zh ? "空闲时间达到该秒数时触发。" : "Triggers after the user has been idle for this many seconds.",
            RuleConditionType.ProcessRunning => _zh ? "任意一个列出的进程运行时触发。" : "Triggers while any listed process is running.",
            _ => _zh ? "规则按优先级从上到下匹配。" : "Rules are matched from highest to lowest priority."
        };
    }

    private RuleCondition BuildCondition()
    {
        if (RuleConditionCombo.SelectedItem is not ConditionOption option)
            throw new FormatException(_zh ? "请选择规则条件。" : "Choose a rule condition.");

        var value = RuleValueBox.Text.Trim();
        return option.Type switch
        {
            RuleConditionType.PowerSource => BuildPowerSourceCondition(value),
            RuleConditionType.BatteryLevel => new RuleCondition
            {
                Type = option.Type,
                Comparison = RuleComparison.LessThanOrEqual,
                Value = ParseNumber(value, 0, 100, _zh ? "电池电量" : "battery level")
            },
            RuleConditionType.TimeOfDay => BuildTimeCondition(value),
            RuleConditionType.ProcessRunning => new RuleCondition
            {
                Type = option.Type,
                Comparison = RuleComparison.AnyOf,
                Value = RequireValue(value, _zh ? "请输入至少一个进程名。" : "Enter at least one process name.")
            },
            RuleConditionType.UserIdleSeconds => new RuleCondition
            {
                Type = option.Type,
                Comparison = RuleComparison.GreaterThanOrEqual,
                Value = ParseNumber(value, 0, 86400 * 30, _zh ? "空闲秒数" : "idle seconds")
            },
            RuleConditionType.ForegroundFullscreen or RuleConditionType.RemoteSession => new RuleCondition
            {
                Type = option.Type,
                Comparison = RuleComparison.Equals,
                Value = ParseBoolean(value).ToString(CultureInfo.InvariantCulture)
            },
            _ => throw new FormatException(_zh ? "暂不支持此条件。" : "This condition is not supported.")
        };
    }

    private RuleCondition BuildPowerSourceCondition(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0 || normalized.Equals("battery", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("dc", StringComparison.OrdinalIgnoreCase) || normalized.Contains("电池"))
        {
            return new RuleCondition
            {
                Type = RuleConditionType.PowerSource,
                Comparison = RuleComparison.Equals,
                Value = nameof(AutomationPowerSource.Battery)
            };
        }

        if (normalized.Equals("ac", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("交流") || normalized.Contains("电源"))
        {
            return new RuleCondition
            {
                Type = RuleConditionType.PowerSource,
                Comparison = RuleComparison.Equals,
                Value = nameof(AutomationPowerSource.Ac)
            };
        }

        throw new FormatException(_zh ? "供电来源请输入“电池”或“交流电”。" : "Power source must be Battery or AC.");
    }

    private RuleCondition BuildTimeCondition(string value)
    {
        var parts = value.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !TimeSpan.TryParse(parts[0], CultureInfo.CurrentCulture, out var start)
            || !TimeSpan.TryParse(parts[1], CultureInfo.CurrentCulture, out var end)
            || start < TimeSpan.Zero || start >= TimeSpan.FromDays(1)
            || end < TimeSpan.Zero || end >= TimeSpan.FromDays(1))
        {
            throw new FormatException(_zh ? "时间段格式应为 HH:mm-HH:mm。" : "Use the time range format HH:mm-HH:mm.");
        }

        return new RuleCondition
        {
            Type = RuleConditionType.TimeOfDay,
            Comparison = RuleComparison.Between,
            Value = start.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
            SecondaryValue = end.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
        };
    }

    private static string RequireValue(string value, string error)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException(error);
        return value;
    }

    private static string ParseNumber(string value, double min, double max, string fieldName)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var number)
            && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            throw new FormatException($"{fieldName}: invalid number");
        if (number < min || number > max)
            throw new FormatException($"{fieldName}: {min:0}-{max:0}");
        return number.ToString(CultureInfo.InvariantCulture);
    }

    private static bool ParseBoolean(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (bool.TryParse(value, out var parsed)) return parsed;
        if (value is "1" or "是" or "开启" or "开") return true;
        if (value is "0" or "否" or "关闭" or "关") return false;
        throw new FormatException("Boolean value must be true or false.");
    }

    private async void AddRuleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RuleModeCombo.SelectedItem is not ModeOption mode)
                throw new FormatException(_zh ? "请选择目标模式。" : "Choose a target mode.");
            var condition = BuildCondition();
            var fallbackName = RuleConditionCombo.SelectedItem is ConditionOption option
                ? option.Label
                : (_zh ? "自动规则" : "Automation rule");
            var nextPriority = _settings.Rules.Count == 0 ? 100 : _settings.Rules.Max(x => x.Priority) + 10;
            _settings.Rules.Add(new AutomationRule
            {
                Name = string.IsNullOrWhiteSpace(RuleNameBox.Text) ? fallbackName : RuleNameBox.Text.Trim(),
                Description = string.IsNullOrWhiteSpace(RuleNameBox.Text) ? fallbackName : RuleNameBox.Text.Trim(),
                IsEnabled = true,
                Priority = nextPriority,
                TargetMode = mode.Value,
                MatchMode = RuleMatchMode.All,
                Conditions = new List<RuleCondition> { condition }
            });
            await PersistSettingsAsync("rule-add");
            RuleNameBox.Text = string.Empty;
            RuleValueBox.Text = string.Empty;
            RefreshRulesList();
            ShowSaved(_zh ? "自动规则已添加" : "Automation rule added");
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            ShowSaved(ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async void ToggleRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var rule = GetSelectedRule();
        if (rule is null)
        {
            ShowSaved(_zh ? "请先选择一条规则。" : "Select a rule first.", InfoBarSeverity.Warning);
            return;
        }
        rule.IsEnabled = !rule.IsEnabled;
        await PersistSettingsAsync("rule-toggle");
        RefreshRulesList(rule.Id);
        ShowSaved(rule.IsEnabled ? (_zh ? "规则已启用" : "Rule enabled") : (_zh ? "规则已停用" : "Rule disabled"));
    }

    private async void DeleteRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var rule = GetSelectedRule();
        if (rule is null)
        {
            ShowSaved(_zh ? "请先选择一条规则。" : "Select a rule first.", InfoBarSeverity.Warning);
            return;
        }
        _settings.Rules.Remove(rule);
        await PersistSettingsAsync("rule-delete");
        RefreshRulesList();
        ShowSaved(_zh ? "规则已删除" : "Rule deleted");
    }

    private AutomationRule? GetSelectedRule()
    {
        return RulesList.SelectedItem is RuleListItem item
            ? _settings.Rules.FirstOrDefault(x => x.Id == item.Id)
            : null;
    }

    private void RefreshRulesList(Guid? selectId = null)
    {
        selectId ??= (RulesList.SelectedItem as RuleListItem)?.Id;
        var items = _settings.Rules
            .OrderByDescending(x => x.Priority)
            .Select(rule => new RuleListItem(rule.Id, FormatRule(rule)))
            .ToList();
        RulesList.ItemsSource = items;
        if (selectId is Guid id)
            RulesList.SelectedItem = items.FirstOrDefault(x => x.Id == id);
    }

    private string FormatRule(AutomationRule rule)
    {
        var state = rule.IsEnabled ? "●" : "○";
        var mode = rule.TargetMode.ToLowerInvariant() switch
        {
            "saver" => _zh ? "低功耗" : "Saver",
            "high" => _zh ? "高性能" : "High performance",
            "remote" => _zh ? "远程" : "Remote",
            _ => _zh ? "平衡" : "Balanced"
        };
        var summary = rule.Conditions.Count == 0 ? string.Empty : FormatCondition(rule.Conditions[0]);
        return $"{state}  {rule.Name}\n{summary}  →  {mode}";
    }

    private string FormatCondition(RuleCondition condition)
    {
        var label = condition.Type switch
        {
            RuleConditionType.PowerSource => _zh ? "供电" : "Power",
            RuleConditionType.BatteryLevel => _zh ? "电量 ≤" : "Battery ≤",
            RuleConditionType.TimeOfDay => _zh ? "时间" : "Time",
            RuleConditionType.ProcessRunning => _zh ? "进程" : "Process",
            RuleConditionType.UserIdleSeconds => _zh ? "空闲 ≥" : "Idle ≥",
            RuleConditionType.ForegroundFullscreen => _zh ? "全屏" : "Fullscreen",
            RuleConditionType.RemoteSession => _zh ? "远程会话" : "Remote session",
            _ => condition.Type.ToString()
        };
        var value = condition.SecondaryValue is null
            ? condition.Value
            : $"{condition.Value}-{condition.SecondaryValue}";
        return $"{label} {value}";
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        BackupButton.IsEnabled = false;
        try
        {
            CaptureSettingsFromUi();
            ApplyStartupRegistration();
            await PersistSettingsAsync("manual-save", createVersionBackup: false);
            var backup = await _systemIntegration.CreateConfigurationBackupAsync(SettingsStore.FilePath,
                "manual", _settings.ConfigurationBackupCount);
            ShowSaved(_zh
                ? $"配置已备份：{backup.FileName}"
                : $"Configuration backed up: {backup.FileName}");
        }
        catch (Exception ex)
        {
            ShowSaved(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BackupButton.IsEnabled = true;
        }
    }

    private async void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var latest = FindLatestRestorableBackup();
        if (latest is null)
        {
            ShowSaved(_zh ? "还没有与当前配置不同的历史版本。" : "No backup differs from the current configuration.",
                InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = _zh ? "恢复最近配置？" : "Restore latest configuration?",
            Content = _zh
                ? $"将恢复 {latest.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} 的配置。当前配置会先自动备份。"
                : $"Restore the configuration from {latest.CreatedUtc.ToLocalTime():g}? The current configuration will be backed up first.",
            PrimaryButtonText = _zh ? "恢复" : "Restore",
            CloseButtonText = _zh ? "取消" : "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        RestoreBackupButton.IsEnabled = false;
        try
        {
            var result = await _systemIntegration.RestoreConfigurationBackupAsync(latest.Path,
                SettingsStore.FilePath, createSafetyBackup: true);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Error ?? (_zh ? "配置恢复失败。" : "Configuration restore failed."));
            _settings = SettingsStore.Load();
            ApplyStartupRegistration();
            _owner.ApplyFeatureSettings(_settings);
            LoadSettings();
            ShowSaved(_zh ? "配置已恢复并立即生效。" : "Configuration restored and applied.");
        }
        catch (Exception ex)
        {
            ShowSaved(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            RestoreBackupButton.IsEnabled = true;
        }
    }

    private ConfigurationBackupInfo? FindLatestRestorableBackup()
    {
        var backups = _systemIntegration.ListConfigurationBackups();
        if (!File.Exists(SettingsStore.FilePath)) return backups.FirstOrDefault();
        try
        {
            var currentHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SettingsStore.FilePath)));
            return backups.FirstOrDefault(backup =>
                !string.Equals(backup.Sha256, currentHash, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return backups.FirstOrDefault();
        }
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(UpdateUrlBox.Text.Trim(), UriKind.Absolute, out var apiUri))
        {
            ShowSaved(_zh ? "请输入有效的 GitHub Releases API 地址。" : "Enter a valid GitHub Releases API URL.",
                InfoBarSeverity.Warning);
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        ShowSaved(_zh ? "正在检查更新…" : "Checking for updates…", InfoBarSeverity.Informational);
        try
        {
            var result = await _systemIntegration.CheckForUpdatesAsync(apiUri);
            if (!result.Succeeded)
            {
                ShowSaved(result.Error ?? (_zh ? "更新检查失败。" : "Update check failed."), InfoBarSeverity.Error);
                return;
            }

            if (!result.IsUpdateAvailable)
            {
                ShowSaved(_zh
                    ? $"当前已是最新版本（{result.CurrentVersion}）。"
                    : $"You are up to date ({result.CurrentVersion}).");
                return;
            }

            ShowSaved(_zh
                ? $"发现新版本 {result.LatestVersion}。"
                : $"Version {result.LatestVersion} is available.", InfoBarSeverity.Informational);
            var releaseUri = result.ReleasePageUri ?? result.PortableDownloadUri;
            if (releaseUri is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    Title = _zh ? "发现新版本" : "Update available",
                    Content = _zh
                        ? $"当前版本：{result.CurrentVersion}\n最新版本：{result.LatestVersion}"
                        : $"Current: {result.CurrentVersion}\nLatest: {result.LatestVersion}",
                    PrimaryButtonText = _zh ? "打开发布页" : "Open release page",
                    CloseButtonText = _zh ? "稍后" : "Later",
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await Windows.System.Launcher.LaunchUriAsync(releaseUri);
            }
        }
        catch (Exception ex)
        {
            ShowSaved(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void UpdateVendorInfo()
    {
        try
        {
            var capability = _systemIntegration.GetChargingLimitCapability();
            var device = capability.Device;
            VendorInfo.Title = _zh
                ? $"{device.Manufacturer} {device.Model} · 充电上限"
                : $"{device.Manufacturer} {device.Model} · Charge limit";
            VendorInfo.Message = _zh ? capability.DescriptionZh : capability.DescriptionEn;
            VendorInfo.Severity = capability.VendorUtilityMaySupport
                ? InfoBarSeverity.Informational
                : InfoBarSeverity.Warning;
            if (capability.SupportUri is not null)
            {
                VendorInfo.ActionButton = new HyperlinkButton
                {
                    Content = capability.RecommendedTool ?? (_zh ? "厂商支持" : "OEM support"),
                    NavigateUri = capability.SupportUri
                };
            }
        }
        catch (Exception ex)
        {
            VendorInfo.Title = _zh ? "充电上限" : "Charge limit";
            VendorInfo.Message = ex.Message;
            VendorInfo.Severity = InfoBarSeverity.Warning;
        }
    }

    private void InitializePicker(object picker)
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private void ShowSaved(string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        SaveStatus.Message = message;
        SaveStatus.Severity = severity;
        SaveStatus.IsOpen = true;
    }
}
