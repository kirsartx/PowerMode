using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PowerModeWinUI;

public interface IRecoveryHistory
{
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

public sealed class RecoveryService
{
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
        var entries = await _history.GetRecentAsync(2_000, cancellationToken).ConfigureAwait(false);
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
            && StandardModes.Contains(entry.TargetMode)
            && !undoneOperationIds.Contains(entry.Id));
    }

    public async Task ResetDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await _backend.CreateSafetyBackupAsync("before-reset-defaults", cancellationToken)
            .ConfigureAwait(false);
        await _backend.SaveDefaultSettingsAsync(cancellationToken).ConfigureAwait(false);
    }
}
