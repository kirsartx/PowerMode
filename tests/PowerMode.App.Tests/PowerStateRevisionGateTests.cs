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
    public void NewRevisionCanBeAppliedAfterMutation()
    {
        var gate = new PowerStateRevisionGate();
        gate.BeginMutation();
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
}
