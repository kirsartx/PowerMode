namespace PowerModeWinUI;

internal sealed class RecoveredSettingsActivationFlow
{
    private readonly Action<SettingsLoadResult> _accept;
    private readonly Func<CancellationToken, Task> _resumeAsync;
    private readonly Action<SettingsLoadResult> _applyAfterResume;

    public RecoveredSettingsActivationFlow(
        Action<SettingsLoadResult> accept,
        Func<CancellationToken, Task> resumeAsync,
        Action<SettingsLoadResult> applyAfterResume)
    {
        _accept = accept ?? throw new ArgumentNullException(nameof(accept));
        _resumeAsync = resumeAsync ?? throw new ArgumentNullException(nameof(resumeAsync));
        _applyAfterResume = applyAfterResume
            ?? throw new ArgumentNullException(nameof(applyAfterResume));
    }

    public async Task RunAsync(
        SettingsLoadResult recovered,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recovered);
        if (!recovered.AllowsExternalSideEffects)
        {
            throw new InvalidOperationException(
                "Recovered settings must be usable before startup can resume.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _accept(recovered);
        await _resumeAsync(cancellationToken).ConfigureAwait(false);
        _applyAfterResume(recovered);
    }
}
