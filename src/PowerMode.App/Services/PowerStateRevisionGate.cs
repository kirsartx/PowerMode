namespace PowerModeWinUI;

internal sealed class PowerStateRevisionGate
{
    private int _activeMutations;
    private long _revision;

    public bool MutationInProgress => Volatile.Read(ref _activeMutations) > 0;

    public long Capture() => Volatile.Read(ref _revision);

    public long BeginMutation()
    {
        Interlocked.Increment(ref _activeMutations);
        return Interlocked.Increment(ref _revision);
    }

    public void EndMutation()
    {
        if (Interlocked.Decrement(ref _activeMutations) < 0)
            throw new InvalidOperationException("Mutation lifecycle ended without a matching begin.");
    }

    public bool CanApply(long capturedRevision, bool mutationInProgress) =>
        !mutationInProgress && !MutationInProgress && capturedRevision == Volatile.Read(ref _revision);
}
