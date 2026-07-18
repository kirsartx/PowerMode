using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text;
using Windows.Graphics;

namespace PowerModeWinUI;

public sealed partial class MainWindow : Window
{
    private readonly string _cliPath;
    private readonly string _scriptPath;
    private string _language = "zh";
    private bool _busy;
    private bool _syncingCpu;
    private readonly bool _startHidden;

    private readonly Dictionary<string, Dictionary<string, string>> _texts = new()
    {
        ["zh"] = new()
        {
            ["Subtitle"]="Hermes 远程电源切换器", ["Refresh"]="刷新", ["Language"]="English", ["Features"]="功能中心", ["Insights"]="洞察",
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
            ["Subtitle"]="Hermes remote power switcher", ["Refresh"]="Refresh", ["Language"]="中文", ["Features"]="Features", ["Insights"]="Insights",
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
        InitializeComponent();
        ConfigureWindow();
        _cliPath = FindCliPath();
        _scriptPath = PrepareCachedScript(_cliPath);
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
        await RefreshStatusAsync();
        if (_featureSettings.ApplyLastModeOnStartup && !string.IsNullOrWhiteSpace(_featureSettings.LastMode))
            await RunModeWithContextAsync(_featureSettings.LastMode,new SwitchRequestContext("startup",AllowPreview:false));
        await RunStartupFeaturesAsync();
    }

    private void ConfigureWindow()
    {
        if(!DpiAwareWindowSizer.TryRestore(this,960,650))
            DpiAwareWindowSizer.Resize(this,1120,760,960,650,center:true);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact=e.NewSize.Width<1040;
        RootGrid.Padding=compact?new Thickness(20,18,20,18):new Thickness(28,22,28,22);
        var visibility=compact?Visibility.Collapsed:Visibility.Visible;
        AutoQuickText.Visibility=visibility;LiveQuickText.Visibility=visibility;RefreshButtonText.Visibility=visibility;
        FeaturesButtonText.Visibility=visibility;InsightsButtonText.Visibility=visibility;
    }

    internal string T(string key) => _texts[_language].TryGetValue(key, out var value) ? value : key;
    internal bool IsChinese => _language == "zh";

    private void ApplyLanguage()
    {
        SubtitleText.Text=T("Subtitle"); RefreshButtonText.Text=T("Refresh"); FeaturesButtonText.Text=T("Features");InsightsButtonText.Text=T("Insights"); LanguageButton.Content=T("Language");LastUpdatedText.Text=string.Format(T("LastUpdated"),"—");
        AutoQuickText.Text=T("Auto");LiveQuickText.Text=T("Live");
        AutomationProperties.SetName(RefreshButton,T("Refresh"));AutomationProperties.SetName(FeaturesButton,T("Features"));AutomationProperties.SetName(InsightsButton,T("Insights"));
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
        ToolTipService.SetToolTip(LanguageButton,_language=="zh"?"切换到 English":"Switch to Chinese");
        ToolTipService.SetToolTip(CopyLogButton,_language=="zh"?"复制本次会话日志":"Copy this session log");
        ToolTipService.SetToolTip(ClearLogButton,_language=="zh"?"清空本次会话日志":"Clear this session log");
        ToolTipService.SetToolTip(RemoteButton,_language=="zh"?"切换到远程推荐（快捷键 1）":"Switch to Remote (shortcut 1)");
        ToolTipService.SetToolTip(SaverButton,_language=="zh"?"切换到低功耗（快捷键 2）":"Switch to Saver (shortcut 2)");
        ToolTipService.SetToolTip(BalancedButton,_language=="zh"?"切换到平衡（快捷键 3）":"Switch to Balanced (shortcut 3)");
        ToolTipService.SetToolTip(HighButton,_language=="zh"?"切换到高性能（快捷键 4）":"Switch to High performance (shortcut 4)");
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

    private static string PrepareCachedScript(string cliPath)
    {
        if (string.IsNullOrWhiteSpace(cliPath) || !File.Exists(cliPath)) return string.Empty;
        try
        {
            const string marker = "### POWERSHELL_PAYLOAD_BELOW ###";
            var lines = File.ReadAllLines(cliPath, Encoding.UTF8);
            var index = Array.FindIndex(lines, line => line.Trim() == marker);
            if (index < 0 || index + 1 >= lines.Length) return string.Empty;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerMode", "Cache");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"PowerModeSwitcher-winui-{File.GetLastWriteTimeUtc(cliPath).Ticks}.ps1");
            if (!File.Exists(path)) File.WriteAllText(path, string.Join(Environment.NewLine, lines[(index + 1)..]), new UTF8Encoding(true));
            return path;
        }
        catch { return string.Empty; }
    }

    private async Task<CommandResult> RunCliAsync(
        string[] args,
        bool manageBusy = true,
        CancellationToken cancellationToken = default,
        bool updateStatus = true)
    {
        if (manageBusy && _busy) return new(-1, string.Empty, "Busy");
        if (string.IsNullOrWhiteSpace(_cliPath) || !File.Exists(_cliPath))
        {
            await ShowMessageAsync("PowerMode", T("CliMissing"));
            return new(1, string.Empty, T("CliMissing"));
        }
        if (manageBusy) { _busy = true; SetControlsEnabled(false, includeModeControls: false); }
        var command = string.Join(' ', args);
        if(updateStatus){StatusText.Text=string.Format(T("Running"),command); StatusBar.Severity=InfoBarSeverity.Informational;} AppendLog($"[{DateTime.Now:HH:mm:ss}]  › PowerModeSwitcher {command}");
        var stopwatch=Stopwatch.StartNew();
        try
        {
            var cached=!string.IsNullOrWhiteSpace(_scriptPath)&&File.Exists(_scriptPath);
            var psi=new ProcessStartInfo(cached?"powershell.exe":"cmd.exe") { WorkingDirectory=Path.GetDirectoryName(_cliPath)!, UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true, StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8 };
            if(cached){ psi.ArgumentList.Add("-NoLogo");psi.ArgumentList.Add("-NoProfile");psi.ArgumentList.Add("-ExecutionPolicy");psi.ArgumentList.Add("Bypass");psi.ArgumentList.Add("-File");psi.ArgumentList.Add(_scriptPath);foreach(var arg in args)psi.ArgumentList.Add(arg); }
            else psi.Arguments=$"/c \"\"{_cliPath}\" {string.Join(' ',args)}\"";
            psi.EnvironmentVariables["PM_NO_PAUSE"]="1";
            using var process=Process.Start(psi); if(process is null)return new(1,string.Empty,"Process start failed");
            var output=new StringBuilder();var error=new StringBuilder();
            var outputTask=PumpOutputAsync(process.StandardOutput,output,isError:false);var errorTask=PumpOutputAsync(process.StandardError,error,isError:true);
            try{await process.WaitForExitAsync(cancellationToken);}
            catch(OperationCanceledException)
            {
                try{if(!process.HasExited)process.Kill(true);}catch{}
                try{await process.WaitForExitAsync();}catch{}
                try{await Task.WhenAll(outputTask,errorTask);}catch{}
                AppendLogLine($"[{DateTime.Now:HH:mm:ss}]  ✕ Cancelled · {stopwatch.Elapsed.TotalSeconds:0.0}s");AppendLogLine(new string('─',48));
                return new(-2,output.ToString(),"Cancelled");
            }
            await Task.WhenAll(outputTask,errorTask);
            var result=new CommandResult(process.ExitCode,output.ToString(),error.ToString());
            AppendLogLine($"[{DateTime.Now:HH:mm:ss}]  {(process.ExitCode==0?"✓":"✕")} Exit {process.ExitCode} · {stopwatch.Elapsed.TotalSeconds:0.0}s");AppendLogLine(new string('─',48));
            if(updateStatus){StatusText.Text=string.Format(process.ExitCode==0?T("Done"):T("Failed"),command,stopwatch.Elapsed.TotalSeconds); StatusBar.Severity=process.ExitCode==0?InfoBarSeverity.Success:InfoBarSeverity.Error;}
            return result;
        }
        catch(Exception ex){AppendLog($"[{DateTime.Now:HH:mm:ss}]  ! {ex.Message}");if(updateStatus){StatusText.Text=string.Format(T("Failed"),command,stopwatch.Elapsed.TotalSeconds);StatusBar.Severity=InfoBarSeverity.Error;}return new(1,string.Empty,ex.Message);}
        finally{if(manageBusy){_busy=false;SetControlsEnabled(true, includeModeControls: false);}}
    }
    private Task<CommandResult> RunCliAsync(params string[] args)=>RunCliAsync(args,true);

    private async Task PumpOutputAsync(StreamReader reader,StringBuilder buffer,bool isError)
    {
        while(await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            buffer.AppendLine(line);
            DispatcherQueue.TryEnqueue(()=>AppendLogLine(isError?$"  ! {line}":$"  {line}"));
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
        if(includeModeControls)foreach(var control in new Control[]{RemoteButton,SaverButton,BalancedButton,HighButton,RemoteCustomButton,RemoteNoWifiButton,CpuSlider,CpuBox})control.IsEnabled=enabled;
        foreach(var control in new Control[]{VerifyButton,RepairButton,WifiOnButton,RefreshButton,FeaturesButton,InsightsButton,LanguageButton,AutoQuickToggle,LiveQuickToggle})control.IsEnabled=enabled;
        BusyProgress.Visibility=enabled?Visibility.Collapsed:Visibility.Visible;
    }

    private async Task RefreshStatusAsync()
    {
        RefreshIcon.Visibility=Visibility.Collapsed;RefreshProgressRing.Visibility=Visibility.Visible;RefreshProgressRing.IsActive=true;
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var generation=Volatile.Read(ref _modeSwitchGeneration);var result=await RunCliAsync(["status"],true,timeout.Token);
            if(result.ExitCode==-2){StatusText.Text=T("RefreshTimeout");StatusBar.Severity=InfoBarSeverity.Warning;return;}
            if(result.ExitCode!=0){StatusBar.Severity=InfoBarSeverity.Error;return;}
            if(generation==Volatile.Read(ref _modeSwitchGeneration)&&!_modeSwitchInProgress)ApplyStatus(result.Output);
            StatusBar.Severity=InfoBarSeverity.Success;
        }
        finally
        {
            RefreshProgressRing.IsActive=false;RefreshProgressRing.Visibility=Visibility.Collapsed;RefreshIcon.Visibility=Visibility.Visible;
        }
    }
    private void ApplyStatus(string output)
    {
        var rawMode=Extract(output,"当前模式","Current mode");var rawGpu=Extract(output,"独显功耗","dGPU power");var rawPower=Extract(output,"供电","Power");
        var rawCpu=Extract(output,"CPU 上限","CPU max");var rawBrightness=Extract(output,"亮度","Brightness");var rawSleep=Extract(output,"睡眠","Sleep");var rawDisplay=Extract(output,"关屏","Display off");
        var onBattery=rawPower.Contains("电池",StringComparison.OrdinalIgnoreCase)||rawPower.Contains("battery",StringComparison.OrdinalIgnoreCase);
        ModeValue.Text=CompactMode(rawMode);GpuValue.Text=CompactGpu(rawGpu);PowerValue.Text=rawPower;CpuValue.Text=SelectPowerBranch(rawCpu,onBattery);BrightnessValue.Text=SelectPowerBranch(rawBrightness,onBattery);SleepValue.Text=$"{FormatDuration(SelectPowerBranch(rawDisplay,onBattery))} / {FormatDuration(SelectPowerBranch(rawSleep,onBattery))}";
        var displayLabel=_language=="zh"?"关屏":"Display off";var sleepLabel=_language=="zh"?"睡眠":"Sleep";
        ToolTipService.SetToolTip(ModeValue,rawMode);ToolTipService.SetToolTip(GpuValue,rawGpu);ToolTipService.SetToolTip(CpuValue,rawCpu);ToolTipService.SetToolTip(BrightnessValue,rawBrightness);ToolTipService.SetToolTip(SleepValue,$"{displayLabel}：{rawDisplay}\n{sleepLabel}：{rawSleep}");
        LastUpdatedText.Text = string.Format(T("LastUpdated"), DateTime.Now.ToString("HH:mm:ss"));
        UpdateActiveMode(rawMode);
    }
    private string CompactMode(string value)
    {
        var text=value.ToLowerInvariant();if(text.Contains("remote")||text.Contains("远程"))return T("ModeRemote");if(text.Contains("saver")||text.Contains("低功耗"))return T("ModeSaver");if(text.Contains("balanced")||text.Contains("平衡"))return T("ModeBalanced");if(text.Contains("high")||text.Contains("高性能"))return T("ModeHigh");var parenthesis=value.IndexOf('(');return parenthesis>0?value[..parenthesis].Trim():value;
    }
    private static string CompactGpu(string value){var parenthesis=value.IndexOf('(');return parenthesis>0?value[..parenthesis].Trim():value;}
    private static string SelectPowerBranch(string value,bool onBattery)
    {
        var prefix=onBattery?"DC ":"AC ";foreach(var part in value.Split('/')){var item=part.Trim();if(item.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))return item[prefix.Length..].Trim();}return value;
    }
    private string FormatDuration(string value)
    {
        var text=value.Trim();
        if(!text.EndsWith('s')||!int.TryParse(text[..^1],out var seconds)||seconds<0)return text;
        if(seconds==0)return _language=="zh"?"永不":"Never";
        if(seconds%3600==0){var hours=seconds/3600;return _language=="zh"?$"{hours} 小时":$"{hours} {(hours==1?"hour":"hours")}";}
        if(seconds%60==0){var minutes=seconds/60;return _language=="zh"?$"{minutes} 分钟":$"{minutes} {(minutes==1?"minute":"minutes")}";}
        return _language=="zh"?$"{seconds} 秒":$"{seconds} {(seconds==1?"second":"seconds")}";
    }
    private string Extract(string output,params string[] labels)
    {
        foreach(var line in output.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries)){var text=line.Trim();foreach(var label in labels){if(!text.StartsWith(label,StringComparison.OrdinalIgnoreCase))continue;var i=text.IndexOf(':');if(i>=0)return text[(i+1)..].Trim();}}
        return T("Unknown");
    }

