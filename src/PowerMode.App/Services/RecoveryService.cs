using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
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
    Task<ConfigurationBackupInfo> CreateSafetyBackupAsync(
        string reason,
        CancellationToken token);
    Task SaveDefaultSettingsAsync(CancellationToken token);
    Task RestoreSafetyBackupAsync(
        ConfigurationBackupInfo backup,
        CancellationToken token);
}

public sealed record RecoveryActionResult(
    bool MutationSucceeded,
    bool AuditSucceeded,
    string? Error = null,
    bool? RollbackSucceeded = null)
{
    public bool Succeeded => MutationSucceeded && AuditSucceeded;
    public bool IsPartialSuccess => MutationSucceeded && !AuditSucceeded;
}

public sealed record RecoveryBackupAvailability(
    ConfigurationBackupInfo? Backup,
    string? Error);

internal sealed record LastOperationAvailability(
    LastOperationRecord? Record,
    bool CanUndo,
    bool RequiresVerification,
    string? Error);

internal interface IRecoveryOperationService
{
    Task<LastOperationAvailability> GetLastOperationAvailabilityAsync(
        CancellationToken cancellationToken = default);

    Task<LastOperationVerificationResult> VerifyLastOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<RecoveryActionResult> RestoreBeforeStateAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}

public static class RecoveryBackupSelector
{
    public static RecoveryBackupAvailability FindLatestDistinct(
        IReadOnlyList<ConfigurationBackupInfo> backups,
        string currentSettingsPath,
        Func<string, string>? computeHash = null)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSettingsPath);
        if (backups.Count == 0)
            return new RecoveryBackupAvailability(null, null);

        try
        {
            var currentHash = (computeHash ?? ComputeFileHash)(currentSettingsPath);
            var backup = backups.FirstOrDefault(candidate =>
                !string.Equals(
                    candidate.Sha256,
                    currentHash,
                    StringComparison.OrdinalIgnoreCase));
            return new RecoveryBackupAvailability(backup, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RecoveryBackupAvailability(null, ex.Message);
        }
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed class ProductionRecoveryBackend : IRecoveryBackend
{
    private readonly Func<string, CancellationToken, Task<ConfigurationBackupInfo>>
        _createSafetyBackupAsync;
    private readonly Action<PowerModeSettings> _saveSettings;
    private readonly Func<ConfigurationBackupInfo, CancellationToken, Task>
        _restoreSafetyBackupAsync;

    public ProductionRecoveryBackend(
        SystemIntegrationService systemIntegration,
        Func<int> backupRetentionProvider)
    {
        ArgumentNullException.ThrowIfNull(systemIntegration);
        ArgumentNullException.ThrowIfNull(backupRetentionProvider);
        _createSafetyBackupAsync = (reason, token) =>
            systemIntegration.CreateConfigurationBackupAsync(
                SettingsStore.FilePath,
                reason,
                Math.Clamp(backupRetentionProvider(), 1, 50),
                token);
        _saveSettings = SettingsStore.Save;
        _restoreSafetyBackupAsync = async (backup, token) =>
        {
            var result = await systemIntegration.RestoreConfigurationBackupAsync(
                    backup.Path,
                    SettingsStore.FilePath,
                    createSafetyBackup: false,
                    token)
                .ConfigureAwait(false);
            if (!result.Succeeded)
                throw new IOException(result.Error ?? "Configuration rollback failed.");
        };
    }

    internal ProductionRecoveryBackend(
        Func<string, CancellationToken, Task<ConfigurationBackupInfo>>
            createSafetyBackupAsync,
        Action<PowerModeSettings> saveSettings,
        Func<ConfigurationBackupInfo, CancellationToken, Task>
            restoreSafetyBackupAsync)
    {
        _createSafetyBackupAsync = createSafetyBackupAsync
            ?? throw new ArgumentNullException(nameof(createSafetyBackupAsync));
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
        _restoreSafetyBackupAsync = restoreSafetyBackupAsync
            ?? throw new ArgumentNullException(nameof(restoreSafetyBackupAsync));
    }

    public Task<ConfigurationBackupInfo> CreateSafetyBackupAsync(
        string reason,
        CancellationToken token) =>
        _createSafetyBackupAsync(reason, token);

    public Task SaveDefaultSettingsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _saveSettings(new PowerModeSettings());
        return Task.CompletedTask;
    }

    public Task RestoreSafetyBackupAsync(
        ConfigurationBackupInfo backup,
        CancellationToken token) =>
        _restoreSafetyBackupAsync(backup, token);
}

internal sealed class RecoveryService : IRecoveryOperationService
{
    private readonly IRecoveryHistory _history;
    private readonly IRecoveryBackend _backend;
    private readonly ILastOperationStore? _lastOperationStore;
    private readonly IModeSwitchCoordinator? _modeSwitchCoordinator;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<Guid, CompletedRecoveryOperation> _completedOperations = [];

    public RecoveryService(IRecoveryHistory history, IRecoveryBackend backend)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    internal RecoveryService(
        ILastOperationStore lastOperationStore,
        IModeSwitchCoordinator modeSwitchCoordinator,
        IRecoveryHistory history,
        IRecoveryBackend backend)
        : this(history, backend)
    {
        _lastOperationStore = lastOperationStore
            ?? throw new ArgumentNullException(nameof(lastOperationStore));
        _modeSwitchCoordinator = modeSwitchCoordinator
            ?? throw new ArgumentNullException(nameof(modeSwitchCoordinator));
    }

