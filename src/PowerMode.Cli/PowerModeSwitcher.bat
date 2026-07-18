@echo off
chcp 65001 >nul
title PowerMode
setlocal
set "PM_SELF=%~f0"
set "PM_TMP=%TEMP%\PowerModeSwitcher-%RANDOM%-%RANDOM%.ps1"

powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$self=$env:PM_SELF; $tmp=$env:PM_TMP; $lines=[System.IO.File]::ReadAllLines($self,[System.Text.UTF8Encoding]::new($false)); $marker='### POWERSHELL_PAYLOAD_BELOW ###'; $idx=[Array]::IndexOf($lines,$marker); if($idx -lt 0){throw 'PowerMode payload marker not found.'}; $payload=($lines[($idx+1)..($lines.Length-1)] -join [Environment]::NewLine); [System.IO.File]::WriteAllText($tmp,$payload,[System.Text.UTF8Encoding]::new($true))"
if errorlevel 1 (
    pause
    exit /b 1
)

if "%~1"=="" (
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%PM_TMP%"
    set "PM_EXIT=%ERRORLEVEL%"
) else (
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%PM_TMP%" %*
    set "PM_EXIT=%ERRORLEVEL%"
)

del /q "%PM_TMP%" >nul 2>nul
if not "%PM_NO_PAUSE%"=="1" if not "%~1"=="" pause
exit /b %PM_EXIT%

### POWERSHELL_PAYLOAD_BELOW ###
# PowerMode.ps1 - Hermes remote low-power profile switcher
# Keeps Hermes + uu remote reachable by preventing sleep/hibernate.

param(
    [Parameter(Position = 0)]
    [string]$Mode,

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Options = @()
)

# Keep redirected output deterministic for the WPF client. Windows PowerShell
# otherwise uses the active legacy console code page when stdout is redirected.
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)

$GUID_SAVER = 'a1841308-3541-4fab-bc81-f71556f20b4a'
$GUID_BAL   = '381b4222-f694-41f0-9685-ff5bb260df2e'
$GUID_HIGH  = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'

$IntelGraphicsSubgroup = '44f3beca-a7c0-460e-9df2-bb8b99e0cba6'
$IntelGraphicsSetting  = '3619c3f2-afb2-4afc-b0e9-e7fef372de36'
$WiFiSubgroup          = '19cbb8fa-5279-450e-9fac-8a3d5fedd0c1'
$WiFiPowerSavingMode   = '12bbebe6-58d6-4636-95bb-3217ef867c1a'
$UsbSubgroup           = '2a737441-1930-4402-8d77-b2bebba308a3'
$UsbSelectiveSuspend   = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'
$BrightnessRemote      = 50
$BrightnessSaver       = 50
$BrightnessBalanced    = 100
$BrightnessHigh        = 100
$ConfigRegPath         = 'HKCU:\Software\PowerModeSwitcher'

$script:AdminWarningShown = $false
$script:Language = 'en'

