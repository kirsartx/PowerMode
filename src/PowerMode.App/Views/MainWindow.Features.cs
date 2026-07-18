using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.System;

namespace PowerModeWinUI;

public sealed partial class MainWindow
{
    internal const string SaverGuid="a1841308-3541-4fab-bc81-f71556f20b4a";
    internal const string BalancedGuid="381b4222-f694-41f0-9685-ff5bb260df2e";
    internal const string HighGuid="8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const uint WmHotkey=0x0312,WmTray=0x8000+100,WmLButtonDblClk=0x0203,WmRButtonUp=0x0205;
    private const uint ModAlt=0x0001,ModControl=0x0002;
    private PowerModeSettings _featureSettings=SettingsStore.Load();
    private DispatcherTimer? _featureTimer;
    private string _startupPlanGuid=string.Empty,_lastAutoMode=string.Empty;
    private CustomPowerProfile? _lastCustomProfile;
    private bool _customProfileInProgress;
    private SettingsWindow? _settingsWindow;
    private IntPtr _hwnd;
    private Native.SubclassProc? _subclassProc;
    private bool _trayAdded;

    private void InitializeFeatures()
    {
        _hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);_startupPlanGuid=GetActivePlanGuidFast();
        _subclassProc=WindowSubclassProc;Native.SetWindowSubclass(_hwnd,_subclassProc,1,UIntPtr.Zero);
        RegisterGlobalHotkeys();AddTrayIcon();InitializeAdvancedFeatures();ApplyFeatureSettings(_featureSettings);
        AppWindow.Changed+=AppWindow_Changed;AppWindow.Closing+=AppWindow_Closing;
    }

    private void ApplyExperienceMode(ExperienceMode mode)
    {
        _featureSettings.ExperienceMode=mode;
        var professional=mode==ExperienceMode.Professional;
        ProfessionalQuickActions.Visibility=professional?Visibility.Visible:Visibility.Collapsed;
        ProfessionalModeControls.Visibility=professional?Visibility.Visible:Visibility.Collapsed;
        ProfessionalLogPanel.Visibility=professional?Visibility.Visible:Visibility.Collapsed;
        MainContentGrid.ColumnSpacing=professional?16:0;
        MainContentGrid.ColumnDefinitions[0].Width=
            professional?new GridLength(390):new GridLength(1,GridUnitType.Star);
        MainContentGrid.ColumnDefinitions[1].Width=
            professional?new GridLength(1,GridUnitType.Star):new GridLength(0);
        ExperienceModeText.Text=professional
            ?(IsChinese?"专业":"Professional")
            :(IsChinese?"简单":"Simple");
        ExperienceModeButton.IsChecked=professional;
    }

    private void ExperienceModeButton_Click(object sender,RoutedEventArgs e)
    {
        var mode=ExperienceModeButton.IsChecked==true
            ?ExperienceMode.Professional
            :ExperienceMode.Simple;
        ApplyExperienceMode(mode);
        SettingsStore.Save(_featureSettings);
    }

    internal void ApplyFeatureSettings(PowerModeSettings settings)
    {
        _featureSettings=settings;
        ApplyExperienceMode(settings.ExperienceMode);
        AutoQuickToggle.IsChecked=settings.AutoSwitchEnabled;LiveQuickToggle.IsChecked=settings.RealTimeMonitoringEnabled;
        if(!settings.TemperatureProtectionEnabled){_temperatureProtectionActive=false;_handlingTemperature=false;}
        _featureTimer?.Stop();_featureTimer??=new DispatcherTimer();_featureTimer.Tick-=FeatureTimer_Tick;_featureTimer.Interval=TimeSpan.FromSeconds(Math.Max(10,settings.MonitorIntervalSeconds));_featureTimer.Tick+=FeatureTimer_Tick;if(settings.AutoSwitchEnabled||settings.RealTimeMonitoringEnabled||settings.TemperatureProtectionEnabled)_featureTimer.Start();
        _=ConfigureMonitoringAsync();ApplySystemSettings(settings);
    }
    private async void FeatureTimer_Tick(object? sender,object e)
    {
        if(_busy||_modeSwitchInProgress)return;if(await RunAutomationTickAsync())return;if(_featureSettings.RealTimeMonitoringEnabled&&AppWindow.IsVisible)await RefreshStatusAsync();
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
    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender,Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        DpiAwareWindowSizer.SavePlacement(this);
        if(_featureSettings.RestorePlanOnExit&&!string.IsNullOrEmpty(_startupPlanGuid)){try{using var p=Process.Start(new ProcessStartInfo("powercfg.exe",$"/setactive {_startupPlanGuid}"){UseShellExecute=false,CreateNoWindow=true});p?.WaitForExit(2500);}catch{}}
        CleanupNativeFeatures();
    }

    private void RegisterGlobalHotkeys(){for(var id=1;id<=4;id++)Native.RegisterHotKey(_hwnd,id,ModControl|ModAlt,(uint)(0x30+id));}
    private void CleanupNativeFeatures()
    {
        _featureTimer?.Stop();try{_settingsWindow?.Close();}catch{} _settingsWindow=null;for(var id=1;id<=4;id++)Native.UnregisterHotKey(_hwnd,id);if(_trayAdded){var data=CreateTrayData();Native.Shell_NotifyIcon(2,ref data);_trayAdded=false;}if(_subclassProc is not null)Native.RemoveWindowSubclass(_hwnd,_subclassProc,1);DisposeAdvancedFeatures();
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

    internal string GetActivePlanGuidFast()
    {
        try{using var p=Process.Start(new ProcessStartInfo("powercfg.exe","/getactivescheme"){UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true});if(p is null)return string.Empty;var output=p.StandardOutput.ReadToEnd();p.WaitForExit();return Regex.Match(output,"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}").Value.ToLowerInvariant();}catch{return string.Empty;}
    }
    internal async Task RestorePlanAsync(string guid){if(guid.Length>0)await RunPowerCfgAsync("/setactive",guid);}
    private async Task<bool> RunPowerCfgAsync(params string[] args)
    {
        try{Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var oem=Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);var psi=new ProcessStartInfo("powercfg.exe"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,StandardOutputEncoding=oem,StandardErrorEncoding=oem};foreach(var arg in args)psi.ArgumentList.Add(arg);using var p=Process.Start(psi);if(p is null)return false;var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync();var o=await output;var e=await error;if(o.Length>0)AppendLog(o);if(e.Length>0)AppendLog(e);return p.ExitCode==0;}catch(Exception ex){AppendLog(ex.Message);return false;}
    }

    internal async Task ApplyCustomProfileAsync(CustomPowerProfile profile)
    {
        if(_busy||_modeSwitchInProgress)return;
        var previous=GetActivePlanGuidFast();var previousMode=_featureSettings.LastMode;var stopwatch=Stopwatch.StartNew();var succeeded=false;string? error=null;
        _busy=true;_customProfileInProgress=true;SetControlsEnabled(false);StatusText.Text=IsChinese?$"正在应用：{profile.Name}":$"Applying: {profile.Name}";
        try
        {
            var dcCpu=profile.UseSeparateBatteryValues?profile.BatteryCpuMax:profile.CpuMax;var dcBrightness=profile.UseSeparateBatteryValues?profile.BatteryBrightness:profile.Brightness;var dcDisplay=profile.UseSeparateBatteryValues?profile.BatteryDisplayOffSeconds:profile.DisplayOffSeconds;
            var list=new List<string[]>{new[]{"/setactive",SaverGuid},new[]{"/setacvalueindex",SaverGuid,"SUB_PROCESSOR","PROCTHROTTLEMAX",profile.CpuMax.ToString()},new[]{"/setdcvalueindex",SaverGuid,"SUB_PROCESSOR","PROCTHROTTLEMAX",dcCpu.ToString()},new[]{"/setacvalueindex",SaverGuid,"SUB_PROCESSOR","PROCTHROTTLEMIN",profile.CpuMin.ToString()},new[]{"/setdcvalueindex",SaverGuid,"SUB_PROCESSOR","PROCTHROTTLEMIN",Math.Min(profile.CpuMin,dcCpu).ToString()},new[]{"/setacvalueindex",SaverGuid,"SUB_PROCESSOR","PERFBOOSTMODE",profile.DisableBoost?"0":"2"},new[]{"/setdcvalueindex",SaverGuid,"SUB_PROCESSOR","PERFBOOSTMODE",profile.DisableBoost?"0":"2"},new[]{"/setacvalueindex",SaverGuid,"SUB_VIDEO","VIDEONORMALLEVEL",profile.Brightness.ToString()},new[]{"/setdcvalueindex",SaverGuid,"SUB_VIDEO","VIDEONORMALLEVEL",dcBrightness.ToString()},new[]{"/setacvalueindex",SaverGuid,"SUB_VIDEO","VIDEOIDLE",profile.DisplayOffSeconds.ToString()},new[]{"/setdcvalueindex",SaverGuid,"SUB_VIDEO","VIDEOIDLE",dcDisplay.ToString()},new[]{"/setacvalueindex",SaverGuid,"SUB_SLEEP","STANDBYIDLE","0"},new[]{"/setdcvalueindex",SaverGuid,"SUB_SLEEP","STANDBYIDLE","0"},new[]{"/setactive",SaverGuid}};
            foreach(var command in list)if(!await RunPowerCfgAsync(command)){error=IsChinese?"预设命令失败":"Profile command failed";await RestorePlanAsync(previous);StatusText.Text=IsChinese?"预设应用失败，已回滚":"Profile failed; rolled back";StatusBar.Severity=InfoBarSeverity.Error;return;}
            _lastCustomProfile=profile;_featureSettings.LastMode="saver";try{SettingsStore.Save(_featureSettings);}catch(Exception ex){AppendLog($"Settings save: {ex.Message}");}
            ModeValue.Text=profile.Name;CpuValue.Text=$"{profile.CpuMax}%";BrightnessValue.Text=$"{profile.Brightness}%";SleepValue.Text=$"0s / {profile.DisplayOffSeconds}s";UpdateActiveMode(string.Empty);StatusText.Text=IsChinese?$"已应用：{profile.Name}":$"Applied: {profile.Name}";StatusBar.Severity=InfoBarSeverity.Success;succeeded=true;
        }
        catch(Exception ex)
        {
            error=ex.Message;AppendLog(ex.ToString());await RestorePlanAsync(previous);StatusText.Text=IsChinese?"预设应用失败，已回滚":"Profile failed; rolled back";StatusBar.Severity=InfoBarSeverity.Error;
        }
        finally
        {
            stopwatch.Stop();
            if(_featureSettings.OperationHistoryEnabled)await RecordSwitchHistoryAsync(new SwitchHistoryEntry{Timestamp=DateTimeOffset.Now,PreviousMode=previousMode,TargetMode=$"profile:{profile.Name}",Trigger="custom-profile",Reason=profile.Name,Succeeded=succeeded,DurationMilliseconds=stopwatch.ElapsedMilliseconds,ErrorMessage=error});
            _customProfileInProgress=false;_busy=false;SetControlsEnabled(true);
        }
    }
    internal async Task VerifyWithSummaryAsync(){var result=await RunCliAsync("verify");if(result.ExitCode!=0)return;var ok=Regex.Matches(result.Output,@"\[OK\]").Count;var bad=Regex.Matches(result.Output,@"\[!!?\]").Count;StatusText.Text=IsChinese?$"校验：{ok} 项正常，{bad} 项需修复":$"Verification: {ok} passed, {bad} need repair";StatusBar.Severity=bad==0?InfoBarSeverity.Success:InfoBarSeverity.Warning;}
    private async void RepairButton_Click(object sender,RoutedEventArgs e){var text=ModeValue.Text.ToLowerInvariant();if(_lastCustomProfile is not null&&text==_lastCustomProfile.Name.ToLowerInvariant()){await ApplyCustomProfileAsync(_lastCustomProfile);return;}var mode=text.Contains("remote")||text.Contains("远程")?"remote":text.Contains("saver")||text.Contains("低功耗")?"saver":text.Contains("high")||text.Contains("高性能")?"high":"balanced";await RunModeAsync(mode);}
    private async void WifiOnButton_Click(object sender,RoutedEventArgs e)
    {
        try{var command="Get-NetAdapter | Where-Object { $_.Name -like '*Wi*' -or $_.InterfaceDescription -match 'Wireless|Wi-Fi|WLAN' } | Enable-NetAdapter -Confirm:$false";using var p=Process.Start(new ProcessStartInfo("powershell.exe") {UseShellExecute=true,Verb="runas",Arguments=$"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",WindowStyle=ProcessWindowStyle.Hidden});if(p is null)return;await p.WaitForExitAsync();StatusText.Text=p.ExitCode==0?(IsChinese?"WiFi 已恢复":"WiFi restored"):(IsChinese?"WiFi 恢复失败":"WiFi restore failed");StatusBar.Severity=p.ExitCode==0?InfoBarSeverity.Success:InfoBarSeverity.Error;}catch(Win32Exception){StatusText.Text=IsChinese?"已取消管理员授权":"Administrator request cancelled";}
    }
    private void FeaturesButton_Click(object sender,RoutedEventArgs e){if(_settingsWindow is not null){_settingsWindow.Activate();return;}_settingsWindow=new SettingsWindow(this,_featureSettings,IsChinese);_settingsWindow.Closed+=(_,_)=>_settingsWindow=null;_settingsWindow.Activate();}
    private void AutoQuickToggle_Click(object sender,RoutedEventArgs e){_featureSettings.AutoSwitchEnabled=AutoQuickToggle.IsChecked==true;SettingsStore.Save(_featureSettings);ApplyFeatureSettings(_featureSettings);StatusText.Text=IsChinese?($"自动切换已{(_featureSettings.AutoSwitchEnabled?"开启":"关闭")}"):($"Automatic switching {(_featureSettings.AutoSwitchEnabled?"enabled":"disabled")}");StatusBar.Severity=InfoBarSeverity.Success;}
    private void LiveQuickToggle_Click(object sender,RoutedEventArgs e){_featureSettings.RealTimeMonitoringEnabled=LiveQuickToggle.IsChecked==true;SettingsStore.Save(_featureSettings);ApplyFeatureSettings(_featureSettings);StatusText.Text=IsChinese?($"实时监控已{(_featureSettings.RealTimeMonitoringEnabled?"开启":"关闭")}"):($"Live monitoring {(_featureSettings.RealTimeMonitoringEnabled?"enabled":"disabled")}");StatusBar.Severity=InfoBarSeverity.Success;}

    private async void RootGrid_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key==VirtualKey.F5){e.Handled=true;if(!_busy)await RefreshStatusAsync();return;}var focused=FocusManager.GetFocusedElement(RootGrid.XamlRoot);if(focused is TextBox or NumberBox)return;var mode=e.Key switch{VirtualKey.Number1 or VirtualKey.NumberPad1=>"remote",VirtualKey.Number2 or VirtualKey.NumberPad2=>"saver",VirtualKey.Number3 or VirtualKey.NumberPad3=>"balanced",VirtualKey.Number4 or VirtualKey.NumberPad4=>"high",_=>string.Empty};if(mode.Length>0){e.Handled=true;await RunModeAsync(mode);}
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
