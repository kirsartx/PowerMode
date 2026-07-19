namespace PowerModeWinUI;

internal static class RecoveryCenterAutomationLabels
{
    public static string Undo(bool isChinese) =>
        isChinese ? "撤销最近模式切换" : "Undo latest mode switch";

    public static string Restore(bool isChinese) =>
        isChinese ? "恢复配置备份" : "Restore configuration backup";

    public static string Reset(bool isChinese) =>
        isChinese ? "重置为默认设置" : "Reset to defaults";

    public static string Result(bool isChinese) =>
        isChinese ? "恢复操作结果" : "Recovery operation result";
}