function Get-Text {
    param([string]$Key)

    $zh = @{
        AdminWarning = '提示：如果部分电源设置或 WiFi 修改失败，请用管理员身份运行。'
        NoWifiFound = '  没有找到启用中的 WiFi 适配器，可能已经关闭。'
        WifiDisabled = '  已禁用 WiFi 适配器以进一步省电：{0}'
        WifiDisableFailed = '  无法禁用 WiFi：{0}'
        SwitchedRemote = '已切换到 HERMES 远程模式。'
        RemoteDetails = '  CPU 上限：{0}% | 亮度：{1}% | 1 分钟关屏 | 永不睡眠'
        RemoteSavings = '  Intel 核显最大省电 + WiFi 最大节能（有线网最佳）。'
        RemoteExpected = '  预计：整机约 23-33W（远程 Hermes 操作时 +3-8W）。'
        SwitchedSaver = '已切换到低功耗模式。'
        SaverDetails = '  CPU 上限：{0}% | 亮度：{1}% | 1 分钟关屏 | 永不睡眠'
        SaverExpected = '  预计：整机约 22-32W（远程操作时 +3-8W）。'
        SwitchedBalanced = '已切换到平衡模式。亮度：{0}%。'
        SwitchedHigh = '已切换到高性能模式。亮度：{0}%。'
        CurrentMode = '当前模式'
        DgpuPower = '独显功耗'
        Power = '供电'
        PowerBattery = '电池'
        PowerAc = '插电'
        PowerCharging = '插电 / 充电中'
        PowerFull = '插电 / 已充满'
        PowerUnknown = '未知'
        CpuMax = 'CPU 上限'
        Brightness = '亮度'
        DisplayOff = '关屏'
        Sleep = '睡眠'
        Hibernate = '休眠'
        Tip = '提示：远程使用 Hermes + uu 远程建议用 remote；插网线时可加 nowifi。'
        HeaderSubtitle = '  Hermes 远程电源切换器'
        CurrentProfile = ' 当前模式       : '
        CpuBrightness = ' CPU / 亮度     : '
        PowerSource = ' 供电状态       : '
        RemoteSafety = ' 远程保护       : 永不睡眠 / 永不休眠'
        ChooseMode = ' 选择模式'
        MenuRemote = '远程推荐'
        MenuRemoteHint = 'CPU 32%，亮度 50%，1 分钟关屏'
        MenuCustom = '远程自定义 CPU'
        MenuCustomHint = '选择 20-50%，在流畅和低功耗之间调整'
        MenuNoWifi = '远程 + 关闭 WiFi'
        MenuNoWifiHint = '仅适合插网线；禁用前会确认'
        MenuSaver = '低功耗'
        MenuSaverHint = 'CPU 30%，亮度 50%，更省电'
        MenuBalanced = '平衡'
        MenuBalancedHint = '日常使用，亮度 100%'
        MenuHigh = '高性能'
        MenuHighHint = '游戏/重负载，亮度 100%'
        MenuRefresh = '刷新状态'
        MenuRefreshHint = '更新当前模式和独显功耗'
        MenuVerify = '校验当前模式'
        MenuVerifyHint = '检查关键电源设置是否生效'
        MenuHelp = '命令帮助'
        MenuHelpHint = '显示命令行示例'
        MenuLang = '语言 / Language'
        MenuLangHint = '切换中文 / English'
        MenuExit = '退出'
        MenuExitHint = '关闭工具'
        Select = '选择'
        CpuPrompt = 'CPU 上限百分比，20-50（回车 = {0}）'
        InvalidNumber = '输入不是数字，使用默认值。'
        WifiConfirm1 = '这会尝试禁用启用中的 WiFi 适配器。'
        WifiConfirm2 = '仅在已插网线、远程连接不依赖 WiFi 时使用。'
        WifiConfirmPrompt = '输入 YES 继续'
        WifiCanceled = '已取消，WiFi 未修改。'
        PauseMenu = '按回车返回菜单'
        UnknownChoice = '无法识别。请输入 1-9、L、0，或模式名称。'
        UsageTitle = 'PowerMode（Hermes 远程低功耗优化）：'
        UsageStatus = '  PowerModeSwitcher status'
        UsageVerify = '  PowerModeSwitcher verify                   - 校验当前模式关键设置'
        UsageRemote = '  PowerModeSwitcher remote [20-50]           - Hermes 远程推荐'
        UsageRemoteNoWifi = '  PowerModeSwitcher remote nowifi            - 远程 + 插网线时禁用 WiFi'
        UsageRemoteCpu = '  PowerModeSwitcher remote 32 nowifi         - 远程 CPU32% + 禁用 WiFi'
        UsageSaver = '  PowerModeSwitcher saver [20-50]'
        UsageBalanced = '  PowerModeSwitcher balanced'
        UsageHigh = '  PowerModeSwitcher high'
        UsageAlias = '  PowerModeSwitcher low | normal | perf       - 快捷别名'
        UsageLang = '  PowerModeSwitcher lang zh|en                - 切换中文 / English'
        HelpModesTitle = '模式说明：'
        HelpRemote = '  remote：远程推荐，CPU 默认 32%，亮度 50%，1 分钟关屏，永不睡眠。'
        HelpSaver = '  saver / low：低功耗，CPU 默认 30%，亮度 50%，适合轻负载省电。'
        HelpBalanced = '  balanced / normal：平衡，CPU 100%，亮度 100%，日常使用。'
        HelpHigh = '  high / perf：高性能，CPU 100%，亮度 100%，游戏或重负载。'
        HelpSafetyTitle = '安全与排查：'
        HelpVerify = '  verify：检查当前模式的 CPU、亮度、睡眠、休眠、关屏设置是否生效。'
        HelpNoWifi = '  nowifi：会尝试禁用 WiFi，仅建议插网线并确认远程不依赖 WiFi 时使用。'
        HelpLanguage = '  菜单按 L 或运行 lang zh|en 可切换语言；偏好保存在注册表。'
        HelpSingleFile = '  这是单文件版 bat，运行时临时释放脚本到 TEMP，执行后自动删除。'
        HelpRepair = '  如果设置被 Windows 改乱，重新运行对应模式即可修复，例如 saver 或 balanced。'
        VerifyTitle = '正在校验 {0} ({1})'
        VerifyPassed = '模式校验通过。'
        VerifyFailed = '模式校验发现不一致。重新运行对应模式可自动修复。'
        VerifyUnknown = '未知模式：{0} ({1})'
        ActivePlanMissing = '无法检测当前电源计划。'
        CheckOk = '  [OK] {0}: {1}'
        CheckMissing = '  [!] {0}: N/A，预期 {1}'
        CheckBad = '  [!!] {0}: {1}，预期 {2}'
        LanguageSet = '语言已切换为中文。'
        LanguageNow = '当前语言：中文'
        IgnoringUnknown = '忽略未知参数：{0}'
    }

    $en = @{
        AdminWarning = 'Note: run as Administrator if some power settings or WiFi changes fail.'
        NoWifiFound = '  No active WiFi adapter found to disable (or already down).'
        WifiDisabled = '  WiFi adapter disabled for extra power saving: {0}'
        WifiDisableFailed = '  Could not disable WiFi: {0}'
        SwitchedRemote = 'Switched to HERMES REMOTE mode.'
        RemoteDetails = '  CPU max: {0}% | Brightness: {1}% | Display off after 1 min | Never sleep'
        RemoteSavings = '  Intel max battery + WiFi max saving (best with wired Ethernet).'
        RemoteExpected = '  Expected: ~23-33W total (remote Hermes adds ~3-8W).'
        SwitchedSaver = 'Switched to SAVER mode.'
        SaverDetails = '  CPU max: {0}% | Brightness: {1}% | Display off after 1 min | Never sleep'
        SaverExpected = '  Expected total: ~22-32W (remote adds ~3-8W).'
        SwitchedBalanced = 'Switched to BALANCED. Brightness: {0}%.'
        SwitchedHigh = 'Switched to HIGH PERFORMANCE. Brightness: {0}%.'
        CurrentMode = 'Current mode'
        DgpuPower = 'dGPU power'
        Power = 'Power'
        PowerBattery = 'Battery'
        PowerAc = 'AC'
        PowerCharging = 'AC / charging'
        PowerFull = 'AC / full'
        PowerUnknown = 'Unknown'
        CpuMax = 'CPU max'
        Brightness = 'Brightness'
        DisplayOff = 'Display off'
        Sleep = 'Sleep'
        Hibernate = 'Hibernate'
        Tip = "Tip: use 'remote' for Hermes + uu remote; add 'nowifi' when Ethernet is connected."
        HeaderSubtitle = '  Hermes remote power switcher'
        CurrentProfile = ' Current profile : '
        CpuBrightness = ' CPU / brightness: '
        PowerSource = ' Power source    : '
        RemoteSafety = ' Remote safety   : Never sleep / never hibernate'
        ChooseMode = ' Choose a mode'
        MenuRemote = 'Remote recommended'
        MenuRemoteHint = 'CPU 32%, brightness 50%, display off in 1 min'
        MenuCustom = 'Remote custom CPU'
        MenuCustomHint = 'Pick 20-50% for smoother or cooler remote use'
        MenuNoWifi = 'Remote + WiFi off'
        MenuNoWifiHint = 'Ethernet only; asks before disabling WiFi'
        MenuSaver = 'Saver'
        MenuSaverHint = 'CPU 30%, brightness 50%, lower power'
        MenuBalanced = 'Balanced'
        MenuBalancedHint = 'Normal desktop use, brightness 100%'
        MenuHigh = 'High performance'
        MenuHighHint = 'Games/heavy workloads, brightness 100%'
        MenuRefresh = 'Refresh status'
        MenuRefreshHint = 'Update current profile and dGPU power'
        MenuVerify = 'Verify current'
        MenuVerifyHint = 'Check active profile values'
        MenuHelp = 'Command help'
        MenuHelpHint = 'Show CLI examples'
        MenuLang = 'Language / 语言'
        MenuLangHint = 'Switch English / 中文'
        MenuExit = 'Exit'
        MenuExitHint = 'Close this tool'
        Select = 'Select'
        CpuPrompt = 'CPU max percent, 20-50 (Enter = {0})'
        InvalidNumber = 'Invalid number. Using default.'
        WifiConfirm1 = 'This will try to disable active WiFi adapters.'
        WifiConfirm2 = 'Use it only when Ethernet is connected and remote access does not rely on WiFi.'
        WifiConfirmPrompt = 'Type YES to continue'
        WifiCanceled = 'Canceled. WiFi was not changed.'
        PauseMenu = 'Press Enter to return to menu'
        UnknownChoice = 'Unknown choice. Try 1-9, L, 0, or a mode name.'
        UsageTitle = 'PowerMode (Hermes remote optimized):'
        UsageStatus = '  PowerModeSwitcher status'
        UsageVerify = '  PowerModeSwitcher verify                   - Check active profile settings'
        UsageRemote = '  PowerModeSwitcher remote [20-50]           - Recommended for remote Hermes'
        UsageRemoteNoWifi = '  PowerModeSwitcher remote nowifi            - Remote + disable WiFi adapter when on Ethernet'
        UsageRemoteCpu = '  PowerModeSwitcher remote 32 nowifi         - Remote CPU32% + disable WiFi'
        UsageSaver = '  PowerModeSwitcher saver [20-50]'
        UsageBalanced = '  PowerModeSwitcher balanced'
        UsageHigh = '  PowerModeSwitcher high'
        UsageAlias = '  PowerModeSwitcher low | normal | perf       - Short aliases'
        UsageLang = '  PowerModeSwitcher lang zh|en                - Switch Chinese / English'
        HelpModesTitle = 'Modes:'
        HelpRemote = '  remote: recommended for remote use, CPU 32% by default, brightness 50%, display off after 1 min, never sleep.'
        HelpSaver = '  saver / low: low power, CPU 30% by default, brightness 50%, best for light workloads.'
        HelpBalanced = '  balanced / normal: CPU 100%, brightness 100%, normal desktop use.'
        HelpHigh = '  high / perf: CPU 100%, brightness 100%, games or heavy local workloads.'
        HelpSafetyTitle = 'Safety and troubleshooting:'
        HelpVerify = '  verify: checks CPU, brightness, sleep, hibernate, and display-off settings for the active profile.'
        HelpNoWifi = '  nowifi: tries to disable WiFi; use only when Ethernet is connected and remote access does not depend on WiFi.'
        HelpLanguage = '  Press L in the menu or run lang zh|en to switch language; preference is stored in the registry.'
        HelpSingleFile = '  This is a single-file bat; it extracts a temporary script to TEMP and deletes it after running.'
        HelpRepair = '  If Windows changes settings later, re-run the mode command to repair them, such as saver or balanced.'
        VerifyTitle = 'Verifying {0} ({1})'
        VerifyPassed = 'Profile check passed.'
        VerifyFailed = 'Profile check found differences. Re-run the mode command to repair this profile.'
        VerifyUnknown = 'Unknown profile: {0} ({1})'
        ActivePlanMissing = 'Could not detect the active power plan.'
        CheckOk = '  [OK] {0}: {1}'
        CheckMissing = '  [!] {0}: N/A, expected {1}'
        CheckBad = '  [!!] {0}: {1}, expected {2}'
        LanguageSet = 'Language switched to English.'
        LanguageNow = 'Current language: English'
        IgnoringUnknown = 'Ignoring unknown option(s): {0}'
    }

    $table = if ($script:Language -eq 'zh') { $zh } else { $en }
    if ($table.ContainsKey($Key)) { return $table[$Key] }
    return $Key
}

