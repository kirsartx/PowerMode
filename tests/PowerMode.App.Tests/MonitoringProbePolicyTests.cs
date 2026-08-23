using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class MonitoringProbePolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CapabilitySupport.Supported, true)]
    [InlineData(CapabilitySupport.Unsupported, false)]
    public void ShouldRun_KnownSupport_ReturnsExpected(
        CapabilitySupport support,
        bool expected)
    {
        Assert.Equal(expected, MonitoringProbePolicy.ShouldRun(
            support,
            Now,
            Now.AddMinutes(5)));
    }

    [Fact]
    public void ShouldRun_UnknownWaitsForFiveMinuteRetry()
    {
        var now = new DateTimeOffset(2026, 8, 9, 0, 4, 59, TimeSpan.Zero);

        Assert.False(MonitoringProbePolicy.ShouldRun(
            CapabilitySupport.Unknown,
            now,
            now.AddSeconds(1)));
        Assert.True(MonitoringProbePolicy.ShouldRun(
            CapabilitySupport.Unknown,
            now.AddSeconds(1),
            now.AddSeconds(1)));
    }

    [Theory]
    [InlineData(CapabilitySupport.Supported, CapabilitySupport.Supported, CapabilitySupport.Supported)]
    [InlineData(CapabilitySupport.Supported, CapabilitySupport.Unknown, CapabilitySupport.Unknown)]
    [InlineData(CapabilitySupport.Unknown, CapabilitySupport.Supported, CapabilitySupport.Unknown)]
    [InlineData(CapabilitySupport.Unknown, CapabilitySupport.Unknown, CapabilitySupport.Unknown)]
    [InlineData(CapabilitySupport.Unsupported, CapabilitySupport.Supported, CapabilitySupport.Unsupported)]
    [InlineData(CapabilitySupport.Supported, CapabilitySupport.Unsupported, CapabilitySupport.Unsupported)]
    [InlineData(CapabilitySupport.Unsupported, CapabilitySupport.Unknown, CapabilitySupport.Unsupported)]
    [InlineData(CapabilitySupport.Unknown, CapabilitySupport.Unsupported, CapabilitySupport.Unsupported)]
    [InlineData(CapabilitySupport.Unsupported, CapabilitySupport.Unsupported, CapabilitySupport.Unsupported)]
    public void CombineNvidia_UsesBothCapabilityResults(
        CapabilitySupport gpu,
        CapabilitySupport nvidiaSmi,
        CapabilitySupport expected)
    {
        Assert.Equal(expected, MonitoringProbePolicy.CombineNvidia(gpu, nvidiaSmi));
    }
}
