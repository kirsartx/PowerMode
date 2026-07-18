using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PowerModeWinUI;

public interface IRecoveryHistory
{
    /// <summary>
    /// Returns up to <paramref name="maximumCount"/> recent entries in any order.
    /// Consumers that need chronological ordering must apply it themselves.
    /// </summary>
    Task<IReadOnlyList<SwitchHistoryEntry>> GetRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task RecordAsync(
        SwitchHistoryEntry entry,
        CancellationToken cancellationToken = default);
}

public interface IRecoveryBackend
{
    Task CreateSafetyBackupAsync(string reason, CancellationToken token);
    Task SaveDefaultSettingsAsync(CancellationToken token);
}

public sealed class ProductionRecoveryBackend : IRecoveryBackend
{
    private readonly Func<string, CancellationToken, Task> _createSafetyBackupAsync;
    private readonly Action<PowerModeSettings> _saveSettings;

    public ProductionRecoveryBackend(
        SystemIntegrationService systemIntegration,
        Func<int> backupRetentionProvider)
    {
        ArgumentNullException.ThrowIfNull(systemIntegration);
        ArgumentNullException.ThrowIfNull(backupRetentionProvider);
        _createSafetyBackupAsync = async (reason, token) =>
        {
            await systemIntegration.CreateConfigurationBackupAsync(
                    SettingsStore.FilePath,
                    reason,
                    Math.Clamp(backupRetentionProvider(), 1, 50),
                    token)
                .ConfigureAwait(false);
        };
        _saveSettings = SettingsStore.Save;
    }

    internal ProductionRecoveryBackend(
        Func<string, CancellationToken, Task> createSafetyBackupAsync,
        Action<PowerModeSettings> saveSettings)
    {
        _createSafetyBackupAsync = createSafetyBackupAsync
            ?? throw new ArgumentNullException(nameof(createSafetyBackupAsync));
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
    }

    public Task CreateSafetyBackupAsync(string reason, CancellationToken token) =>
        _createSafetyBackupAsync(reason, token);

    public Task SaveDefaultSettingsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _saveSettings(new PowerModeSettings());
        return Task.CompletedTask;
    }
}

public sealed class RecoveryService
{
    /// <summary>
    /// Recovery surfaces inspect only the most recent 2,000 persisted history entries.
    /// This deliberately bounded contract does not perform an unlimited history scan.
    /// </summary>
    public const int MaximumRecoveryHistoryEntries = 2_000;

    private static readonly HashSet<string> StandardModes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "remote",
            "saver",
            "balanced",
            "high"
        };

    private readonly IRecoveryHistory _history;
    private readonly IRecoveryBackend _backend;

    public RecoveryService(IRecoveryHistory history, IRecoveryBackend backend)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<SwitchHistoryEntry?> FindLatestUndoableAsync(
        CancellationToken cancellationToken = default)
    {
        var recentEntries = await _history
            .GetRecentAsync(MaximumRecoveryHistoryEntries, cancellationToken)
            .ConfigureAwait(false);
        var entries = recentEntries
            .OrderByDescending(entry => entry.Timestamp)
            .ToArray();
        var undoneOperationIds = entries
            .Where(entry =>
                entry.Succeeded
                && entry.IsUndo
                && string.Equals(entry.OperationKind, "mode-undo", StringComparison.OrdinalIgnoreCase)
                && entry.RelatedOperationId.HasValue)
            .Select(entry => entry.RelatedOperationId!.Value)
            .ToHashSet();

        return entries.FirstOrDefault(entry =>
            entry.Succeeded
            && string.Equals(entry.OperationKind, "mode-switch", StringComparison.OrdinalIgnoreCase)
            && StandardModes.Contains(entry.PreviousMode)
            && StandardModes.Contains(entry.TargetMode)
            && !undoneOperationIds.Contains(entry.Id));
    }

    public async Task ResetDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await _backend.CreateSafetyBackupAsync("before-reset-defaults", cancellationToken)
            .ConfigureAwait(false);
        await _backend.SaveDefaultSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task RecordConfigurationResetAsync(
        CancellationToken cancellationToken = default) =>
        _history.RecordAsync(new SwitchHistoryEntry
        {
            Timestamp = DateTimeOffset.Now,
            OperationKind = "configuration-reset",
            Trigger = "recovery-center",
            Reason = "Reset settings to defaults",
            Succeeded = true
        }, cancellationToken);

    public Task RecordConfigurationRestoreAsync(
        ConfigurationRestoreResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _history.RecordAsync(new SwitchHistoryEntry
        {
            Timestamp = DateTimeOffset.Now,
            OperationKind = "configuration-restore",
            TargetMode = Path.GetFileName(result.BackupPath),
            Trigger = "recovery-center",
            Reason = result.SafetyBackup is null
                ? string.Empty
                : $"Safety backup: {result.SafetyBackup.FileName}",
            Succeeded = result.Succeeded,
            ErrorMessage = result.Error
        }, cancellationToken);
    }

    public async Task<bool> UndoLatestAsync(
        Func<string, CancellationToken, Task<bool>> executeModeAsync,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executeModeAsync);
        var original = await FindLatestUndoableAsync(cancellationToken);
        if (original is null)
            return false;

        var stopwatch = Stopwatch.StartNew();
        var succeeded = false;
        string? error = null;
        try
        {
            succeeded = await executeModeAsync(original.PreviousMode, cancellationToken)
                .ConfigureAwait(false);
            return succeeded;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            await _history.RecordAsync(new SwitchHistoryEntry
            {
                Timestamp = DateTimeOffset.Now,
                OperationKind = "mode-undo",
                RelatedOperationId = original.Id,
                IsUndo = true,
                PreviousMode = original.TargetMode,
                TargetMode = original.PreviousMode,
                Trigger = "recovery-center",
                Reason = reason,
                Succeeded = succeeded,
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                ErrorMessage = error
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

public static class RecoveryOperationFormatter
{
    public static string FormatOperation(SwitchHistoryEntry entry, bool isChinese)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var kind = string.IsNullOrWhiteSpace(entry.OperationKind)
            ? "mode-switch"
            : entry.OperationKind.Trim();
        return kind.ToLowerInvariant() switch
        {
            "mode-switch" => $"{entry.PreviousMode} → {entry.TargetMode}",
            "mode-undo" => isChinese
                ? $"撤销 · {entry.PreviousMode} → {entry.TargetMode}"
                : $"Undo · {entry.PreviousMode} → {entry.TargetMode}",
            "configuration-restore" => isChinese ? "配置已恢复" : "Configuration restored",
            "configuration-reset" => isChinese ? "已重置为默认设置" : "Defaults reset",
            _ => kind
        };
    }
}