function Initialize-Config {
    try {
        $config = Get-ItemProperty -Path $ConfigRegPath -ErrorAction Stop
        if ($config.Language -in @('en', 'zh')) {
            $script:Language = $config.Language
        }
    } catch {
        $script:Language = 'en'
    }
}

function Set-LanguagePreference {
    param([string]$Language)

    $normalized = if ($Language -in @('zh', 'cn', 'chinese', '中文')) {
        'zh'
    } elseif ($Language -in @('en', 'english')) {
        'en'
    } elseif ($script:Language -eq 'zh') {
        'en'
    } else {
        'zh'
    }

    $script:Language = $normalized
    if (-not (Test-Path -Path $ConfigRegPath)) {
        [void](New-Item -Path $ConfigRegPath -Force)
    }
    [void](New-ItemProperty -Path $ConfigRegPath -Name Language -Value $normalized -PropertyType String -Force)

    Write-Host (Get-Text 'LanguageSet') -ForegroundColor Cyan
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-AdminWarning {
    if (-not $script:AdminWarningShown -and -not (Test-IsAdministrator)) {
        Write-Host (Get-Text 'AdminWarning') -ForegroundColor DarkYellow
        $script:AdminWarningShown = $true
    }
}

function Format-CommandArgument {
    param([string]$Argument)

    if ($Argument -match '[\s"]') {
        return '"' + ($Argument -replace '"', '\"') + '"'
    }

    return $Argument
}

function Invoke-PowerCfg {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'powercfg.exe'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments = (($Arguments | ForEach-Object { Format-CommandArgument $_ }) -join ' ')

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEnd().Trim()
    $stderr = $process.StandardError.ReadToEnd().Trim()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0) {
        $message = (@($stderr, $stdout) | Where-Object { $_ }) -join ' '
        if ($message) {
            Write-Host "  powercfg $($Arguments -join ' ') failed: $message" -ForegroundColor DarkGray
        } else {
            Write-Host "  powercfg $($Arguments -join ' ') failed." -ForegroundColor DarkGray
        }
        return $false
    }

    return $true
}

