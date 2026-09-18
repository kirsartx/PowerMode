namespace PowerModeWinUI;

/// <summary>
/// Localized UI strings for <see cref="MainWindow"/>, keyed by language code ("zh", "en")
/// then by string key. Extracted from the window so the view code stays focused on layout.
/// </summary>
internal static class MainWindowStrings
{
    public const string DefaultLanguage = "zh";

    public static IReadOnlyDictionary<string, string> ForLanguage(string language) =>
        Catalog.TryGetValue(language, out var table) ? table : Catalog[DefaultLanguage];

    public static bool IsSupportedLanguage(string language) => Catalog.ContainsKey(language);

    private static readonly Dictionary<string, Dictionary<string, string>> Catalog = new()
    {
        ["zh"] = new()
        {
            ["Subtitle"]="Hermes 远程电源切换器", ["Refresh"]="刷新", ["Language"]="English", ["Features"]="功能中心", ["Insights"]="洞察", ["RecoveryCenter"]="恢复", ["More"]="更多", ["ExperienceModeAutomation"]="切换简单或专业模式",
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
            ["Subtitle"]="Hermes remote power switcher", ["Refresh"]="Refresh", ["Language"]="中文", ["Features"]="Features", ["Insights"]="Insights", ["RecoveryCenter"]="Recovery", ["More"]="More", ["ExperienceModeAutomation"]="Switch between Simple and Professional modes",
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
}