    public async Task<LastOperationAvailability> GetLastOperationAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        var store = _lastOperationStore ?? throw new InvalidOperationException(
            "Last-operation recovery is not configured.");
        var read = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.Succeeded)
            return new(null, false, false, read.Error);
        if (read.Record is not { } record)
            return new(null, false, false, null);

        return new(
            record,
            record.Status == LastOperationStatus.Verified,
            record.Status is LastOperationStatus.Prepared or
                LastOperationStatus.Applying or
                LastOperationStatus.Uncertain,
            null);
    }

    public Task<LastOperationVerificationResult> VerifyLastOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        GetModeSwitchCoordinator().VerifyLastOperationAsync(
            operationId,
            cancellationToken);

    public Task<RecoveryActionResult> RestoreBeforeStateAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        GetModeSwitchCoordinator().RestoreBeforeStateAsync(
            operationId,
            cancellationToken);

    private IModeSwitchCoordinator GetModeSwitchCoordinator() =>
        _modeSwitchCoordinator ?? throw new InvalidOperationException(
            "Last-operation recovery is not configured.");

    public async Task<RecoveryActionResult> ResetDefaultsAsync(
        Func<CancellationToken, Task> strictReloadAndApplyAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strictReloadAndApplyAsync);
        const string intentKey = "configuration-reset";
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var pendingAudit = FindPendingAudit(intentKey);
            if (pendingAudit is not null)
                return await RecordCompletedOperationAsync(pendingAudit);

            ConfigurationBackupInfo safetyBackup;
            try
            {
                safetyBackup = await _backend.CreateSafetyBackupAsync(
                    "before-reset-defaults",
                    cancellationToken);
                await _backend.SaveDefaultSettingsAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new RecoveryActionResult(false, false, ex.Message);
            }

            try
            {
                await strictReloadAndApplyAsync(CancellationToken.None);
            }
            catch (Exception reloadError)
            {
                try
                {
                    await _backend.RestoreSafetyBackupAsync(
                        safetyBackup,
                        CancellationToken.None);
                    await strictReloadAndApplyAsync(CancellationToken.None);
                }
                catch (Exception rollbackError)
                {
                    return new RecoveryActionResult(
                        false,
                        false,
                        $"{reloadError.Message} Rollback failed: {rollbackError.Message}",
                        RollbackSucceeded: false);
                }

                return new RecoveryActionResult(
                    false,
                    false,
                    reloadError.Message,
                    RollbackSucceeded: true);
            }

            var completed = new CompletedRecoveryOperation(intentKey, new SwitchHistoryEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.Now,
                OperationKind = "configuration-reset",
                Trigger = "recovery-center",
                Reason = "Reset settings to defaults",
                Succeeded = true
            });
            _completedOperations[completed.Entry.Id] = completed;
            return await RecordCompletedOperationAsync(completed);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RecoveryActionResult> RestoreConfigurationAsync(
        string operationIdentity,
        Func<CancellationToken, Task<ConfigurationRestoreResult>> restoreAsync,
        Func<CancellationToken, Task> strictReloadAndApplyAsync,
        Func<ConfigurationRestoreResult, CancellationToken, Task> rollbackAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationIdentity);
        ArgumentNullException.ThrowIfNull(restoreAsync);
        ArgumentNullException.ThrowIfNull(strictReloadAndApplyAsync);
        ArgumentNullException.ThrowIfNull(rollbackAsync);
        var intentKey = $"configuration-restore:{operationIdentity}";
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var pendingAudit = FindPendingAudit(intentKey);
            if (pendingAudit is not null)
                return await RecordCompletedOperationAsync(pendingAudit);

            ConfigurationRestoreResult restoreResult;
            try
            {
                restoreResult = await restoreAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new RecoveryActionResult(false, false, ex.Message);
            }
            if (!restoreResult.Succeeded)
            {
                return new RecoveryActionResult(
                    false,
                    false,
                    restoreResult.Error ?? "Configuration restore did not complete.");
            }

            try
            {
                await strictReloadAndApplyAsync(cancellationToken);
            }
            catch (Exception reloadError)
            {
                try
                {
                    await rollbackAsync(restoreResult, CancellationToken.None);
                }
                catch (Exception rollbackError)
                {
                    return new RecoveryActionResult(
                        false,
                        false,
                        $"{reloadError.Message} Rollback failed: {rollbackError.Message}");
                }

                if (reloadError is OperationCanceledException
                    && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new RecoveryActionResult(false, false, reloadError.Message);
            }

            var completed = new CompletedRecoveryOperation(intentKey, new SwitchHistoryEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.Now,
                OperationKind = "configuration-restore",
                TargetMode = Path.GetFileName(restoreResult.BackupPath),
                Trigger = "recovery-center",
                Reason = restoreResult.SafetyBackup is null
                    ? string.Empty
                    : $"Safety backup: {restoreResult.SafetyBackup.FileName}",
                Succeeded = true
            });
            _completedOperations[completed.Entry.Id] = completed;
            return await RecordCompletedOperationAsync(completed);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _operationGate.Release();
    }

    private async Task<RecoveryActionResult> RecordCompletedOperationAsync(
        CompletedRecoveryOperation operation)
    {
        try
        {
            await _history.RecordAsync(operation.Entry, CancellationToken.None)
                .ConfigureAwait(false);
            _completedOperations.Remove(operation.Entry.Id);
            return new RecoveryActionResult(true, true);
        }
        catch (Exception ex)
        {
            return new RecoveryActionResult(true, false, ex.Message);
        }
    }

    private CompletedRecoveryOperation? FindPendingAudit(string intentKey) =>
        _completedOperations.Values.FirstOrDefault(operation =>
            string.Equals(operation.IntentKey, intentKey, StringComparison.Ordinal));

    private sealed class CompletedRecoveryOperation(
        string intentKey,
        SwitchHistoryEntry entry)
    {
        public string IntentKey { get; } = intentKey;
        public SwitchHistoryEntry Entry { get; } = entry;
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