function Set-ActivePlan {
    param([string]$Guid)
    [void](Invoke-PowerCfg /setactive $Guid)
}

function Set-PowerValue {
    param(
        [string]$Guid,
        [string]$Subgroup,
        [string]$Setting,
        [object]$Value
    )
    [void](Invoke-PowerCfg /setacvalueindex $Guid $Subgroup $Setting ([string]$Value))
}

function Set-PowerValueDc {
    param(
        [string]$Guid,
        [string]$Subgroup,
        [string]$Setting,
        [object]$Value
    )
    [void](Invoke-PowerCfg /setdcvalueindex $Guid $Subgroup $Setting ([string]$Value))
}

function Set-PowerValueAcDc {
    param(
        [string]$Guid,
        [string]$Subgroup,
        [string]$Setting,
        [object]$Value
    )

    Set-PowerValue $Guid $Subgroup $Setting $Value
    Set-PowerValueDc $Guid $Subgroup $Setting $Value
}

function Get-ActiveSchemeGuid {
    $raw = powercfg /getactivescheme 2>$null
    $rawText = ($raw | Out-String).Trim()

    if ($rawText -match '(?i)GUID:\s+([0-9a-f-]+)') {
        return $Matches[1].ToLowerInvariant()
    }

    return $null
}

function Get-PowerSettingAcValue {
    param(
        [string]$Guid,
        [string]$Subgroup,
        [string]$Setting
    )

    $raw = powercfg /query $Guid $Subgroup $Setting 2>$null
    $rawText = ($raw | Out-String)

    if ($rawText -match '当前交流电源设置索引:\s+0x([0-9a-fA-F]+)') {
        return [Convert]::ToInt32($Matches[1], 16)
    }

    if ($rawText -match 'Current AC Power Setting Index:\s+0x([0-9a-fA-F]+)') {
        return [Convert]::ToInt32($Matches[1], 16)
    }

    $hexValues = [regex]::Matches($rawText, '0x([0-9a-fA-F]+)')
    if ($hexValues.Count -ge 2) {
        return [Convert]::ToInt32($hexValues[$hexValues.Count - 2].Groups[1].Value, 16)
    }

    return $null
}

function Get-PowerSettingDcValue {
    param(
        [string]$Guid,
        [string]$Subgroup,
        [string]$Setting
    )

    $raw = powercfg /query $Guid $Subgroup $Setting 2>$null
    $rawText = ($raw | Out-String)

    if ($rawText -match '当前直流电源设置索引:\s+0x([0-9a-fA-F]+)') {
        return [Convert]::ToInt32($Matches[1], 16)
    }

    if ($rawText -match 'Current DC Power Setting Index:\s+0x([0-9a-fA-F]+)') {
        return [Convert]::ToInt32($Matches[1], 16)
    }

    $hexValues = [regex]::Matches($rawText, '0x([0-9a-fA-F]+)')
    if ($hexValues.Count -ge 1) {
        return [Convert]::ToInt32($hexValues[$hexValues.Count - 1].Groups[1].Value, 16)
    }

    return $null
}

function Get-DisplayValue {
    param(
        [Nullable[int]]$Value,
        [string]$Suffix = ''
    )

    if ($null -eq $Value) { return 'N/A' }
    return "$Value$Suffix"
}

function Get-PowerSource {
    try {
        $battery = Get-CimInstance -ClassName Win32_Battery -ErrorAction Stop | Select-Object -First 1
        if (-not $battery) { return (Get-Text 'PowerAc') }

        switch ([int]$battery.BatteryStatus) {
            1       { return (Get-Text 'PowerBattery') }
            2       { return (Get-Text 'PowerCharging') }
            3       { return (Get-Text 'PowerFull') }
            6       { return (Get-Text 'PowerCharging') }
            7       { return (Get-Text 'PowerCharging') }
            8       { return (Get-Text 'PowerCharging') }
            9       { return (Get-Text 'PowerCharging') }
            11      { return (Get-Text 'PowerAc') }
            default { return (Get-Text 'PowerUnknown') }
        }
    } catch {
        return (Get-Text 'PowerUnknown')
    }
}

function Set-DisplayBrightness {
    param(
        [string]$Guid,
        [int]$Percent
    )

    if ($Percent -lt 0) { $Percent = 0 }
    if ($Percent -gt 100) { $Percent = 100 }

    Set-PowerValueAcDc $Guid SUB_VIDEO VIDEONORMALLEVEL $Percent
}

function Get-CurrentPlanName {
    $guid = Get-ActiveSchemeGuid
    if ($guid) {
        switch ($guid) {
            $GUID_SAVER { return 'Power Saver (Hermes Remote Optimized)' }
            $GUID_BAL   { return 'Balanced' }
            $GUID_HIGH  { return 'High Performance' }
            default     { return "Unknown ($guid)" }
        }
    }

    return 'Unknown'
}

function Limit-CpuMax {
    param(
        [int]$Value,
        [int]$Default,
        [int]$Min = 20,
        [int]$Max = 50
    )

    if ($Value -lt $Min) { return $Default }
    if ($Value -gt $Max) { return $Max }
    return $Value
}

function Get-ModeOptions {
    param(
        [string[]]$Values,
        [int]$DefaultCpu
    )

    $cpu = $DefaultCpu
    $noWifi = $false
    $unknown = @()

    foreach ($value in $Values) {
        if ([string]::IsNullOrWhiteSpace($value)) { continue }

        $normalized = $value.Trim().ToLowerInvariant()
        $parsed = 0

        if ($normalized -in @('nowifi', 'no-wifi', 'wifi-off')) {
            $noWifi = $true
        } elseif ([int]::TryParse($normalized, [ref]$parsed)) {
            $cpu = $parsed
        } else {
            $unknown += $value
        }
    }

    [pscustomobject]@{
        Cpu     = $cpu
        NoWiFi  = $noWifi
        Unknown = $unknown
    }
}

