using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class CapabilityPresentationLifetimeTests
{
    [Fact]
    public void Close_CancelsAndRejectsQueuedPresentation()
    {
        using var lifetime = new CapabilityPresentationLifetime();
        var invoked = false;

        lifetime.Close();
        var applied = lifetime.TryApply(() => invoked = true);

        Assert.True(lifetime.Token.IsCancellationRequested);
        Assert.False(applied);
        Assert.False(invoked);
    }

    [Fact]
    public void TryApply_CallbackThrows_ContainsException()
    {
        using var lifetime = new CapabilityPresentationLifetime();

        var applied = lifetime.TryApply(
            () => throw new InvalidOperationException("Window already closed."));

        Assert.False(applied);
    }
}
