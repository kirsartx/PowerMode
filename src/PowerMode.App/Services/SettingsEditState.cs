namespace PowerModeWinUI;

internal enum SettingsCloseChoice
{
    Save,
    Discard,
    ContinueEditing
}

internal enum SettingsCloseAction
{
    SaveAndClose,
    CloseWithoutSaving,
    KeepOpen
}

internal sealed class SettingsEditState
{
    public bool IsDirty { get; private set; }
    public bool IsInputValid { get; private set; } = true;
    public bool CanSave => IsDirty && IsInputValid;

    public void MarkChanged() => IsDirty = true;

    public void SetInputValid(bool value) => IsInputValid = value;

    public void MarkSaved() => IsDirty = false;

    public SettingsCloseAction ResolveClose(SettingsCloseChoice choice) =>
        choice switch
        {
            SettingsCloseChoice.Save when CanSave =>
                SettingsCloseAction.SaveAndClose,
            SettingsCloseChoice.Save => SettingsCloseAction.KeepOpen,
            SettingsCloseChoice.Discard => SettingsCloseAction.CloseWithoutSaving,
            SettingsCloseChoice.ContinueEditing => SettingsCloseAction.KeepOpen,
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };
}

internal sealed record SettingsSaveResult(bool Succeeded, string? Error = null)
{
    public static SettingsSaveResult Success { get; } = new(true);
}

internal sealed class SettingsEditSession
{
    private readonly Func<PowerModeSettings, CancellationToken, Task> _persistAsync;
    private readonly Action<PowerModeSettings> _applyOwner;

    public SettingsEditSession(
        PowerModeSettings ownerSettings,
        Func<PowerModeSettings, CancellationToken, Task> persistAsync,
        Action<PowerModeSettings> applyOwner)
    {
        ArgumentNullException.ThrowIfNull(ownerSettings);
        ArgumentNullException.ThrowIfNull(persistAsync);
        ArgumentNullException.ThrowIfNull(applyOwner);

        WorkingCopy = SettingsStore.Clone(ownerSettings);
        _persistAsync = persistAsync;
        _applyOwner = applyOwner;
    }

    public SettingsEditState State { get; } = new();

    public PowerModeSettings WorkingCopy { get; private set; }

    public void Mutate(Action<PowerModeSettings> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation(WorkingCopy);
        State.MarkChanged();
    }

    public bool MutateWhenConfirmed(
        bool confirmed,
        Action<PowerModeSettings> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (!confirmed)
            return false;

        Mutate(mutation);
        return true;
    }

    public void ReplaceWorkingCopy(PowerModeSettings settings, bool markDirty)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WorkingCopy = SettingsStore.Clone(settings);
        if (markDirty)
            State.MarkChanged();
        else
            State.MarkSaved();
    }

    public PowerModeSettings CreateExportSnapshot() =>
        SettingsStore.Clone(WorkingCopy);

    public async Task<SettingsSaveResult> SaveAsync(
        CancellationToken cancellationToken = default)
    {
        if (!State.IsDirty)
            return SettingsSaveResult.Success;
        if (!State.IsInputValid)
            return new(false, "Settings contain invalid input.");

        var persistenceSnapshot = SettingsStore.Clone(WorkingCopy);
        var ownerSnapshot = SettingsStore.Clone(WorkingCopy);
        var nextWorkingCopy = SettingsStore.Clone(WorkingCopy);

        try
        {
            await _persistAsync(persistenceSnapshot, cancellationToken);
            _applyOwner(ownerSnapshot);
        }
        catch (Exception exception)
        {
            return new(false, exception.Message);
        }

        WorkingCopy = nextWorkingCopy;
        State.MarkSaved();
        return SettingsSaveResult.Success;
    }
}

internal sealed class SettingsCloseCoordinator
{
    private readonly SettingsEditState _state;
    private readonly Func<Task<bool>> _saveAsync;
    private bool _requestInProgress;

    public SettingsCloseCoordinator(
        SettingsEditState state,
        Func<Task<bool>> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(saveAsync);
        _state = state;
        _saveAsync = saveAsync;
    }

    public async Task<bool> RequestCloseAsync(
        Func<bool, Task<SettingsCloseChoice>> chooseAsync)
    {
        ArgumentNullException.ThrowIfNull(chooseAsync);
        if (_requestInProgress)
            return false;
        if (!_state.IsDirty)
            return true;

        _requestInProgress = true;
        try
        {
            var choice = await chooseAsync(_state.CanSave);
            return _state.ResolveClose(choice) switch
            {
                SettingsCloseAction.SaveAndClose => await _saveAsync(),
                SettingsCloseAction.CloseWithoutSaving => true,
                _ => false
            };
        }
        finally
        {
            _requestInProgress = false;
        }
    }
}