function Apply-CommonProtection {
    param([string]$Guid)

    Set-PowerValueAcDc $Guid SUB_SLEEP STANDBYIDLE 0
    Set-PowerValueAcDc $Guid SUB_SLEEP HIBERNATEIDLE 0
    Set-PowerValueAcDc $Guid SUB_DISK DISKIDLE 0
}

function Apply-CpuProfile {
    param(
        [string]$Guid,
        [int]$MaxCpu,
        [int]$MinCpu,
        [switch]$DisableBoost
    )

    Set-PowerValueAcDc $Guid SUB_PROCESSOR PROCTHROTTLEMAX $MaxCpu
    Set-PowerValueAcDc $Guid SUB_PROCESSOR PROCTHROTTLEMIN $MinCpu
    if ($DisableBoost) {
        Set-PowerValueAcDc $Guid SUB_PROCESSOR PERFBOOSTMODE 0
    }
}

function Apply-IntelGraphicsSaver {
    param([string]$Guid)
    Set-PowerValueAcDc $Guid $IntelGraphicsSubgroup $IntelGraphicsSetting '000'
}

function Disable-WiFiAdapter {
    Write-AdminWarning

    try {
        $wifiAdapters = Get-NetAdapter -ErrorAction Stop | Where-Object {
            ($_.Name -like '*Wi*' -or
             $_.InterfaceDescription -like '*Wireless*' -or
             $_.InterfaceDescription -like '*Wi-Fi*' -or
             $_.InterfaceDescription -like '*WLAN*') -and
            $_.Status -eq 'Up'
        }

        if (-not $wifiAdapters) {
            Write-Host (Get-Text 'NoWifiFound') -ForegroundColor DarkGray
            return
        }

        foreach ($adapter in $wifiAdapters) {
            Disable-NetAdapter -Name $adapter.Name -Confirm:$false -ErrorAction Stop
            Write-Host ((Get-Text 'WifiDisabled') -f $adapter.Name) -ForegroundColor Yellow
        }
    } catch {
        Write-Host ((Get-Text 'WifiDisableFailed') -f $_.Exception.Message) -ForegroundColor DarkGray
    }
}

function Set-RemoteTweaks {
    param(
        [int]$MaxCpu = 32,
        [switch]$NoWiFi
    )

    $maxCpu = Limit-CpuMax -Value $MaxCpu -Default 32
    $g = $GUID_SAVER

    Set-ActivePlan $g
    Apply-CpuProfile -Guid $g -MaxCpu $maxCpu -MinCpu 5 -DisableBoost
    Apply-IntelGraphicsSaver $g
    Set-PowerValueAcDc $g SUB_PCIEXPRESS ASPM 2
    Set-PowerValueAcDc $g $WiFiSubgroup $WiFiPowerSavingMode 3
    Set-PowerValueAcDc $g $UsbSubgroup $UsbSelectiveSuspend 1
    Set-PowerValueAcDc $g SUB_VIDEO VIDEOIDLE 60
    Set-DisplayBrightness $g $BrightnessRemote
    Apply-CommonProtection $g
    Set-ActivePlan $g

    Write-Host (Get-Text 'SwitchedRemote') -ForegroundColor Green
    Write-Host ((Get-Text 'RemoteDetails') -f $maxCpu, $BrightnessRemote) -ForegroundColor DarkGray
    Write-Host (Get-Text 'RemoteSavings') -ForegroundColor DarkGray

    if ($NoWiFi) {
        Disable-WiFiAdapter
    }

    Write-Host (Get-Text 'RemoteExpected') -ForegroundColor Cyan
}

function Set-SaverTweaks {
    param([int]$MaxCpu = 30)

    $maxCpu = Limit-CpuMax -Value $MaxCpu -Default 30
    $g = $GUID_SAVER

    Set-ActivePlan $g
    Apply-CpuProfile -Guid $g -MaxCpu $maxCpu -MinCpu 3 -DisableBoost
    Apply-IntelGraphicsSaver $g
    Set-PowerValueAcDc $g SUB_PCIEXPRESS ASPM 2
    Set-PowerValueAcDc $g $WiFiSubgroup $WiFiPowerSavingMode 2
    Set-PowerValueAcDc $g $UsbSubgroup $UsbSelectiveSuspend 1
    Set-PowerValueAcDc $g SUB_VIDEO VIDEOIDLE 60
    Set-DisplayBrightness $g $BrightnessSaver
    Apply-CommonProtection $g
    Set-ActivePlan $g

    Write-Host (Get-Text 'SwitchedSaver') -ForegroundColor Green
    Write-Host ((Get-Text 'SaverDetails') -f $maxCpu, $BrightnessSaver) -ForegroundColor DarkGray
    Write-Host (Get-Text 'SaverExpected') -ForegroundColor Cyan
}

function Set-BalancedTweaks {
    $g = $GUID_BAL

    Set-ActivePlan $g
    Apply-CpuProfile -Guid $g -MaxCpu 100 -MinCpu 5
    Set-PowerValueAcDc $g SUB_PCIEXPRESS ASPM 1
    Set-PowerValueAcDc $g $WiFiSubgroup $WiFiPowerSavingMode 1
    Set-DisplayBrightness $g $BrightnessBalanced
    Apply-CommonProtection $g
    Set-ActivePlan $g

    Write-Host ((Get-Text 'SwitchedBalanced') -f $BrightnessBalanced) -ForegroundColor Yellow
}

function Set-HighTweaks {
    $g = $GUID_HIGH

    Set-ActivePlan $g
    Apply-CpuProfile -Guid $g -MaxCpu 100 -MinCpu 5
    Set-PowerValueAcDc $g SUB_PCIEXPRESS ASPM 0
    Set-PowerValueAcDc $g $WiFiSubgroup $WiFiPowerSavingMode 0
    Set-DisplayBrightness $g $BrightnessHigh
    Apply-CommonProtection $g
    Set-ActivePlan $g

    Write-Host ((Get-Text 'SwitchedHigh') -f $BrightnessHigh) -ForegroundColor Red
}

