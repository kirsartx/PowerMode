using System.Text;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class PowerModeBackendTests
{
    private const string FixedEnginePath = @"C:\test\PowerMode.Engine.ps1";
    private static readonly Guid FixtureOperationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Locator_ExplicitOverrideHasPrecedence()
    {
        using var directory = new TemporaryDirectory();
        var explicitEngine = System.IO.Path.Combine(
            directory.Path,
            "PowerMode.Engine.ps1");
        File.WriteAllText(explicitEngine, "# test");

        var result = PowerModeEngineLocator.Find(
            System.IO.Path.Combine(directory.Path, "App"),
            explicitEngine);

        Assert.Equal(System.IO.Path.GetFullPath(explicitEngine), result);
    }

    [Fact]
    public void Locator_FindsPortableSiblingAboveApp()
    {
        using var directory = new TemporaryDirectory();
        var appDirectory = System.IO.Path.Combine(directory.Path, "App");
        Directory.CreateDirectory(appDirectory);
        var engine = System.IO.Path.Combine(directory.Path, "PowerMode.Engine.ps1");
        File.WriteAllText(engine, "# test");

        Assert.Equal(
            engine,
            PowerModeEngineLocator.Find(appDirectory));
    }

    [Fact]
    public void Locator_MissingEngineListsAbsoluteCandidates()
    {
        using var directory = new TemporaryDirectory();
        var exception = Assert.Throws<FileNotFoundException>(() =>
            PowerModeEngineLocator.Find(
                System.IO.Path.Combine(directory.Path, "App")));

        Assert.Contains(
            System.IO.Path.GetFullPath(directory.Path),
            exception.Message);
        Assert.Contains("PowerMode.Engine.ps1", exception.Message);
    }

    [Fact]
    public async Task ApplyAsync_PresetUsesJsonAndThirtySecondTimeout()
    {
        var runner = FakeProcessRunner.Returning(
            TestPaths.ReadFixture("engine-status-v1.json"));
        var backend = new PowerModeBackend(runner, FixedEnginePath);

        await backend.ApplyAsync(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            PowerModeTarget.ForPreset(PowerModePreset.Remote),
            cpuMaximumPercent: 32,
            disableWifi: true);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
        Assert.Contains("-OutputFormat", request.Arguments);
        Assert.Contains("Json", request.Arguments);
        Assert.Contains("32", request.Arguments);
        Assert.Contains("-DisableWifi", request.Arguments);
        Assert.Contains(FixedEnginePath, request.Arguments);
    }

    [Fact]
    public async Task ApplyAsync_CustomProfileUsesBase64Payload()
    {
        var runner = FakeProcessRunner.Returning(
            TestPaths.ReadFixture("engine-status-v1.json"));
        var profile = new CustomPowerProfileSnapshot(
            "Quiet", 45, 35, 5, 65, 45, 300, 120, true);

        await new PowerModeBackend(runner, FixedEnginePath).ApplyAsync(
            FixtureOperationId,
            PowerModeTarget.ForCustom(profile));

        var request = Assert.Single(runner.Requests);
        var index = Array.IndexOf(
            request.Arguments.ToArray(),
            "-CustomProfileBase64");
        Assert.True(index >= 0);
        var decoded = Encoding.UTF8.GetString(
            Convert.FromBase64String(request.Arguments[index + 1]));
        Assert.Contains("Quiet", decoded);
    }

    [Fact]
    public async Task RestoreAsync_UsesTwentySecondTimeoutAndSnapshotPayload()
    {
        var runner = FakeProcessRunner.Returning(
            TestPaths.ReadFixture("engine-status-v1.json"));
        var snapshot = new PowerModeState(
            FixtureOperationId,
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

        await new PowerModeBackend(runner, FixedEnginePath).RestoreAsync(
            FixtureOperationId,
            snapshot);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(TimeSpan.FromSeconds(20), request.Timeout);
        Assert.Contains("-RestoreSnapshotBase64", request.Arguments);
    }

    [Fact]
    public async Task ReadStateAsync_TimeoutNeverParsesPartialJson()
    {
        var runner = FakeProcessRunner.TimedOut("{\"schemaVersion\":1");
        var result = await new PowerModeBackend(runner, FixedEnginePath)
            .ReadStateAsync(Guid.NewGuid());

        Assert.Equal(BackendOperationOutcome.TimedOut, result.Operation.Outcome);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task ReadStateAsync_ValidJsonSurvivesNonZeroProcessExit()
    {
        var runner = FakeProcessRunner.Returning(
            TestPaths.ReadFixture("engine-status-v1.json"),
            exitCode: 7,
            standardError: "human warning");
        var result = await new PowerModeBackend(runner, FixedEnginePath)
            .ReadStateAsync(FixtureOperationId);

        Assert.NotNull(result.State);
        Assert.Equal(BackendOperationOutcome.Succeeded, result.Operation.Outcome);
        Assert.Equal(7, result.Operation.ProcessDiagnostics!.ProcessExitCode);
        Assert.Equal("human warning", result.Operation.ProcessDiagnostics.StandardError);
    }

    [Fact]
    public async Task ReadStateAsync_MismatchedOperationIsInvalidResponse()
    {
        var runner = FakeProcessRunner.Returning(
            TestPaths.ReadFixture("engine-status-v1.json"));
        var result = await new PowerModeBackend(runner, FixedEnginePath)
            .ReadStateAsync(Guid.NewGuid());

        Assert.Equal(
            BackendOperationOutcome.InvalidResponse,
            result.Operation.Outcome);
        Assert.Null(result.State);
        Assert.Contains("operation", result.Operation.ContractError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadStateAsync_UnknownSchemaIsContractIncompatible()
    {
        var runner = FakeProcessRunner.Returning(
            TestPaths.ReadFixture("engine-unknown-version.json"));
        var result = await new PowerModeBackend(runner, FixedEnginePath)
            .ReadStateAsync(FixtureOperationId);

        Assert.Equal(
            BackendOperationOutcome.ContractIncompatible,
            result.Operation.Outcome);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task ApplyAsync_CancellationMapsWithoutParsingOutput()
    {
        var runner = FakeProcessRunner.Cancelled("{\"schemaVersion\":1");
        var result = await new PowerModeBackend(runner, FixedEnginePath).ApplyAsync(
            FixtureOperationId,
            PowerModeTarget.ForPreset(PowerModePreset.Remote));

        Assert.Equal(BackendOperationOutcome.Cancelled, result.Outcome);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly ProcessExecutionResult _result;

        private FakeProcessRunner(ProcessExecutionResult result)
        {
            _result = result;
        }

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public static FakeProcessRunner Returning(
            string stdout,
            int? exitCode = 0,
            string standardError = "") =>
            new(new(
                exitCode,
                stdout,
                standardError,
                TimeSpan.FromMilliseconds(25),
                TimedOut: false,
                Cancelled: false,
                StartError: null));

        public static FakeProcessRunner TimedOut(string partialStdout) =>
            new(new(
                null,
                partialStdout,
                "timeout diagnostics",
                TimeSpan.FromSeconds(10),
                TimedOut: true,
                Cancelled: false,
                StartError: null));

        public static FakeProcessRunner Cancelled(string partialStdout) =>
            new(new(
                null,
                partialStdout,
                "cancel diagnostics",
                TimeSpan.FromMilliseconds(10),
                TimedOut: false,
                Cancelled: true,
                StartError: null));

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_result);
        }
    }
}
