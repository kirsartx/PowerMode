using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PowerModeWinUI;

public sealed partial class RecoveryCenterWindow : Window
{
    private readonly MainWindow _owner;
    private readonly bool _isChinese;
    private SwitchHistoryEntry? _latestUndo;
    private ConfigurationBackupInfo? _latestBackup;
    private readonly CancellationTokenSource _lifetimeCancellation;
    private bool _busy;
    private bool _closed;

    public RecoveryCenterWindow(MainWindow owner, bool isChinese)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _isChinese = isChinese;
        _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            owner.RecoveryLifetimeToken);
        InitializeComponent();
        DpiAwareWindowSizer.Resize(this, 820, 650, 720, 560);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
        ApplyLanguage();
        Closed += RecoveryCenterWindow_Closed;
    }

    private void ApplyLanguage()
    {
        Title = _isChinese ? "PowerMode 恢复中心" : "PowerMode Recovery Center";
        if (!_isChinese)
        {
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

        AutomationProperties.SetName(
            UndoButton,
            RecoveryCenterAutomationLabels.Undo(_isChinese));
        AutomationProperties.SetName(
            RestoreButton,
            RecoveryCenterAutomationLabels.Restore(_isChinese));
        AutomationProperties.SetName(
            ResetButton,
            RecoveryCenterAutomationLabels.Reset(_isChinese));
        AutomationProperties.SetName(
            ResultMessageText,
            RecoveryCenterAutomationLabels.Result(_isChinese));
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshAvailabilityAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAvailabilityAsync(bool showProgress = true)
    {
        if (!TryUpdatePresentation(() =>
            {
                _latestUndo = null;
                _latestBackup = null;
                SetBusy(
                    true,
                    _isChinese
                        ? "正在检查可用的恢复操作…"
                        : "Checking available recovery operations…",
                    updateMessage: showProgress);
            }))
            return;

        var latestUndo = default(SwitchHistoryEntry);
        var backupAvailability = default(RecoveryBackupAvailability);
        try
        {
            latestUndo = await _owner.FindLatestUndoableModeOperationAsync(
                _lifetimeCancellation.Token);
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            backupAvailability = _owner.GetLatestDistinctSettingsBackup();
            if (!string.IsNullOrWhiteSpace(backupAvailability.Error))
                throw new IOException(backupAvailability.Error);
            TryUpdatePresentation(() =>
            {
                _latestUndo = latestUndo;
                _latestBackup = backupAvailability.Backup;
                RenderAvailability();
            });
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TryUpdatePresentation(() =>
                ShowResult(
                    FormatFailure(
                        _isChinese ? "读取恢复状态失败" : "Could not read recovery state",
                        ex),
                    InfoBarSeverity.Error));
        }
        finally
        {
            TryUpdatePresentation(() => SetBusy(false));
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
        var undo = _latestUndo;
        if (undo is null || !TryBeginOperation(
                _isChinese ? "等待确认撤销操作…" : "Waiting for undo confirmation…"))
            return;

        var refreshAvailability = false;
        try
        {
            var confirmed = await ConfirmAsync(
                _isChinese ? "撤销最近模式切换？" : "Undo latest mode switch?",
                _isChinese
                    ? $"PowerMode 将通过现有模式管线恢复到 {undo.PreviousMode}。"
                    : $"PowerMode will use the existing mode pipeline to return to {undo.PreviousMode}.",
                _isChinese ? "撤销" : "Undo");
            if (!confirmed)
                return;
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            TryUpdatePresentation(() => SetBusy(
                true,
                _isChinese ? "正在撤销模式切换…" : "Undoing mode switch…"));
            var result = await _owner.UndoLatestModeOperationAsync(
                _lifetimeCancellation.Token);
            refreshAvailability = true;
            TryUpdatePresentation(() => ShowActionResult(
                result,
                _isChinese ? "最近模式切换已撤销。" : "The latest mode switch was undone.",
                _isChinese
                    ? "模式已恢复，但操作记录失败。"
                    : "The mode was restored, but its audit record failed.",
                _isChinese
                    ? "未能撤销最近模式切换"
                    : "The latest mode switch could not be undone"));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TryUpdatePresentation(() =>
                ShowResult(
                    FormatFailure(_isChinese ? "撤销失败" : "Undo failed", ex),
                    InfoBarSeverity.Error));
        }
        finally
        {
            if (refreshAvailability && !_closed)
                await RefreshAvailabilityAsync(showProgress: false);
            else
                TryUpdatePresentation(() => SetBusy(false));
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var backup = _latestBackup;
        if (backup is null || !TryBeginOperation(
                _isChinese ? "等待确认配置恢复…" : "Waiting for restore confirmation…"))
            return;

        var refreshAvailability = false;
        try
        {
            var confirmed = await ConfirmAsync(
                _isChinese ? "恢复配置备份？" : "Restore configuration backup?",
                _isChinese
                    ? "当前 settings.json 会先创建安全备份，再原子恢复所选版本。当前 Windows 电源方案不会改变。"
                    : "Current settings.json will be backed up before the selected version is restored atomically. The active Windows power plan will not change.",
                _isChinese ? "恢复配置" : "Restore");
            if (!confirmed)
                return;
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            TryUpdatePresentation(() => SetBusy(
                true,
                _isChinese ? "正在安全恢复配置…" : "Restoring configuration safely…"));
            var result = await _owner.RestoreSettingsBackupAsync(
                backup,
                _lifetimeCancellation.Token);
            refreshAvailability = true;
            TryUpdatePresentation(() => ShowActionResult(
                result,
                _isChinese ? "配置已恢复并重新加载。" : "Configuration was restored and reloaded.",
                _isChinese
                    ? "配置已恢复，但操作记录失败。"
                    : "Configuration was restored, but its audit record failed.",
                _isChinese ? "配置恢复失败" : "Configuration restore failed"));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TryUpdatePresentation(() =>
                ShowResult(
                    FormatFailure(
                        _isChinese ? "配置恢复失败" : "Configuration restore failed",
                        ex),
                    InfoBarSeverity.Error));
        }
        finally
        {
            if (refreshAvailability && !_closed)
                await RefreshAvailabilityAsync(showProgress: false);
            else
                TryUpdatePresentation(() => SetBusy(false));
        }
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation(
                _isChinese ? "等待确认默认值重置…" : "Waiting for reset confirmation…"))
            return;

        var refreshAvailability = false;
        try
        {
            var confirmed = await ConfirmAsync(
                _isChinese ? "重置 PowerMode 设置？" : "Reset PowerMode settings?",
                _isChinese
                    ? "将先创建安全备份，再用默认值替换 settings.json。历史、备份、遥测和当前 Windows 电源方案不会改变。"
                    : "A safety backup will be created before settings.json is replaced with defaults. History, backups, telemetry and the active Windows power plan will not change.",
                _isChinese ? "重置默认值" : "Reset defaults");
            if (!confirmed)
                return;
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            TryUpdatePresentation(() => SetBusy(
                true,
                _isChinese
                    ? "正在创建安全备份并重置…"
                    : "Creating safety backup and resetting…"));
            var result = await _owner.ResetSettingsDefaultsAsync(
                _lifetimeCancellation.Token);
            refreshAvailability = true;
            TryUpdatePresentation(() => ShowActionResult(
                result,
                _isChinese
                    ? "设置已重置为默认值并重新加载。"
                    : "Settings were reset to defaults and reloaded.",
                _isChinese
                    ? "设置已重置，但操作记录失败。"
                    : "Settings were reset, but its audit record failed.",
                _isChinese ? "重置失败" : "Reset failed"));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TryUpdatePresentation(() =>
                ShowResult(
                    FormatFailure(_isChinese ? "重置失败" : "Reset failed", ex),
                    InfoBarSeverity.Error));
        }
        finally
        {
            if (refreshAvailability && !_closed)
                await RefreshAvailabilityAsync(showProgress: false);
            else
                TryUpdatePresentation(() => SetBusy(false));
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

    private bool TryBeginOperation(string progressMessage)
    {
        if (_busy || _closed || _lifetimeCancellation.IsCancellationRequested)
            return false;

        SetBusy(true, progressMessage);
        return true;
    }

    private bool TryUpdatePresentation(Action update)
    {
        if (_closed || _lifetimeCancellation.IsCancellationRequested)
            return false;

        update();
        return true;
    }

    private void ShowActionResult(
        RecoveryActionResult result,
        string successMessage,
        string partialSuccessMessage,
        string failureTitle)
    {
        if (result.Succeeded)
        {
            ShowResult(successMessage, InfoBarSeverity.Success);
            return;
        }

        if (result.IsPartialSuccess)
        {
            ShowResult(partialSuccessMessage, InfoBarSeverity.Warning);
            return;
        }

        ShowResult(
            string.IsNullOrWhiteSpace(result.Error)
                ? failureTitle
                : $"{failureTitle}: {result.Error}",
            InfoBarSeverity.Error);
    }

    private void RecoveryCenterWindow_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _latestUndo = null;
        _latestBackup = null;

        if (!_lifetimeCancellation.IsCancellationRequested)
            _lifetimeCancellation.Cancel();
    }

    private void SetBusy(
        bool busy,
        string? progressMessage = null,
        bool updateMessage = true)
    {
        _busy = busy;
        BusyProgressRing.IsActive = busy;
        BusyProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            UndoButton.IsEnabled = false;
            RestoreButton.IsEnabled = false;
            ResetButton.IsEnabled = false;
            if (updateMessage)
            {
                ResultInfoBar.Severity = InfoBarSeverity.Informational;
                ResultMessageText.Text = progressMessage ?? (_isChinese ? "正在处理…" : "Working…");
                ResultInfoBar.IsOpen = true;
            }
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
