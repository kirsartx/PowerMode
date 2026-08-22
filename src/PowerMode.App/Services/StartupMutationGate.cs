namespace PowerModeWinUI;

internal sealed record StartupMutationAttempt<T>(bool Allowed, T Value);

internal sealed class StartupMutationGate
{
    private int _open;

    public bool IsOpen => Volatile.Read(ref _open) != 0;

    public void Open() => Interlocked.Exchange(ref _open, 1);

    public async Task<StartupMutationAttempt<T>> TryRunAsync<T>(
        Func<Task<T>> mutationAsync)
    {
        ArgumentNullException.ThrowIfNull(mutationAsync);
        if (!IsOpen)
            return new(false, default!);

        return new(true, await mutationAsync().ConfigureAwait(false));
    }
}