function Get-GpuPower {
    try {
        $raw = nvidia-smi --query-gpu=power.draw --format=csv,noheader,nounits 2>$null |
            Select-Object -First 1

        if ($raw) {
            return ("{0} W (NVIDIA dGPU)" -f $raw.Trim())
        }
    } catch {}

    return 'N/A (run nvidia-smi manually or use HWInfo)'
}

function Get-ProfileTag {
    param(
        [string]$PlanName,
        [string]$Guid
    )

    if ($Guid -eq $GUID_SAVER) {
        $cpuMax = Get-PowerSettingAcValue $Guid SUB_PROCESSOR PROCTHROTTLEMAX
        if ($null -eq $cpuMax) { return 'REMOTE / SAVER' }
        if ($cpuMax -le 30) { return 'SAVER' }
        return 'REMOTE'
    }

    switch -Wildcard ($PlanName) {
        'Balanced'         { return 'BALANCED' }
        'High Performance' { return 'HIGH PERFORMANCE' }
        default            { return 'UNKNOWN' }
    }
}

function Write-Rule {
    param(
        [int]$Width = 72,
        [ConsoleColor]$Color = 'DarkGray'
    )

    Write-Host ('=' * $Width) -ForegroundColor $Color
}

function Write-MenuItem {
    param(
        [string]$Key,
        [string]$Title,
        [string]$Hint,
        [ConsoleColor]$Color = 'Gray'
    )

    Write-Host ('  [{0}] ' -f $Key) -NoNewline -ForegroundColor DarkGray
    Write-Host $Title.PadRight(24) -NoNewline -ForegroundColor $Color
    Write-Host $Hint -ForegroundColor DarkGray
}

function Show-Header {
    $plan = Get-CurrentPlanName
    $guid = Get-ActiveSchemeGuid
    $tag = Get-ProfileTag -PlanName $plan -Guid $guid
    $gpuPower = Get-GpuPower
    $brightness = if ($guid) { Get-PowerSettingAcValue $guid SUB_VIDEO VIDEONORMALLEVEL } else { $null }
    $cpuMax = if ($guid) { Get-PowerSettingAcValue $guid SUB_PROCESSOR PROCTHROTTLEMAX } else { $null }

    Clear-Host
    Write-Rule -Color Cyan
    Write-Host ' PowerMode' -NoNewline -ForegroundColor Cyan
    Write-Host (Get-Text 'HeaderSubtitle') -ForegroundColor DarkGray
    Write-Rule -Color Cyan
    Write-Host ''
    Write-Host (Get-Text 'CurrentProfile') -NoNewline -ForegroundColor DarkGray
    Write-Host $tag -NoNewline -ForegroundColor Green
    Write-Host "  ($plan)" -ForegroundColor DarkGray
    Write-Host (" {0}      : " -f (Get-Text 'DgpuPower')) -NoNewline -ForegroundColor DarkGray
    Write-Host $gpuPower -ForegroundColor Cyan
    Write-Host (Get-Text 'CpuBrightness') -NoNewline -ForegroundColor DarkGray
    Write-Host "$(Get-DisplayValue $cpuMax '%') / $(Get-DisplayValue $brightness '%')" -ForegroundColor Cyan
    Write-Host (Get-Text 'PowerSource') -NoNewline -ForegroundColor DarkGray
    Write-Host (Get-PowerSource) -ForegroundColor Cyan
    Write-Host (Get-Text 'RemoteSafety') -ForegroundColor DarkGray
    Write-Host ''
}

function Show-Status {
    $guid = Get-ActiveSchemeGuid
    $plan = Get-CurrentPlanName
    $tag = Get-ProfileTag -PlanName $plan -Guid $guid
    $brightnessAc = if ($guid) { Get-PowerSettingAcValue $guid SUB_VIDEO VIDEONORMALLEVEL } else { $null }
    $brightnessDc = if ($guid) { Get-PowerSettingDcValue $guid SUB_VIDEO VIDEONORMALLEVEL } else { $null }
    $cpuAc = if ($guid) { Get-PowerSettingAcValue $guid SUB_PROCESSOR PROCTHROTTLEMAX } else { $null }
    $cpuDc = if ($guid) { Get-PowerSettingDcValue $guid SUB_PROCESSOR PROCTHROTTLEMAX } else { $null }
    $displayAc = if ($guid) { Get-PowerSettingAcValue $guid SUB_VIDEO VIDEOIDLE } else { $null }
    $displayDc = if ($guid) { Get-PowerSettingDcValue $guid SUB_VIDEO VIDEOIDLE } else { $null }
    $sleepAc = if ($guid) { Get-PowerSettingAcValue $guid SUB_SLEEP STANDBYIDLE } else { $null }
    $sleepDc = if ($guid) { Get-PowerSettingDcValue $guid SUB_SLEEP STANDBYIDLE } else { $null }
    $hibernateAc = if ($guid) { Get-PowerSettingAcValue $guid SUB_SLEEP HIBERNATEIDLE } else { $null }
    $hibernateDc = if ($guid) { Get-PowerSettingDcValue $guid SUB_SLEEP HIBERNATEIDLE } else { $null }

    Write-Host ("{0}: {1} ({2})" -f (Get-Text 'CurrentMode'), $tag, $plan) -ForegroundColor Cyan
    Write-Host ("{0} : {1}" -f (Get-Text 'DgpuPower'), (Get-GpuPower))
    Write-Host ("{0}      : {1}" -f (Get-Text 'Power'), (Get-PowerSource))
    Write-Host ("{0}    : AC {1} / DC {2}" -f (Get-Text 'CpuMax'), (Get-DisplayValue $cpuAc '%'), (Get-DisplayValue $cpuDc '%'))
    Write-Host ("{0} : AC {1} / DC {2}" -f (Get-Text 'Brightness'), (Get-DisplayValue $brightnessAc '%'), (Get-DisplayValue $brightnessDc '%'))
    Write-Host ("{0}: AC {1} / DC {2}" -f (Get-Text 'DisplayOff'), (Get-DisplayValue $displayAc 's'), (Get-DisplayValue $displayDc 's'))
    Write-Host ("{0}      : AC {1} / DC {2}" -f (Get-Text 'Sleep'), (Get-DisplayValue $sleepAc 's'), (Get-DisplayValue $sleepDc 's'))
    Write-Host ("{0}  : AC {1} / DC {2}" -f (Get-Text 'Hibernate'), (Get-DisplayValue $hibernateAc 's'), (Get-DisplayValue $hibernateDc 's'))
    Write-Host ''
    Write-Host (Get-Text 'Tip') -ForegroundColor DarkGray
}

