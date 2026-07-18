using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace PowerModeWinUI;

public sealed partial class RecoveryCenterWindow : Window
{
    private readonly MainWindow _owner;
    private readonly bool _isChinese;
    private SwitchHistoryEntry? _latestUndo;
    private ConfigurationBackupInfo? _latestBackup;
    private bool _busy;

    public RecoveryCenterWindow(MainWindow owner, bool isChinese)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _isChinese = isChinese;
        InitializeComponent();
        DpiAwareWindowSizer.Resize(this, 820, 650, 720, 560);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        Title = _isChinese ? "PowerMode 恢复中心" : "PowerMode Recovery Center";
        if (_isChinese)
            return;

        HeaderText.Text = "Recovery center";
        SubheaderText.Text = "Undo the latest mode operation or safely restore PowerMode configuration";
        UndoTitle.Text = "Undo latest mode switch";
        UndoImpactText.Text = "Return to the standard power mode used before that operation.";
        UndoAvailabilityText.Text = "Checking mode history…";
        UndoButton.Content = "Undo";
        RestoreTitle.Text = "Restore configuration backup";
        RestoreImpactText.Text =
            "Back up current settings, then atomically restore the latest distinct configuration. The active Windows power plan is not changed.";
        RestoreAvailabilityText.Text = "Checking configuration backups…";
        RestoreButton.Content = "Restore";
        ResetTitle.Text = "Reset to defaults";
        ResetImpactText.Text =
            "After a safety backup, reset settings.json only. History, backups and telemetry remain intact.";
        ResetAvailabilityText.Text =
            "Defaults use Simple experience and the Balanced preference. The active Windows power plan is not changed.";
        ResetButton.Content = "Reset defaults";
        ResultMessageText.Text = "Checking available recovery operations.";
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e) =>
        await RefreshAvailabilityAsync();

    private async Task RefreshAvailabilityAsync()
    {
        try
        {
            _latestUndo = await _owner.FindLatestUndoableModeOperationAsync();
            _latestBackup = _owner.GetLatestDistinctSettingsBackup();
            RenderAvailability();
        }
        catch (Exception ex)
        {
            ShowResult(
                FormatFailure(_isChinese ? "读取恢复状态失败" : "Could not read recovery state", ex),
                InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenderAvailability()
    {
        UndoAvailabilityText.Text = _latestUndo is null
            ? (_isChinese
                ? "没有可撤销的成功标准模式切换，或最近操作已经撤销。"
                : "No eligible successful standard-mode switch is available, or it was already undone.")
            : (_isChinese
                ? $"{_latestUndo.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm} · {_latestUndo.TargetMode} → {_latestUndo.PreviousMode}"
                : $"{_latestUndo.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm} · {_latestUndo.TargetMode} → {_latestUndo.PreviousMode}");
        RestoreAvailabilityText.Text = _latestBackup is null
            ? (_isChinese
                ? "没有与当前设置不同的配置备份。"
                : "No configuration backup differs from current settings.")
            : (_isChinese
                ? $"{_latestBackup.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {_latestBackup.FileName}"
                : $"{_latestBackup.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {_latestBackup.FileName}");
        UndoButton.IsEnabled = !_busy && _latestUndo is not null;
        RestoreButton.IsEnabled = !_busy && _latestBackup is not null;
        ResetButton.IsEnabled = !_busy;
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _latestUndo is null ||
            !await ConfirmAsync(
                _isChinese ? "撤销最近模式切换？" : "Undo latest mode switch?",
                _isChinese
                    ? $"PowerMode 将通过现有模式管线恢复到 {_latestUndo.PreviousMode}。"
                    : $"PowerMode will use the existing mode pipeline to return to {_latestUndo.PreviousMode}.",
                _isChinese ? "撤销" : "Undo"))
            return;

        SetBusy(true, _isChinese ? "正在撤销模式切换…" : "Undoing mode switch…");
        try
        {
            var succeeded = await _owner.UndoLatestModeOperationAsync();
            ShowResult(
                succeeded
                    ? (_isChinese ? "最近模式切换已撤销。" : "The latest mode switch was undone.")
                    : (_isChinese ? "未能撤销最近模式切换。" : "The latest mode switch could not be undone."),
                succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowResult(
                FormatFailure(_isChinese ? "撤销失败" : "Undo failed", ex),
                InfoBarSeverity.Error);
        }
        finally
        {
            await RefreshAvailabilityAsync();
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _latestBackup is null ||
            !await ConfirmAsync(
                _isChinese ? "恢复配置备份？" : "Restore configuration backup?",
                _isChinese
                    ? "当前 settings.json 会先创建安全备份，再原子恢复所选版本。当前 Windows 电源方案不会改变。"
                    : "Current settings.json will be backed up before the selected version is restored atomically. The active Windows power plan will not change.",
                _isChinese ? "恢复配置" : "Restore"))
            return;

        SetBusy(true, _isChinese ? "正在安全恢复配置…" : "Restoring configuration safely…");
        try
        {
            var result = await _owner.RestoreLatestSettingsBackupAsync();
            if (result is null)
            {
                ShowResult(
                    _isChinese ? "没有可恢复的不同配置备份。" : "No distinct configuration backup is available.",
                    InfoBarSeverity.Warning);
            }
            else
            {
                ShowResult(
                    result.Succeeded
                        ? (_isChinese ? "配置已恢复并重新加载。" : "Configuration was restored and reloaded.")
                        : (_isChinese
                            ? $"配置恢复失败：{result.Error}"
                            : $"Configuration restore failed: {result.Error}"),
                    result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowResult(
                FormatFailure(_isChinese ? "配置恢复失败" : "Configuration restore failed", ex),
                InfoBarSeverity.Error);
        }
        finally
        {
            await RefreshAvailabilityAsync();
        }
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy ||
            !await ConfirmAsync(
                _isChinese ? "重置 PowerMode 设置？" : "Reset PowerMode settings?",
                _isChinese
                    ? "将先创建安全备份，再用默认值替换 settings.json。历史、备份、遥测和当前 Windows 电源方案不会改变。"
                    : "A safety backup will be created before settings.json is replaced with defaults. History, backups, telemetry and the active Windows power plan will not change.",
                _isChinese ? "重置默认值" : "Reset defaults"))
            return;

        SetBusy(true, _isChinese ? "正在创建安全备份并重置…" : "Creating safety backup and resetting…");
        try
        {
            await _owner.ResetSettingsDefaultsAsync();
            ShowResult(
                _isChinese ? "设置已重置为默认值并重新加载。" : "Settings were reset to defaults and reloaded.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowResult(
                FormatFailure(_isChinese ? "重置失败" : "Reset failed", ex),
                InfoBarSeverity.Error);
        }
        finally
        {
            await RefreshAvailabilityAsync();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primaryText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = _isChinese ? "取消" : "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SetBusy(bool busy, string? progressMessage = null)
    {
        _busy = busy;
        BusyProgressRing.IsActive = busy;
        BusyProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            UndoButton.IsEnabled = false;
            RestoreButton.IsEnabled = false;
            ResetButton.IsEnabled = false;
            ResultInfoBar.Severity = InfoBarSeverity.Informational;
            ResultMessageText.Text = progressMessage ?? (_isChinese ? "正在处理…" : "Working…");
            ResultInfoBar.IsOpen = true;
        }
        else
        {
            RenderAvailability();
        }
    }

    private void ShowResult(string message, InfoBarSeverity severity)
    {
        ResultMessageText.Text = message;
        ResultInfoBar.Severity = severity;
        ResultInfoBar.IsOpen = true;
    }

    private static string FormatFailure(string title, Exception exception)
    {
        var message = exception.Message.Trim();
        return string.IsNullOrEmpty(message) ? title : $"{title}: {message}";
    }
}
