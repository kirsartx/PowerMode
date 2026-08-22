using System.Text.Json;

namespace PowerModeWinUI;

internal sealed record PowerModeStateResult(
    PowerModeState? State,
    BackendOperationResult Operation);

internal interface IPowerModeBackend
{
    Task<PowerModeStateResult> ReadStateAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<BackendOperationResult> ApplyAsync(
        Guid operationId,
        PowerModeTarget target,
        int? cpuMaximumPercent = null,
        bool disableWifi = false,
        CancellationToken cancellationToken = default);

    Task<BackendOperationResult> RestoreAsync(
        Guid operationId,
        PowerModeState snapshot,
        CancellationToken cancellationToken = default);
}

internal sealed class PowerModeBackend : IPowerModeBackend
{
    private const int MaxDiagnosticLength = 8 * 1024;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IProcessRunner _processRunner;
    private readonly string _enginePath;

    public PowerModeBackend(
        IProcessRunner processRunner,
        string enginePath)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _enginePath = enginePath ?? throw new ArgumentNullException(nameof(enginePath));
    }

    public async Task<PowerModeStateResult> ReadStateAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var execution = await RunEngineAsync(
            operationId,
            action: "status",
            arguments: ["-Mode", "status"],
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        var operation = ParseOperation(
            execution,
            operationId,
            expectedAction: "status",
            expectedTargetKey: null);
        return new(
            operation.Outcome is BackendOperationOutcome.InvalidResponse or
                BackendOperationOutcome.ContractIncompatible or
                BackendOperationOutcome.TimedOut or
                BackendOperationOutcome.Cancelled
                ? null
                : operation.AfterState,
            operation);
    }

    public async Task<BackendOperationResult> ApplyAsync(
        Guid operationId,
        PowerModeTarget target,
        int? cpuMaximumPercent = null,
        bool disableWifi = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var arguments = new List<string> { "-Mode" };
        if (target.Preset.HasValue)
        {
            arguments.Add(target.Preset.Value.ToString().ToLowerInvariant());
        }
        else
        {
            arguments.Add("custom");
            arguments.Add("-CustomProfileBase64");
            arguments.Add(Encode(target.CustomProfile!));
        }

        arguments.Add("-OutputFormat");
        arguments.Add("Json");
        arguments.Add("-OperationId");
        arguments.Add(operationId.ToString());
        if (cpuMaximumPercent.HasValue)
        {
            arguments.Add("-CpuMaximumPercent");
            arguments.Add(cpuMaximumPercent.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }
        if (disableWifi)
        {
            arguments.Add("-DisableWifi");
        }

        var execution = await RunEngineAsync(
            operationId,
            action: "apply",
            arguments,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        return ParseOperation(
            execution,
            operationId,
            expectedAction: "apply",
            expectedTargetKey: target.Key);
    }

    public async Task<BackendOperationResult> RestoreAsync(
        Guid operationId,
        PowerModeState snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var execution = await RunEngineAsync(
            operationId,
            action: "restore",
            arguments:
            [
                "-Mode", "restore",
                "-RestoreSnapshotBase64", Encode(snapshot)
            ],
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        return ParseOperation(
            execution,
            operationId,
            expectedAction: "restore",
            expectedTargetKey: null);
    }

    private async Task<ProcessExecutionResult> RunEngineAsync(
        Guid operationId,
        string action,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var fullArguments = new List<string>
        {
            "-NoLogo",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            _enginePath
        };
        fullArguments.AddRange(arguments);
        if (!fullArguments.Contains("-OutputFormat", StringComparer.OrdinalIgnoreCase))
        {
            fullArguments.Add("-OutputFormat");
            fullArguments.Add("Json");
        }
        if (!fullArguments.Contains("-OperationId", StringComparer.OrdinalIgnoreCase))
        {
            fullArguments.Add("-OperationId");
            fullArguments.Add(operationId.ToString());
        }

        return await _processRunner.RunAsync(
            new ProcessExecutionRequest(
                "powershell.exe",
                fullArguments,
                timeout),
            cancellationToken).ConfigureAwait(false);
    }

    private static BackendOperationResult ParseOperation(
        ProcessExecutionResult execution,
        Guid expectedOperationId,
        string expectedAction,
        string? expectedTargetKey)
    {
        var diagnostics = new BackendProcessDiagnostics(
            Bound(execution.StandardOutput),
            Bound(execution.StandardError),
            BoundNullable(execution.StartError),
            execution.ExitCode,
            execution.TimedOut,
            execution.Cancelled);

        if (execution.Cancelled)
        {
            return BackendOperationResult.ContractFailure(
                    BackendOperationOutcome.Cancelled,
                    "Power engine execution was cancelled.")
                with { ProcessDiagnostics = diagnostics };
        }
        if (execution.TimedOut)
        {
            return BackendOperationResult.ContractFailure(
                    BackendOperationOutcome.TimedOut,
                    "Power engine execution timed out.")
                with { ProcessDiagnostics = diagnostics };
        }
        if (execution.StartError is not null)
        {
            return BackendOperationResult.ContractFailure(
                    BackendOperationOutcome.InvalidResponse,
                    execution.StartError)
                with { ProcessDiagnostics = diagnostics };
        }

        var parsed = PowerModeEngineContract.Parse(execution.StandardOutput)
            with { ProcessDiagnostics = diagnostics };
        if (parsed.Outcome is BackendOperationOutcome.InvalidResponse or
            BackendOperationOutcome.ContractIncompatible)
        {
            return parsed;
        }

        if (parsed.OperationId != expectedOperationId)
        {
            return InvalidIdentity(parsed, diagnostics, "operation ID");
        }
        if (!string.Equals(parsed.Action, expectedAction, StringComparison.OrdinalIgnoreCase))
        {
            return InvalidIdentity(parsed, diagnostics, "action");
        }
        if (!string.Equals(
                parsed.RequestedTargetKey,
                expectedTargetKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return InvalidIdentity(parsed, diagnostics, "requested target");
        }

        return parsed;
    }

    private static BackendOperationResult InvalidIdentity(
        BackendOperationResult parsed,
        BackendProcessDiagnostics diagnostics,
        string field) =>
        BackendOperationResult.ContractFailure(
                BackendOperationOutcome.InvalidResponse,
                $"Engine response {field} does not match the request.")
            with { ProcessDiagnostics = diagnostics };

    private static string Encode<T>(T value) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            value,
            SnapshotJsonOptions));

    private static string Bound(string value) =>
        value.Length <= MaxDiagnosticLength
            ? value
            : value[..MaxDiagnosticLength];

    private static string? BoundNullable(string? value) =>
        value is null ? null : Bound(value);
}
