namespace PowerModeWinUI;

internal static class SettingsWindowStrings
{
    public static string On(bool chinese) => chinese ? "开" : "On";
    public static string Off(bool chinese) => chinese ? "关" : "Off";
    public static string Save(bool chinese) => chinese ? "保存" : "Save";
    public static string Close(bool chinese) => chinese ? "关闭" : "Close";
    public static string Discard(bool chinese) => chinese ? "放弃" : "Discard";
    public static string ContinueEditing(bool chinese) =>
        chinese ? "继续编辑" : "Continue editing";
    public static string UnsavedTitle(bool chinese) =>
        chinese ? "保存设置更改？" : "Save settings changes?";
    public static string UnsavedMessage(bool chinese) =>
        chinese
            ? "设置中有尚未保存的更改。"
            : "There are unsaved settings changes.";
    public static string InvalidInput(bool chinese) =>
        chinese ? "请先修正无效输入。" : "Fix invalid input before saving.";
    public static string SaveFailed(bool chinese) =>
        chinese ? "设置保存失败。" : "Settings could not be saved.";
    public static string DeleteProfileTitle(bool chinese) =>
        chinese ? "删除自定义方案？" : "Delete custom profile?";
    public static string DeleteRuleTitle(bool chinese) =>
        chinese ? "删除自动化规则？" : "Delete automation rule?";
    public static string DeleteNamedItem(bool chinese, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return chinese ? $"将删除“{name}”。" : $"This will delete “{name}”.";
    }
    public static string Delete(bool chinese) => chinese ? "删除" : "Delete";
    public static string Cancel(bool chinese) => chinese ? "取消" : "Cancel";
}