    private Task<bool> RunModeAsync(params string[] args)=>RunModeCoreAsync(args,SwitchRequestContext.Manual);
    private static async Task<bool> ActivatePlanImmediatelyAsync(string mode)
    {
        var guid=mode.ToLowerInvariant() switch{"remote" or "saver"=>SaverGuid,"balanced"=>BalancedGuid,"high"=>HighGuid,_=>string.Empty};if(guid.Length==0)return false;
        try{using var p=Process.Start(new ProcessStartInfo("powercfg.exe",$"/setactive {guid}"){UseShellExecute=false,CreateNoWindow=true});if(p is null)return false;await p.WaitForExitAsync();return p.ExitCode==0;}catch{return false;}
    }
    private void ApplyOptimisticMode(string[] args)
    {
        var mode=args[0].ToLowerInvariant();var custom=args.Length>1&&int.TryParse(args[1],out var value)?value:0;
        ModeValue.Text=mode switch{"remote"=>T("ModeRemote"),"saver"=>T("ModeSaver"),"balanced"=>T("ModeBalanced"),"high"=>T("ModeHigh"),_=>mode};
        CpuValue.Text=mode switch{"remote"=>$"{(custom>0?custom:32)}%","saver"=>$"{(custom>0?custom:30)}%",_=>"100%"};
        BrightnessValue.Text=mode is "remote" or "saver"?"50%":"100%";
        SleepValue.Text=mode is "remote" or "saver"
            ? $"{FormatDuration("60s")} / {FormatDuration("0s")}"
            : (_language=="zh"?"正在同步…":"Syncing…");
        UpdateActiveMode(mode);
    }
    private void UpdateActiveMode(string text)
    {
        var value=text.ToLowerInvariant();Button? active=value.Contains("remote")||value.Contains("远程")?RemoteButton:value.Contains("saver")||value.Contains("低功耗")?SaverButton:value.Contains("balanced")||value.Contains("平衡")?BalancedButton:value.Contains("high")||value.Contains("高性能")?HighButton:null;
        foreach(var button in new[]{RemoteButton,SaverButton,BalancedButton,HighButton})
        {
            var isActive=button==active;
            AutomationProperties.SetItemStatus(button,isActive?(_language=="zh"?"当前模式":"Current mode"):string.Empty);
            if(isActive){button.Background=new SolidColorBrush(Colors.DodgerBlue);button.Foreground=new SolidColorBrush(Colors.White);}
            else{button.ClearValue(Control.BackgroundProperty);button.ClearValue(Control.ForegroundProperty);}
        }
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
    private async void LanguageButton_Click(object sender,RoutedEventArgs e){var next=_language=="zh"?"en":"zh";var result=await RunCliAsync("lang",next);if(result.ExitCode!=0)return;_language=next;ApplyLanguage();await RefreshStatusAsync();}
    private void ClearLogButton_Click(object sender,RoutedEventArgs e){LogBox.Text=string.Empty;_logLineCount=0;UpdateLogStats();}
    private void CopyLogButton_Click(object sender,RoutedEventArgs e){if(string.IsNullOrWhiteSpace(LogBox.Text))return;var package=new Windows.ApplicationModel.DataTransfer.DataPackage();package.SetText(LogBox.Text);Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);StatusText.Text=T("Copied");StatusBar.Severity=InfoBarSeverity.Success;}
    private void CpuSlider_ValueChanged(object sender,RangeBaseValueChangedEventArgs e){if(_syncingCpu||CpuBox is null)return;_syncingCpu=true;CpuBox.Value=Math.Round(e.NewValue);_syncingCpu=false;}
    private void CpuBox_ValueChanged(NumberBox sender,NumberBoxValueChangedEventArgs args){if(_syncingCpu||CpuSlider is null||double.IsNaN(args.NewValue))return;_syncingCpu=true;CpuSlider.Value=Math.Clamp(args.NewValue,20,50);_syncingCpu=false;}
    private readonly record struct CommandResult(int ExitCode, string Output, string Error);
}
