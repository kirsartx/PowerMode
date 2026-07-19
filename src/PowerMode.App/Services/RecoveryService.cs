using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

public sealed record RecoveryActionResult(
    bool MutationSucceeded,
    bool AuditSucceeded,
    string? Error = null)
{
    public bool Succeeded => MutationSucceeded && AuditSucceeded;
    public bool IsPartialSuccess => MutationSucceeded && !AuditSucceeded;
}

public sealed record RecoveryBackupAvailability(
    ConfigurationBackupInfo? Backup,
    string? Error);

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
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<Guid, CompletedRecoveryOperation> _completedOperations = [];
    private readonly Guid _sessionOperationNamespace = Guid.NewGuid();

    public RecoveryService(IRecoveryHistory history, IRecoveryBackend backend)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<SwitchHistoryEntry?> FindLatestUndoableAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await FindLatestUndoableCoreAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<SwitchHistoryEntry?> FindLatestUndoableCoreAsync(
        CancellationToken cancellationToken)
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

    public async Task<RecoveryActionResult> ResetDefaultsAsync(
        Func<CancellationToken, Task> strictReloadAndApplyAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strictReloadAndApplyAsync);
        var operationId = CreateStableOperationId(
            _sessionOperationNamespace,
            "configuration-reset");
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_completedOperations.TryGetValue(operationId, out var completed))
                return await RecordCompletedOperationAsync(completed);

            try
            {
                await _backend.CreateSafetyBackupAsync(
                    "before-reset-defaults",
                    cancellationToken);
                await _backend.SaveDefaultSettingsAsync(cancellationToken);
                await strictReloadAndApplyAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new RecoveryActionResult(false, false, ex.Message);
            }

            completed = new CompletedRecoveryOperation(new SwitchHistoryEntry
            {
                Id = operationId,
                Timestamp = DateTimeOffset.Now,
                OperationKind = "configuration-reset",
                Trigger = "recovery-center",
                Reason = "Reset settings to defaults",
                Succeeded = true
            });
            _completedOperations[operationId] = completed;
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
        var operationId = CreateStableOperationId(
            _sessionOperationNamespace,
            $"configuration-restore:{operationIdentity}");
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_completedOperations.TryGetValue(operationId, out var completed))
                return await RecordCompletedOperationAsync(completed);

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

            completed = new CompletedRecoveryOperation(new SwitchHistoryEntry
            {
                Id = operationId,
                Timestamp = DateTimeOffset.Now,
                OperationKind = "configuration-restore",
                TargetMode = Path.GetFileName(restoreResult.BackupPath),
                Trigger = "recovery-center",
                Reason = restoreResult.SafetyBackup is null
                    ? string.Empty
                    : $"Safety backup: {restoreResult.SafetyBackup.FileName}",
                Succeeded = true
            });
            _completedOperations[operationId] = completed;
            return await RecordCompletedOperationAsync(completed);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RecoveryActionResult> UndoLatestAsync(
        Func<string, CancellationToken, Task<bool>> executeModeAsync,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executeModeAsync);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var pendingAudit = _completedOperations.Values.FirstOrDefault(operation =>
                !operation.AuditSucceeded
                && string.Equals(
                    operation.Entry.OperationKind,
                    "mode-undo",
                    StringComparison.OrdinalIgnoreCase));
            if (pendingAudit is not null)
                return await RecordCompletedOperationAsync(pendingAudit);

            var original = await FindLatestUndoableCoreAsync(cancellationToken);
            if (original is null)
                return new RecoveryActionResult(false, false, "No eligible mode operation is available.");

            var stopwatch = Stopwatch.StartNew();
            bool mutationSucceeded;
            try
            {
                mutationSucceeded = await executeModeAsync(
                    original.PreviousMode,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new RecoveryActionResult(false, false, ex.Message);
            }
            finally
            {
                stopwatch.Stop();
            }

            if (!mutationSucceeded)
                return new RecoveryActionResult(false, false, "The mode pipeline did not complete.");

            var operationId = CreateStableOperationId(original.Id, "mode-undo");
            var completed = new CompletedRecoveryOperation(new SwitchHistoryEntry
            {
                Id = operationId,
                Timestamp = DateTimeOffset.Now,
                OperationKind = "mode-undo",
                RelatedOperationId = original.Id,
                IsUndo = true,
                PreviousMode = original.TargetMode,
                TargetMode = original.PreviousMode,
                Trigger = "recovery-center",
                Reason = reason,
                Succeeded = true,
                DurationMilliseconds = stopwatch.ElapsedMilliseconds
            });
            _completedOperations[operationId] = completed;
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
            operation.AuditSucceeded = true;
            return new RecoveryActionResult(true, true);
        }
        catch (Exception ex)
        {
            operation.AuditSucceeded = false;
            return new RecoveryActionResult(true, false, ex.Message);
        }
    }

    private static Guid CreateStableOperationId(Guid sourceId, string operationKind)
    {
        var sourceBytes = sourceId.ToByteArray();
        var kindBytes = Encoding.UTF8.GetBytes(operationKind);
        var input = new byte[sourceBytes.Length + kindBytes.Length];
        sourceBytes.CopyTo(input, 0);
        kindBytes.CopyTo(input, sourceBytes.Length);
        return new Guid(SHA256.HashData(input).AsSpan(0, 16));
    }

    private sealed class CompletedRecoveryOperation(SwitchHistoryEntry entry)
    {
        public SwitchHistoryEntry Entry { get; } = entry;
        public bool AuditSucceeded { get; set; }
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
