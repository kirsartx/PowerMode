using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class RecoveredSettingsActivationFlowTests
{
    [Fact]
    public async Task RunAsync_AcceptsRecoveredSettingsButDoesNotApplyPresentationWhenResumeFails()
    {
        var events = new List<string>();
        SettingsLoadResult? accepted = null;
        var flow = new RecoveredSettingsActivationFlow(
            recovered =>
            {
                accepted = recovered;
                events.Add("accept");
            },
            _ =>
            {
                events.Add("resume");
                throw new InvalidOperationException("journal remains unresolved");
            },
            _ => events.Add("apply"));
        var recovered = LoadedSettings();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            flow.RunAsync(recovered));

        Assert.Same(recovered, accepted);
        Assert.Equal(["accept", "resume"], events);
    }

    [Fact]
    public async Task RunAsync_AppliesPresentationOnlyAfterResumeSucceeds()
    {
        var events = new List<string>();
        var flow = new RecoveredSettingsActivationFlow(
            _ => events.Add("accept"),
            _ =>
            {
                events.Add("resume");
                return Task.CompletedTask;
            },
            _ => events.Add("apply"));

        await flow.RunAsync(LoadedSettings());

        Assert.Equal(["accept", "resume", "apply"], events);
    }

    private static SettingsLoadResult LoadedSettings() =>
        new(SettingsLoadState.Loaded, new PowerModeSettings());
}
