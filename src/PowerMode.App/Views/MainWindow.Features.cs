using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.System;

namespace PowerModeWinUI;

public sealed partial class MainWindow
{
    private const uint WmHotkey=0x0312,WmTray=0x8000+100,WmLButtonDblClk=0x0203,WmRButtonUp=0x0205;
    private const uint ModAlt=0x0001,ModControl=0x0002;
    private SettingsLoadResult _settingsLoadResult=default!;
    private PowerModeSettings _featureSettings=default!;
    private DispatcherTimer? _featureTimer;
    private bool _featureTickInProgress;
    private string _lastAutoMode=string.Empty;
    private CustomPowerProfile? _lastCustomProfile;
    private SettingsWindow? _settingsWindow;
    private IntPtr _hwnd;
    private Native.SubclassProc? _subclassProc;
    private bool _trayAdded;
    private bool _globalHotkeysAvailable;
    private bool _closingAfterRestore;
    private bool _exitRestoreInProgress;
    private readonly SettingsOwnerShutdownCoordinator _settingsOwnerShutdownCoordinator = new();
    private HardwareCapabilities _hardwareCapabilities=HardwareCapabilities.Unknown;
    private HardwareCapabilityService? _hardwareCapabilityService;
    private readonly CapabilityPresentationLifetime _capabilityPresentationLifetime=new();

    internal HardwareCapabilities HardwareCapabilities => _hardwareCapabilities;
    internal bool CanPersistSettings => _settingsActivationCoordinator.CanPersistSettings;

    internal bool TrySaveSettings(PowerModeSettings settings)
    {
        if (!CanPersistSettings)
        {
            ShowCorruptSettingsWarning();
            return false;
        }

        try
        {
            SettingsStore.Save(settings);
            return true;
        }
        catch (Exception exception)
        {
            AppendLog($"Settings save: {exception.Message}");
            return false;
        }
    }

    internal Task AcceptRecoveredSettingsAsync(PowerModeSettings settings)
    {
        return _recoveredSettingsActivationFlow.RunAsync(new SettingsLoadResult(
            SettingsLoadState.Loaded,
            settings));
    }

    private void AcceptRecoveredSettingsState(SettingsLoadResult recovered)
    {
        ArgumentNullException.ThrowIfNull(recovered);
        if (!recovered.AllowsExternalSideEffects)
            throw new InvalidOperationException("Recovered settings are not usable.");
        _settingsActivationCoordinator.AcceptRecoveredSettings(recovered);
        _startupCoordinator.AcceptRecoveredSettings(recovered);
        _settingsLoadResult = recovered;
        _featureSettings = recovered.Settings;
    }

    private void ApplyRecoveredSettingsPresentation(SettingsLoadResult recovered)
    {
        ApplyFeatureSettingsPresentation(recovered.Settings);
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusText.Text = IsChinese
            ? "配置已恢复，可继续编辑设置"
            : "Settings recovered; editing is available again";
    }

    private void ShowCorruptSettingsWarning()
    {
        StatusBar.Severity = InfoBarSeverity.Warning;
        StatusText.Text = IsChinese
            ? "配置文件损坏：请打开恢复中心；当前设置为只读安全默认值"
            : "Settings are damaged: open Recovery; safe defaults are read-only";
    }

