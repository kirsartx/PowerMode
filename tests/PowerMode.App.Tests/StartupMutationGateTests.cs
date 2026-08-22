using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class StartupMutationGateTests
{
    [Fact]
    public async Task TryRunAsync_DefaultClosedGateRejectsEarlyMutation()
    {
        var mutations = 0;
        var gate = new StartupMutationGate();

        var early = await gate.TryRunAsync(() =>
        {
            mutations++;
            return Task.FromResult(true);
        });

        Assert.False(early.Allowed);
        Assert.False(early.Value);
        Assert.Equal(0, mutations);

        gate.Open();
        var ready = await gate.TryRunAsync(() =>
        {
            mutations++;
            return Task.FromResult(true);
        });

        Assert.True(ready.Allowed);
        Assert.True(ready.Value);
        Assert.Equal(1, mutations);
    }

    [Fact]
    public async Task InitializeAsync_OpensGateAfterJournalAndSnapshotBeforeActivation()
    {
        var events = new List<string>();
        var gate = new StartupMutationGate();
        var coordinator = new StartupCoordinator(
            new OrderedJournal(events),
            new OrderedBackend(events),
            async (_, _) =>
            {
                var attempt = await gate.TryRunAsync(() =>
                {
                    events.Add("mutation");
                    return Task.FromResult(true);
                });
                Assert.True(attempt.Allowed);
                events.Add("activate");
            },
            gate);

        var result = await coordinator.InitializeAsync(LoadedSettings());

        Assert.False(result.ActivationDeferred);
        Assert.True(gate.IsOpen);
        Assert.Equal(["journal", "read-state", "mutation", "activate"], events);
    }

    [Fact]
    public async Task ResumeAfterRecoveryAsync_KeepsGateClosedUntilJournalResolves()
    {
        var gate = new StartupMutationGate();
        var journal = new OrderedJournal([])
        {
            Current = Record(LastOperationStatus.Applying)
        };
        var activations = 0;
        var coordinator = new StartupCoordinator(
            journal,
            new OrderedBackend([]),
            (_, _) =>
            {
                Assert.True(gate.IsOpen);
                activations++;
                return Task.CompletedTask;
            },
            gate);

        await coordinator.InitializeAsync(LoadedSettings());
        Assert.False(gate.IsOpen);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ResumeAfterRecoveryAsync());
        Assert.False(gate.IsOpen);
        Assert.Equal(0, activations);

        journal.Current = Record(LastOperationStatus.Verified);
        await coordinator.ResumeAfterRecoveryAsync();
        Assert.True(gate.IsOpen);
        Assert.Equal(1, activations);
    }

    private static SettingsLoadResult LoadedSettings() =>
        new(SettingsLoadState.Loaded, new PowerModeSettings());

    private static LastOperationRecord Record(LastOperationStatus status) => new(
        1,
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "startup-test",
        null,
        PowerModeTarget.ForPreset(PowerModePreset.Remote),
        31,
        false,
        DateTimeOffset.Parse("2026-08-22T12:00:00Z"),
        State(),
        status);

    private static PowerModeState State() => new(
        Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
        "Balanced",
        PowerModePreset.Balanced,
        PowerSourceKind.Ac,
        null,
        100,
        100,
        5,
        5,
        2,
        2,
        100,
        100,
        600,
        600,
        0,
        0,
        0,
        0,
        false);

    private sealed class OrderedJournal(List<string> events) : ILastOperationStore
    {
        public LastOperationRecord? Current { get; set; }

        public Task<LastOperationReadResult> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            events.Add("journal");
            return Task.FromResult(new LastOperationReadResult(Current, null));
        }

        public Task<LastOperationWriteResult> WriteAsync(
            LastOperationRecord record,
            Guid? expectedOperationId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class OrderedBackend(List<string> events) : IPowerModeBackend
    {
        public Task<PowerModeStateResult> ReadStateAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            events.Add("read-state");
            var state = State();
            return Task.FromResult(new PowerModeStateResult(
                state,
                new BackendOperationResult(
                    operationId,
                    "status",
                    null,
                    DateTimeOffset.UtcNow,
                    TimeSpan.Zero,
                    BackendOperationOutcome.Succeeded,
                    null,
                    state,
                    [],
                    [],
                    null)));
        }

        public Task<BackendOperationResult> ApplyAsync(
            Guid operationId,
            PowerModeTarget target,
            int? cpuMaximumPercent = null,
            bool disableWifi = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BackendOperationResult> RestoreAsync(
            Guid operationId,
            PowerModeState snapshot,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
