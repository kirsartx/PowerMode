namespace PowerModeWinUI;

internal sealed class PowerStateRevisionGate
{
    private long _revision;

    public long Capture() => Volatile.Read(ref _revision);

    public long BeginMutation() => Interlocked.Increment(ref _revision);

    public bool CanApply(long capturedRevision, bool mutationInProgress) =>
        !mutationInProgress && capturedRevision == Volatile.Read(ref _revision);
}