function Test-SettingEquals {
    param(
        [string]$Label,
        [Nullable[int]]$Actual,
        [int]$Expected
    )

    if ($null -eq $Actual) {
        Write-Host ((Get-Text 'CheckMissing') -f $Label, $Expected) -ForegroundColor Yellow
        return $false
    }

    if ($Actual -eq $Expected) {
        Write-Host ((Get-Text 'CheckOk') -f $Label, $Actual) -ForegroundColor Green
        return $true
    }

    Write-Host ((Get-Text 'CheckBad') -f $Label, $Actual, $Expected) -ForegroundColor Red
    return $false
}

function Test-CurrentProfile {
    $guid = Get-ActiveSchemeGuid
    if (-not $guid) {
        Write-Host (Get-Text 'ActivePlanMissing') -ForegroundColor Red
        return
    }

    $plan = Get-CurrentPlanName
    $tag = Get-ProfileTag -PlanName $plan -Guid $guid
    $expectedCpu = $null
    $expectedBrightness = $null
    $expectedDisplayIdle = $null

    switch ($tag) {
        'REMOTE' {
            $expectedCpu = Get-PowerSettingAcValue $guid SUB_PROCESSOR PROCTHROTTLEMAX
            $expectedBrightness = $BrightnessRemote
            $expectedDisplayIdle = 60
        }
        'SAVER' {
            $expectedCpu = Get-PowerSettingAcValue $guid SUB_PROCESSOR PROCTHROTTLEMAX
            $expectedBrightness = $BrightnessSaver
            $expectedDisplayIdle = 60
        }
        'BALANCED' {
            $expectedCpu = 100
            $expectedBrightness = $BrightnessBalanced
        }
        'HIGH PERFORMANCE' {
            $expectedCpu = 100
            $expectedBrightness = $BrightnessHigh
        }
        default {
            Write-Host ((Get-Text 'VerifyUnknown') -f $tag, $plan) -ForegroundColor Yellow
            Show-Status
            return
        }
    }

    Write-Host ((Get-Text 'VerifyTitle') -f $tag, $plan) -ForegroundColor Cyan
    $ok = $true
    $ok = (Test-SettingEquals 'CPU max AC' (Get-PowerSettingAcValue $guid SUB_PROCESSOR PROCTHROTTLEMAX) $expectedCpu) -and $ok
    $ok = (Test-SettingEquals 'CPU max DC' (Get-PowerSettingDcValue $guid SUB_PROCESSOR PROCTHROTTLEMAX) $expectedCpu) -and $ok
    $ok = (Test-SettingEquals 'Brightness AC' (Get-PowerSettingAcValue $guid SUB_VIDEO VIDEONORMALLEVEL) $expectedBrightness) -and $ok
    $ok = (Test-SettingEquals 'Brightness DC' (Get-PowerSettingDcValue $guid SUB_VIDEO VIDEONORMALLEVEL) $expectedBrightness) -and $ok
    $ok = (Test-SettingEquals 'Sleep AC' (Get-PowerSettingAcValue $guid SUB_SLEEP STANDBYIDLE) 0) -and $ok
    $ok = (Test-SettingEquals 'Sleep DC' (Get-PowerSettingDcValue $guid SUB_SLEEP STANDBYIDLE) 0) -and $ok
    $ok = (Test-SettingEquals 'Hibernate AC' (Get-PowerSettingAcValue $guid SUB_SLEEP HIBERNATEIDLE) 0) -and $ok
    $ok = (Test-SettingEquals 'Hibernate DC' (Get-PowerSettingDcValue $guid SUB_SLEEP HIBERNATEIDLE) 0) -and $ok

    if ($expectedDisplayIdle -ne $null) {
        $ok = (Test-SettingEquals 'Display off AC' (Get-PowerSettingAcValue $guid SUB_VIDEO VIDEOIDLE) $expectedDisplayIdle) -and $ok
        $ok = (Test-SettingEquals 'Display off DC' (Get-PowerSettingDcValue $guid SUB_VIDEO VIDEOIDLE) $expectedDisplayIdle) -and $ok
    }

    if ($ok) {
        Write-Host (Get-Text 'VerifyPassed') -ForegroundColor Green
    } else {
        Write-Host (Get-Text 'VerifyFailed') -ForegroundColor Yellow
    }
}

function Show-Usage {
    Write-Host (Get-Text 'UsageTitle')
    Write-Host (Get-Text 'UsageStatus')
    Write-Host (Get-Text 'UsageVerify')
    Write-Host (Get-Text 'UsageRemote')
    Write-Host (Get-Text 'UsageRemoteNoWifi')
    Write-Host (Get-Text 'UsageRemoteCpu')
    Write-Host (Get-Text 'UsageSaver')
    Write-Host (Get-Text 'UsageBalanced')
    Write-Host (Get-Text 'UsageHigh')
    Write-Host (Get-Text 'UsageAlias')
    Write-Host (Get-Text 'UsageLang')
    Write-Host ''
    Write-Host (Get-Text 'HelpModesTitle') -ForegroundColor Cyan
    Write-Host (Get-Text 'HelpRemote')
    Write-Host (Get-Text 'HelpSaver')
    Write-Host (Get-Text 'HelpBalanced')
    Write-Host (Get-Text 'HelpHigh')
    Write-Host ''
    Write-Host (Get-Text 'HelpSafetyTitle') -ForegroundColor Cyan
    Write-Host (Get-Text 'HelpVerify')
    Write-Host (Get-Text 'HelpNoWifi')
    Write-Host (Get-Text 'HelpLanguage')
    Write-Host (Get-Text 'HelpSingleFile')
    Write-Host (Get-Text 'HelpRepair')
}