    private void InitializeFeatures()
    {
        _hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);
        _subclassProc=WindowSubclassProc;Native.SetWindowSubclass(_hwnd,_subclassProc,1,UIntPtr.Zero);
        RegisterGlobalHotkeys();AddTrayIcon();
        _hardwareCapabilityService=new HardwareCapabilityService(new WindowsHardwareCapabilityProbe(
            ()=>_trayAdded,()=>_globalHotkeysAvailable));
        InitializeAdvancedFeatures();ApplyFeatureSettingsPresentation(_featureSettings);
        AppWindow.Changed+=AppWindow_Changed;AppWindow.Closing+=AppWindow_Closing;
    }

    private void ApplyExperienceMode(ExperienceMode mode)
    {
        _featureSettings.ExperienceMode=mode;
        var professional=mode==ExperienceMode.Professional;
        ProfessionalModeControls.Visibility=professional?Visibility.Visible:Visibility.Collapsed;
        ExperienceModeText.Text=professional
            ?(IsChinese?"专业":"Professional")
            :(IsChinese?"简单":"Simple");
        ExperienceModeButton.IsChecked=professional;
        ApplyCapabilityPresentation();
    }

    private void ExperienceModeButton_Click(object sender,RoutedEventArgs e)
    {
        if (!CanPersistSettings)
        {
            ShowCorruptSettingsWarning();
            return;
        }
        var mode=ExperienceModeButton.IsChecked==true
            ?ExperienceMode.Professional
            :ExperienceMode.Simple;
        ApplyExperienceMode(mode);
        TrySaveSettings(_featureSettings);
    }

    internal void ApplyFeatureSettings(PowerModeSettings settings)
    {
        _featureSettings=settings;
        ApplyFeatureSettingsPresentation(settings);
        if (!CanPersistSettings)
        {
            _featureTimer?.Stop();
            ShowCorruptSettingsWarning();
            return;
        }
        ConfigureFeatureTimer(settings);
        _=ConfigureMonitoringAsync();ApplySystemSettings(settings);
        _=BackupSettingsIfChangedAsync(settings.ConfigurationBackupCount, "settings-save");
        _=RefreshRecommendationAsync();
    }

    private void ApplyFeatureSettingsPresentation(PowerModeSettings settings)
    {
        ApplyExperienceMode(settings.ExperienceMode);
        AutoQuickToggle.IsChecked=settings.AutoSwitchEnabled;LiveQuickToggle.IsChecked=settings.RealTimeMonitoringEnabled;
        OverflowAutoQuickToggle.IsChecked=settings.AutoSwitchEnabled;OverflowLiveQuickToggle.IsChecked=settings.RealTimeMonitoringEnabled;
        if(!settings.TemperatureProtectionEnabled){_temperatureProtectionActive=false;_handlingTemperature=false;}
    }

    private void ConfigureFeatureTimer(PowerModeSettings settings)
    {
        _featureTimer?.Stop();_featureTimer??=new DispatcherTimer();_featureTimer.Tick-=FeatureTimer_Tick;_featureTimer.Interval=TimeSpan.FromSeconds(Math.Max(10,settings.MonitorIntervalSeconds));_featureTimer.Tick+=FeatureTimer_Tick;_featureTimer.Start();
    }

    private sealed class MainWindowSettingsActivationEffects(MainWindow owner)
        : ISettingsActivationEffects
    {
        public void StartFeatureTimer() => owner.ConfigureFeatureTimer(owner._featureSettings);
        public void ConfigureMonitoring() => _ = owner.ConfigureMonitoringAsync();
        public void ConfigureStartup() => owner.ApplySystemSettings(owner._featureSettings);
        public Task CreateStartupBackupAsync(CancellationToken cancellationToken) =>
            owner.BackupSettingsIfChangedAsync(
                owner._featureSettings.ConfigurationBackupCount,
                "startup");
        public void StartAutomation() => _ = owner.RefreshRecommendationAsync();
        public Task ApplyLastModeAsync(CancellationToken cancellationToken) =>
            owner.ApplyLastModeOnStartupAsync(cancellationToken);
        public Task CheckUpdatesAsync(CancellationToken cancellationToken) =>
            owner.RunStartupFeaturesAsync(cancellationToken);
    }
    private async void FeatureTimer_Tick(object? sender,object e)
    {
        if(_featureTickInProgress||_modeSwitchInProgress)return;
        _featureTickInProgress=true;
        try
        {
            await RefreshRecommendationAsync();
            if(await RunAutomationTickAsync())return;
            if(_featureSettings.RealTimeMonitoringEnabled&&AppWindow.IsVisible)await RefreshStatusAsync();
        }
        finally
        {
            _featureTickInProgress=false;
        }
    }
    private string DetermineAutomaticMode()
    {
        var names=Process.GetProcesses().Select(p=>{try{return p.ProcessName;}catch{return string.Empty;}}).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(SplitProcesses(_featureSettings.RemoteProcesses).Any(names.Contains))return "remote";if(SplitProcesses(_featureSettings.PerformanceProcesses).Any(names.Contains))return "high";
        return Native.GetSystemPowerStatus(out var power)&&power.ACLineStatus==0?"saver":"balanced";
    }
    private static IEnumerable<string> SplitProcesses(string text)=>text.Split([',',';','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(x=>Path.GetFileNameWithoutExtension(x)??x);

    private void AppWindow_Changed(Microsoft.UI.Windowing.AppWindow sender,Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if(args.DidPresenterChange&&sender.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p&&p.State==Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)sender.Hide();
    }
    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender,Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_settingsWindow is { } settingsWindow)
        {
            args.Cancel = true;
            try
            {
                await _settingsOwnerShutdownCoordinator.RequestAsync(
                    settingsWindow.RequestCloseAsync,
                    () =>
                    {
                        if (ReferenceEquals(_settingsWindow, settingsWindow))
                            _settingsWindow = null;
                        Close();
                    });
            }
            catch (Exception exception)
            {
                AppendLog($"Settings close: {exception.Message}");
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusText.Text = IsChinese
                    ? "设置窗口未能安全关闭。"
                    : "The Settings window could not be closed safely.";
                return;
            }
            return;
        }

        if (!_closingAfterRestore &&
            _featureSettings.RestorePlanOnExit &&
            _startupPowerState is { } startupState)
        {
            args.Cancel = true;
            if (_exitRestoreInProgress)
                return;

            _exitRestoreInProgress = true;
            var decision = await _exitRestoreCoordinator.RestoreLaunchStateAsync(
                startupState);
            _exitRestoreInProgress = false;
            if (!decision.ShouldClose)
            {
                if (decision.Operation is { } failedOperation)
                {
                    AppendLog(failedOperation.DiagnosticSummary);
                    PresentModeSwitchResult(failedOperation);
                }
                else
                {
                    PresentStartupRecovery(new(
                        true,
                        null,
                        null,
                        decision.Error ?? "The launch-state restore could not be completed."));
                }
                return;
            }

            if (decision.Operation is { } completedOperation)
                AppendLog(completedOperation.DiagnosticSummary);
            _closingAfterRestore = true;
            Close();
            return;
        }
        _capabilityPresentationLifetime.Dispose();
        _hardwareCapabilityService?.Dispose();
        DpiAwareWindowSizer.SavePlacement(this);
        CleanupNativeFeatures();
    }

    private void RegisterGlobalHotkeys()
    {
        _globalHotkeysAvailable=true;
        for(var id=1;id<=4;id++)
            _globalHotkeysAvailable&=Native.RegisterHotKey(
                _hwnd,id,ModControl|ModAlt,(uint)(0x30+id));
    }
    private void CleanupNativeFeatures()
    {
        _featureTimer?.Stop();_settingsWindow=null;for(var id=1;id<=4;id++)Native.UnregisterHotKey(_hwnd,id);if(_trayAdded){var data=CreateTrayData();Native.Shell_NotifyIcon(2,ref data);_trayAdded=false;}if(_subclassProc is not null)Native.RemoveWindowSubclass(_hwnd,_subclassProc,1);DisposeAdvancedFeatures();
    }
    private IntPtr WindowSubclassProc(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data)
    {
        if(msg==WmHotkey){var mode=wParam.ToInt32() switch{1=>"remote",2=>"saver",3=>"balanced",4=>"high",_=>string.Empty};if(mode.Length>0)DispatcherQueue.TryEnqueue(()=>_=RunModeWithContextAsync(mode,new SwitchRequestContext("hotkey",AllowPreview:false)));return IntPtr.Zero;}
        if(msg==WmTray){var action=(uint)(lParam.ToInt64()&0xFFFF);if(action==WmLButtonDblClk)DispatcherQueue.TryEnqueue(ShowFromTray);else if(action==WmRButtonUp)DispatcherQueue.TryEnqueue(ShowTrayMenu);return IntPtr.Zero;}
        return Native.DefSubclassProc(hwnd,msg,wParam,lParam);
    }
    private Native.NotifyIconData CreateTrayData()=>new(){cbSize=(uint)Marshal.SizeOf<Native.NotifyIconData>(),hWnd=_hwnd,uID=1,uFlags=1|2|4,uCallbackMessage=WmTray,hIcon=Native.LoadIcon(IntPtr.Zero,(IntPtr)32512),szTip="PowerMode"};
    private void AddTrayIcon(){var data=CreateTrayData();_trayAdded=Native.Shell_NotifyIcon(0,ref data);}
    private void ShowTrayNotification(string title,string message)
    {
        if(!_featureSettings.NotificationsEnabled||!_trayAdded)return;var data=CreateTrayData();data.uFlags=0x10;data.szInfoTitle=title;data.szInfo=message;data.dwInfoFlags=1;Native.Shell_NotifyIcon(1,ref data);
    }
    private void ShowFromTray(){AppWindow.Show();if(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)p.Restore();Activate();}
    private void ShowTrayMenu()
    {
        var menu=Native.CreatePopupMenu();Native.AppendMenu(menu,0,1,"Remote");Native.AppendMenu(menu,0,2,"Saver");Native.AppendMenu(menu,0,3,"Balanced");Native.AppendMenu(menu,0,4,"High performance");Native.AppendMenu(menu,0x800,0,null);Native.AppendMenu(menu,0,10,"Show PowerMode");Native.AppendMenu(menu,0,11,"Exit");Native.GetCursorPos(out var point);Native.SetForegroundWindow(_hwnd);var command=Native.TrackPopupMenu(menu,0x100|0x2,point.X,point.Y,0,_hwnd,IntPtr.Zero);Native.DestroyMenu(menu);
        var mode=command switch{1=>"remote",2=>"saver",3=>"balanced",4=>"high",_=>string.Empty};if(mode.Length>0)_=RunModeWithContextAsync(mode,new SwitchRequestContext("tray",AllowPreview:false));else if(command==10)ShowFromTray();else if(command==11)Close();
    }

    internal async Task ApplyCustomProfileAsync(CustomPowerProfile profile)
    {
        if (!CanPersistSettings)
        {
            ShowCorruptSettingsWarning();
            return;
        }
        if(_modeSwitchInProgress)return;
        if (await RunTargetCoreAsync(
            PowerModeTarget.ForCustom(CustomPowerProfileSnapshot.FromSettings(profile)),
            cpuMaximumPercent: null,
            disableWifi: false,
            new SwitchRequestContext(
                "custom-profile",
                profile.Name,
                AllowPreview: false),
            profile))
        {
            SleepValue.Text = CustomProfileSleepDisplay.Format(profile.DisplayOffSeconds);
        }
    }
    internal async Task VerifyWithSummaryAsync()
    {
        var availability = await GetRecoveryService().GetLastOperationAvailabilityAsync();
        if (!string.IsNullOrWhiteSpace(availability.Error))
        {
            PresentStartupRecovery(new(
                true,
                null,
                null,
                availability.Error));
            return;
        }
        if (availability.Record is { } record)
        {
            var result = await VerifyLastOperationAsync(
                record.OperationId,
                CancellationToken.None);
            if (result.CurrentState is { } currentState)
                ApplyPowerModeState(currentState);
            StatusText.Text = result.Error ?? (result.MatchesCriticalExpectations
                ? (IsChinese ? "最近电源操作已验证。" : "The last power operation is verified.")
                : (IsChinese ? "当前状态与关键预期不匹配。" : "Current state does not match critical expectations."));
            StatusBar.Severity = result.MatchesCriticalExpectations
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;
            StatusBar.IsOpen = true;
            return;
        }

        var current = await _powerModeBackend.ReadStateAsync(Guid.NewGuid());
        AppendBackendDiagnostics(current.Operation);
        if (current.State is { } state)
            ApplyPowerModeState(state);
        StatusText.Text = current.State is null
            ? (IsChinese ? "无法可靠验证当前状态。" : "The current state could not be verified reliably.")
            : (IsChinese ? "当前电源状态可读取且契约有效。" : "The current power state is readable and contract-valid.");
        StatusBar.Severity = current.State is null
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Success;
    }
    private async void RepairButton_Click(object sender,RoutedEventArgs e)
    {
        if (_lastCustomProfile is not null && _activeModeKey.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
        {
            await ApplyCustomProfileAsync(_lastCustomProfile);
            return;
        }
        await RunModeAsync(_activeModeKey is "remote" or "saver" or "balanced" or "high"
            ? _activeModeKey
            : "balanced");
    }
    private async void WifiOnButton_Click(object sender,RoutedEventArgs e)
    {
        const string command = "Get-NetAdapter | Where-Object { $_.Name -like '*Wi*' -or $_.InterfaceDescription -match 'Wireless|Wi-Fi|WLAN' } | Enable-NetAdapter -Confirm:$false";
        var result = await _processRunner.RunAsync(new ProcessExecutionRequest(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
            TimeSpan.FromSeconds(10)));
        AppendProcessDiagnostics("wifi-enable", result);
        StatusText.Text=result.Succeeded?(IsChinese?"WiFi 已恢复":"WiFi restored"):(IsChinese?"WiFi 恢复失败":"WiFi restore failed");StatusBar.Severity=result.Succeeded?InfoBarSeverity.Success:InfoBarSeverity.Error;
    }
    private void FeaturesButton_Click(object sender,RoutedEventArgs e)
    {
        if(!CanPersistSettings)
        {
            ShowCorruptSettingsWarning();
            OpenRecoveryCenterButton_Click(sender,e);
            return;
        }
        if(_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        var window=new SettingsWindow(this,_featureSettings,IsChinese);
        _settingsWindow=window;
        window.Closed+=(_,_)=>
        {
            if(ReferenceEquals(_settingsWindow,window))
                _settingsWindow=null;
        };
        window.Activate();
    }
    private void AutoQuickToggle_Click(object sender,RoutedEventArgs e){if(!CanPersistSettings){ShowCorruptSettingsWarning();return;}_featureSettings.AutoSwitchEnabled=AutoQuickToggle.IsChecked==true;TrySaveSettings(_featureSettings);ApplyFeatureSettings(_featureSettings);StatusText.Text=IsChinese?($"自动切换已{(_featureSettings.AutoSwitchEnabled?"开启":"关闭")}"):($"Automatic switching {(_featureSettings.AutoSwitchEnabled?"enabled":"disabled")}");StatusBar.Severity=InfoBarSeverity.Success;}
    private void LiveQuickToggle_Click(object sender,RoutedEventArgs e){if(!CanPersistSettings){ShowCorruptSettingsWarning();return;}_featureSettings.RealTimeMonitoringEnabled=LiveQuickToggle.IsChecked==true;TrySaveSettings(_featureSettings);ApplyFeatureSettings(_featureSettings);StatusText.Text=IsChinese?($"实时监控已{(_featureSettings.RealTimeMonitoringEnabled?"开启":"关闭")}"):($"Live monitoring {(_featureSettings.RealTimeMonitoringEnabled?"enabled":"disabled")}");StatusBar.Severity=InfoBarSeverity.Success;}
    private void OverflowAutoQuickToggle_Click(object sender,RoutedEventArgs e)
    {
        AutoQuickToggle.IsChecked=OverflowAutoQuickToggle.IsChecked;
        AutoQuickToggle_Click(sender,e);
    }
    private void OverflowLiveQuickToggle_Click(object sender,RoutedEventArgs e)
    {
        LiveQuickToggle.IsChecked=OverflowLiveQuickToggle.IsChecked;
        LiveQuickToggle_Click(sender,e);
    }

    private async void RootGrid_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key==VirtualKey.F5){e.Handled=true;await RefreshStatusAsync();return;}var focused=FocusManager.GetFocusedElement(RootGrid.XamlRoot);if(focused is TextBox or NumberBox)return;var mode=e.Key switch{VirtualKey.Number1 or VirtualKey.NumberPad1=>"remote",VirtualKey.Number2 or VirtualKey.NumberPad2=>"saver",VirtualKey.Number3 or VirtualKey.NumberPad3=>"balanced",VirtualKey.Number4 or VirtualKey.NumberPad4=>"high",_=>string.Empty};if(mode.Length>0){e.Handled=true;await RunModeAsync(mode);}
    }

    private async Task DetectCapabilitiesAndRefreshPresentationAsync()
    {
        if(_hardwareCapabilityService is null)return;
        try
        {
            var capabilities=await Task
                .Run(()=>_hardwareCapabilityService.DetectAsync(
                    TimeSpan.FromSeconds(2),_capabilityPresentationLifetime.Token))
                .ConfigureAwait(false);
            _monitoringService.UpdateCapabilities(capabilities);
            void Apply()
            {
                _capabilityPresentationLifetime.TryApply(()=>
                {
                    _hardwareCapabilities=capabilities;
                    ApplyCapabilityPresentation();
                    _=RefreshRecommendationAsync();
                });
            }
            if(_capabilityPresentationLifetime.IsClosing)return;
            if(DispatcherQueue.HasThreadAccess)Apply();
            else DispatcherQueue.TryEnqueue(Apply);
        }
        catch
        {
            // Capability detection is best-effort and must never affect core mode controls.
        }
    }

    private void ApplyCapabilityPresentation()
    {
        var policy=CapabilityVisibilityPolicy.Evaluate(
            _featureSettings.ExperienceMode,_hardwareCapabilities,IsChinese);
        ApplyCapabilityPresentation(
            GpuStatusCard,policy[CapabilityFeature.GpuTelemetry]);
        ApplyCapabilityPresentation(
            BrightnessStatusCard,policy[CapabilityFeature.Brightness]);
        ApplyCapabilityPresentation(
            WifiOnButton,policy[CapabilityFeature.WifiControl]);
        ApplyCapabilityPresentation(
            RemoteNoWifiButton,policy[CapabilityFeature.WifiControl]);
        ApplyResponsiveLayout(RootGrid.ActualWidth,RootGrid.ActualHeight);
        _settingsWindow?.ApplyCapabilityPresentation(
            _featureSettings.ExperienceMode,_hardwareCapabilities);
    }

    private static void ApplyCapabilityPresentation(
        FrameworkElement element,
        FeaturePresentation presentation)
    {
        var state=CapabilityControlPresentation.Map(presentation);
        element.Visibility=state.IsVisible?Visibility.Visible:Visibility.Collapsed;
        if(element is Control control)control.IsEnabled=state.IsEnabled;
        ToolTipService.SetToolTip(element,state.ToolTip);
        AutomationProperties.SetHelpText(element,state.HelpText);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct NotifyIconData{public uint cbSize;public IntPtr hWnd;public uint uID,uFlags,uCallbackMessage;public IntPtr hIcon;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string szTip;public uint dwState,dwStateMask;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string szInfo;public uint uTimeoutOrVersion;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string szInfoTitle;public uint dwInfoFlags;public Guid guidItem;public IntPtr hBalloonIcon;}
        [StructLayout(LayoutKind.Sequential)]internal struct Point{public int X,Y;}
        [StructLayout(LayoutKind.Sequential)]internal struct SystemPowerStatus{public byte ACLineStatus,BatteryFlag,BatteryLifePercent,SystemStatusFlag;public uint BatteryLifeTime,BatteryFullLifeTime;}
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]internal delegate IntPtr SubclassProc(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data);
        [DllImport("comctl32.dll")]internal static extern bool SetWindowSubclass(IntPtr hwnd,SubclassProc proc,UIntPtr id,UIntPtr data);
        [DllImport("comctl32.dll")]internal static extern bool RemoveWindowSubclass(IntPtr hwnd,SubclassProc proc,UIntPtr id);
        [DllImport("comctl32.dll")]internal static extern IntPtr DefSubclassProc(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")]internal static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
        [DllImport("user32.dll")]internal static extern bool UnregisterHotKey(IntPtr hwnd,int id);
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)]internal static extern bool Shell_NotifyIcon(uint message,ref NotifyIconData data);
        [DllImport("user32.dll")]internal static extern IntPtr LoadIcon(IntPtr instance,IntPtr icon);
        [DllImport("user32.dll")]internal static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern bool AppendMenu(IntPtr menu,uint flags,uint id,string? text);
        [DllImport("user32.dll")]internal static extern uint TrackPopupMenu(IntPtr menu,uint flags,int x,int y,int reserved,IntPtr hwnd,IntPtr rect);
        [DllImport("user32.dll")]internal static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")]internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")]internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("kernel32.dll")]internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    }
}
