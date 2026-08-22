using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class StartupCoordinatorTests
{
    [Theory]
    [InlineData((int)LastOperationStatus.Prepared)]
    [InlineData((int)LastOperationStatus.Applying)]
    [InlineData((int)LastOperationStatus.Uncertain)]
    public async Task InitializeAsync_UnfinishedJournalDefersActivationAndReadsReality(
        int statusValue)
    {
        var status = (LastOperationStatus)statusValue;
        var journal = new StartupJournal { Current = Record(status) };
        var backend = new StartupBackend();
        var activations = 0;
        var coordinator = new StartupCoordinator(
            journal,
            backend,
            (_, _) =>
            {
                activations++;
                return Task.CompletedTask;
            });

        var result = await coordinator.InitializeAsync(LoadedSettings());

        Assert.True(result.ActivationDeferred);
        Assert.NotNull(result.LastOperation);
        Assert.NotNull(result.CurrentState);
        Assert.Equal(1, backend.ReadCount);
        Assert.Equal(0, activations);
    }

    [Theory]
    [InlineData((int)LastOperationStatus.Verified)]
    [InlineData((int)LastOperationStatus.RolledBack)]
    public async Task InitializeAsync_ResolvedJournalActivatesNormally(int statusValue)
    {
        var status = (LastOperationStatus)statusValue;
        var activations = 0;
        var coordinator = new StartupCoordinator(
            new StartupJournal { Current = Record(status) },
            new StartupBackend(),
            (_, _) =>
            {
                activations++;
                return Task.CompletedTask;
            });

        var result = await coordinator.InitializeAsync(LoadedSettings());

        Assert.False(result.ActivationDeferred);
        Assert.Equal(1, activations);
    }

    [Fact]
    public async Task InitializeAsync_NoJournalActivatesNormally()
    {
        var activations = 0;
        var coordinator = new StartupCoordinator(
            new StartupJournal(),
            new StartupBackend(),
            (_, _) =>
            {
                activations++;
                return Task.CompletedTask;
            });

        var result = await coordinator.InitializeAsync(LoadedSettings());

        Assert.False(result.ActivationDeferred);
        Assert.Null(result.LastOperation);
        Assert.Equal(1, activations);
    }

    [Fact]
    public async Task InitializeAsync_CorruptSettingsNeverActivates()
    {
        var activations = 0;
        var coordinator = new StartupCoordinator(
            new StartupJournal(),
            new StartupBackend(),
            (_, _) =>
            {
                activations++;
                return Task.CompletedTask;
            });

        var result = await coordinator.InitializeAsync(
            new SettingsLoadResult(SettingsLoadState.Corrupt, new PowerModeSettings(), Error: "bad JSON"));

        Assert.True(result.ActivationDeferred);
        Assert.Contains("settings", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, activations);
    }

    [Fact]
    public async Task ResumeAfterRecoveryAsync_ActivatesExactlyOnceAfterJournalResolves()
    {
        var journal = new StartupJournal { Current = Record(LastOperationStatus.Applying) };
        var activations = 0;
        var coordinator = new StartupCoordinator(
            journal,
            new StartupBackend(),
            (_, _) =>
            {
                activations++;
                return Task.CompletedTask;
            });

        await coordinator.InitializeAsync(LoadedSettings());
        journal.Current = Record(LastOperationStatus.Verified);
        await coordinator.ResumeAfterRecoveryAsync();
        await coordinator.ResumeAfterRecoveryAsync();

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
        new PowerModeState(
            Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
            "Balanced",
            PowerModePreset.Balanced,
            PowerSourceKind.Ac,
            null,
            100,
            100,
            5,
            5,
            null,
            null,
            100,
            100,
            0,
            0,
            0,
            0,
            0,
            0,
            false),
        status);

    private sealed class StartupJournal : ILastOperationStore
    {
        public LastOperationRecord? Current { get; set; }

        public Task<LastOperationReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LastOperationReadResult(Current, null));

        public Task<LastOperationWriteResult> WriteAsync(
            LastOperationRecord record,
            Guid? expectedOperationId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LastOperationWriteResult(true, false, null));
    }

    private sealed class StartupBackend : IPowerModeBackend
    {
        public int ReadCount { get; private set; }

        public Task<PowerModeStateResult> ReadStateAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(new PowerModeStateResult(
                Record(LastOperationStatus.Verified).BeforeState,
                new BackendOperationResult(
                    operationId,
                    "status",
                    null,
                    DateTimeOffset.UtcNow,
                    TimeSpan.Zero,
                    BackendOperationOutcome.Succeeded,
                    null,
                    Record(LastOperationStatus.Verified).BeforeState,
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