function Read-CpuPercent {
    param(
        [int]$Default = 32
    )

    $inputValue = Read-Host ((Get-Text 'CpuPrompt') -f $Default)
    if ([string]::IsNullOrWhiteSpace($inputValue)) {
        return $Default
    }

    $parsed = 0
    if ([int]::TryParse($inputValue.Trim(), [ref]$parsed)) {
        return (Limit-CpuMax -Value $parsed -Default $Default)
    }

    Write-Host (Get-Text 'InvalidNumber') -ForegroundColor Yellow
    return $Default
}

function Confirm-WiFiDisable {
    Write-Host ''
    Write-Host (Get-Text 'WifiConfirm1') -ForegroundColor Yellow
    Write-Host (Get-Text 'WifiConfirm2') -ForegroundColor DarkGray
    $answer = Read-Host (Get-Text 'WifiConfirmPrompt')
    return ($answer -ceq 'YES')
}

function Pause-Menu {
    Write-Host ''
    [void](Read-Host (Get-Text 'PauseMenu'))
}

function Show-Menu {
    while ($true) {
        Show-Header
        Write-Host (Get-Text 'ChooseMode') -ForegroundColor White
        Write-MenuItem 1 (Get-Text 'MenuRemote') (Get-Text 'MenuRemoteHint') Green
        Write-MenuItem 2 (Get-Text 'MenuCustom') (Get-Text 'MenuCustomHint') Green
        Write-MenuItem 3 (Get-Text 'MenuNoWifi') (Get-Text 'MenuNoWifiHint') Yellow
        Write-MenuItem 4 (Get-Text 'MenuSaver') (Get-Text 'MenuSaverHint') Green
        Write-MenuItem 5 (Get-Text 'MenuBalanced') (Get-Text 'MenuBalancedHint') Yellow
        Write-MenuItem 6 (Get-Text 'MenuHigh') (Get-Text 'MenuHighHint') Red
        Write-MenuItem 7 (Get-Text 'MenuRefresh') (Get-Text 'MenuRefreshHint') Cyan
        Write-MenuItem 8 (Get-Text 'MenuVerify') (Get-Text 'MenuVerifyHint') Cyan
        Write-MenuItem 9 (Get-Text 'MenuHelp') (Get-Text 'MenuHelpHint') Cyan
        Write-MenuItem L (Get-Text 'MenuLang') (Get-Text 'MenuLangHint') Cyan
        Write-MenuItem 0 (Get-Text 'MenuExit') (Get-Text 'MenuExitHint') Gray
        Write-Host ''

        $choice = Read-Host (Get-Text 'Select')
        if ($null -eq $choice) { return }

        switch ($choice.Trim().ToLowerInvariant()) {
            { $_ -in @('1', 'r', 'remote') } {
                Set-RemoteTweaks
                Pause-Menu
            }
            { $_ -in @('2', 'custom', 'cpu') } {
                $cpu = Read-CpuPercent -Default 32
                Set-RemoteTweaks -MaxCpu $cpu
                Pause-Menu
            }
            { $_ -in @('3', 'nowifi', 'wifi-off') } {
                if (Confirm-WiFiDisable) {
                    $cpu = Read-CpuPercent -Default 32
                    Set-RemoteTweaks -MaxCpu $cpu -NoWiFi
                } else {
                    Write-Host (Get-Text 'WifiCanceled') -ForegroundColor DarkGray
                }
                Pause-Menu
            }
            { $_ -in @('4', 's', 'saver', 'low') } {
                Set-SaverTweaks
                Pause-Menu
            }
            { $_ -in @('5', 'b', 'balanced', 'normal') } {
                Set-BalancedTweaks
                Pause-Menu
            }
            { $_ -in @('6', 'h', 'high', 'perf') } {
                Set-HighTweaks
                Pause-Menu
            }
            { $_ -in @('7', 'status', 'refresh') } {
                continue
            }
            { $_ -in @('8', 'verify', 'check') } {
                Test-CurrentProfile
                Pause-Menu
            }
            { $_ -in @('9', 'help', '?') } {
                Show-Usage
                Pause-Menu
            }
            { $_ -in @('l', 'lang', 'language') } {
                if ($script:Language -eq 'zh') {
                    Set-LanguagePreference 'en'
                } else {
                    Set-LanguagePreference 'zh'
                }
                Pause-Menu
            }
            { $_ -in @('0', 'q', 'quit', 'exit') } {
                return
            }
            default {
                Write-Host (Get-Text 'UnknownChoice') -ForegroundColor Yellow
                Pause-Menu
            }
        }
    }
}

Initialize-Config

if ([string]::IsNullOrWhiteSpace($Mode)) {
    Show-Menu
    return
}

$normalizedMode = $Mode.Trim().ToLowerInvariant()

switch ($normalizedMode) {
    'status' {
        Show-Status
    }
    { $_ -in @('verify', 'check', 'doctor') } {
        Test-CurrentProfile
    }
    { $_ -in @('lang', 'language') } {
        if ($Options.Count -gt 0) {
            Set-LanguagePreference $Options[0]
        } else {
            Set-LanguagePreference ''
        }
    }
    { $_ -in @('remote', 'hermes', 'uu') } {
        $parsed = Get-ModeOptions -Values $Options -DefaultCpu 32
        if ($parsed.Unknown.Count -gt 0) {
            Write-Host ((Get-Text 'IgnoringUnknown') -f ($parsed.Unknown -join ', ')) -ForegroundColor DarkGray
        }
        Set-RemoteTweaks -MaxCpu $parsed.Cpu -NoWiFi:$parsed.NoWiFi
    }
    { $_ -in @('saver', 'low') } {
        $parsed = Get-ModeOptions -Values $Options -DefaultCpu 30
        if ($parsed.Unknown.Count -gt 0) {
            Write-Host ((Get-Text 'IgnoringUnknown') -f ($parsed.Unknown -join ', ')) -ForegroundColor DarkGray
        }
        Set-SaverTweaks -MaxCpu $parsed.Cpu
    }
    { $_ -in @('balanced', 'normal') } {
        Set-BalancedTweaks
    }
    { $_ -in @('high', 'perf') } {
        Set-HighTweaks
    }
    default {
        Show-Usage
    }
}
