using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class WindowsCapabilityProbeResultTests
{
    [Theory]
    [InlineData(0, false, "supported", CapabilitySupport.Supported)]
    [InlineData(0, false, "unsupported", CapabilitySupport.Unsupported)]
    [InlineData(0, false, "", CapabilitySupport.Unknown)]
    [InlineData(0, false, "unexpected output", CapabilitySupport.Unknown)]
    [InlineData(1, false, "unsupported", CapabilitySupport.Unknown)]
    [InlineData(0, true, "supported", CapabilitySupport.Unknown)]
    public void ClassifyWmiProbeResult_DistinguishesSuccessAbsenceAndFailure(
        int exitCode,
        bool timedOut,
        string output,
        CapabilitySupport expected)
    {
        var result = MonitoringService.ClassifyWmiCapabilityProbeResult(
            exitCode,
            timedOut,
            output);

        Assert.Equal(expected, result);
    }
}
