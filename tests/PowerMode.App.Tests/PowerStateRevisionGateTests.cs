using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class PowerStateRevisionGateTests
{
    [Fact]
    public void InitialRevisionCanBeApplied()
    {
        var gate = new PowerStateRevisionGate();
        var revision = gate.Capture();

        Assert.True(gate.CanApply(revision, mutationInProgress: false));
    }

    [Fact]
    public void BeginningMutationInvalidatesPreviouslyCapturedRevision()
    {
        var gate = new PowerStateRevisionGate();
        var captured = gate.Capture();

        gate.BeginMutation();

        Assert.False(gate.CanApply(captured, mutationInProgress: false));
    }

    [Fact]
    public void BeginningNewReadInvalidatesAnOlderRead()
    {
        var gate = new PowerStateRevisionGate();
        var captured = gate.BeginRead();

        gate.BeginRead();

        Assert.False(gate.CanApply(captured, mutationInProgress: false));
    }

    [Fact]
    public void NewRevisionCanBeAppliedAfterMutation()
    {
        var gate = new PowerStateRevisionGate();
        gate.BeginMutation();
        gate.EndMutation();
        var captured = gate.Capture();

        Assert.True(gate.CanApply(captured, mutationInProgress: false));
    }

    [Fact]
    public void ActiveMutationBlocksEvenTheCurrentRevision()
    {
        var gate = new PowerStateRevisionGate();
        var captured = gate.Capture();

        Assert.False(gate.CanApply(captured, mutationInProgress: true));
    }

    [Fact]
    public void MutationLifecycleBlocksRefreshUntilMutationEnds()
    {
        var gate = new PowerStateRevisionGate();
        gate.BeginMutation();
        var captured = gate.Capture();

        Assert.True(gate.MutationInProgress);
        Assert.False(gate.CanApply(captured, mutationInProgress: false));

        gate.EndMutation();

        Assert.False(gate.MutationInProgress);
        Assert.True(gate.CanApply(captured, mutationInProgress: false));
    }

    [Fact]
    public void NestedMutationLifecyclesRemainActiveUntilAllMutationsEnd()
    {
        var gate = new PowerStateRevisionGate();
        gate.BeginMutation();
        gate.BeginMutation();

        gate.EndMutation();
        Assert.True(gate.MutationInProgress);

        gate.EndMutation();
        Assert.False(gate.MutationInProgress);
    }

    [Fact]
    public async Task InFlightReadCannotApplyAfterMutationBegins()
    {
        var gate = new PowerStateRevisionGate();
        var captured = gate.Capture();
        var allowReadToComplete = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var read = Task.Run(async () =>
        {
            await allowReadToComplete.Task;
            return gate.CanApply(captured, mutationInProgress: false);
        });

        gate.BeginMutation();
        allowReadToComplete.SetResult();

        Assert.False(await read);
        gate.EndMutation();
    }
}
