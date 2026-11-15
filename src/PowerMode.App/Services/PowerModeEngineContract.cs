using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerModeWinUI;

internal static class PowerModeEngineContract
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    public static BackendOperationResult Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var dto = document.RootElement.Deserialize<EngineResultDto>(JsonOptions)
                ?? throw new JsonException("Engine result was null.");
            if (!dto.SchemaVersion.HasValue)
            {
                throw new JsonException(
                    "Engine result is missing schemaVersion.");
            }

            if (dto.SchemaVersion.Value != SupportedSchemaVersion)
            {
                return BackendOperationResult.ContractFailure(
                    BackendOperationOutcome.ContractIncompatible,
                    $"Unsupported engine schema version {dto.SchemaVersion}.");
            }

            return dto.ToDomain();
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or OverflowException)
        {
            return BackendOperationResult.ContractFailure(
                BackendOperationOutcome.InvalidResponse,
                exception.Message);
        }
    }

    private sealed record EngineResultDto
    {
        public int? SchemaVersion { get; init; }
        public Guid OperationId { get; init; }
        public string? Action { get; init; }

        [JsonPropertyName("requestedMode")]
        public string? RequestedTargetKey { get; init; }

        public DateTimeOffset? StartedAtUtc { get; init; }
        public long? DurationMs { get; init; }
        public BackendOperationOutcome? Outcome { get; init; }
        public EngineStateDto? BeforeState { get; init; }
        public EngineStateDto? AfterState { get; init; }
        public List<EngineStepDto?>? Steps { get; init; }
        public List<EngineExpectationDto?>? Expectations { get; init; }

        public BackendOperationResult ToDomain()
        {
            if (OperationId == Guid.Empty ||
                string.IsNullOrWhiteSpace(Action) ||
                !StartedAtUtc.HasValue ||
                !DurationMs.HasValue ||
                DurationMs < 0 ||
                !Outcome.HasValue ||
                Steps is null ||
                Expectations is null)
            {
                throw new JsonException(
                    "Engine result is missing required fields.");
            }

            var steps = Steps.Select(step =>
                (step ?? throw new JsonException(
                    "Engine result contains a null step.")).ToDomain()).ToArray();
            var expectations = Expectations.Select(expectation =>
                (expectation ?? throw new JsonException(
                    "Engine result contains a null verification expectation.")).ToDomain())
                .ToArray();

            return new(
                OperationId,
                Action,
                RequestedTargetKey,
                StartedAtUtc,
                TimeSpan.FromMilliseconds(DurationMs.Value),
                Outcome.Value,
                BeforeState?.ToDomain(),
                AfterState?.ToDomain(),
                steps,
                expectations,
                null);
        }
    }

    private sealed record EngineStateDto
    {
        public Guid? ActiveSchemeId { get; init; }
        public string? ActiveSchemeName { get; init; }
        public PowerModePreset? DetectedMode { get; init; }
        public PowerSourceKind? PowerSource { get; init; }
        public double? DiscreteGpuPowerWatts { get; init; }
        public int? CpuMaximumAcPercent { get; init; }
        public int? CpuMaximumDcPercent { get; init; }
        public int? CpuMinimumAcPercent { get; init; }
        public int? CpuMinimumDcPercent { get; init; }
        public int? ProcessorBoostModeAc { get; init; }
        public int? ProcessorBoostModeDc { get; init; }
        public int? BrightnessAcPercent { get; init; }
        public int? BrightnessDcPercent { get; init; }
        public int? DisplayTimeoutAcSeconds { get; init; }
        public int? DisplayTimeoutDcSeconds { get; init; }
        public int? SleepTimeoutAcSeconds { get; init; }
        public int? SleepTimeoutDcSeconds { get; init; }
        public int? HibernateTimeoutAcSeconds { get; init; }
        public int? HibernateTimeoutDcSeconds { get; init; }
        public bool? WifiDisabled { get; init; }

        public PowerModeState ToDomain()
        {
            if (!PowerSource.HasValue)
                throw new JsonException("Engine state is missing powerSource.");

            return new(
                ActiveSchemeId,
                ActiveSchemeName,
                DetectedMode,
                PowerSource.Value,
                DiscreteGpuPowerWatts,
                CpuMaximumAcPercent,
                CpuMaximumDcPercent,
                CpuMinimumAcPercent,
                CpuMinimumDcPercent,
                ProcessorBoostModeAc,
                ProcessorBoostModeDc,
                BrightnessAcPercent,
                BrightnessDcPercent,
                DisplayTimeoutAcSeconds,
                DisplayTimeoutDcSeconds,
                SleepTimeoutAcSeconds,
                SleepTimeoutDcSeconds,
                HibernateTimeoutAcSeconds,
                HibernateTimeoutDcSeconds,
                WifiDisabled);
        }
    }

    private sealed record EngineStepDto
    {
        public string? Name { get; init; }
        public bool? Critical { get; init; }
        public int? ExitCode { get; init; }
        public bool? TimedOut { get; init; }
        public string? Error { get; init; }

        public BackendStepResult ToDomain()
        {
            if (string.IsNullOrWhiteSpace(Name) ||
                !Critical.HasValue ||
                !TimedOut.HasValue)
            {
                throw new JsonException(
                    "Engine step is missing required fields.");
            }

            return new(Name, Critical.Value, ExitCode, TimedOut.Value, Error);
        }
    }

    private sealed record EngineExpectationDto
    {
        public PowerModeStateField? Field { get; init; }
        public string? ExpectedValue { get; init; }
        public bool? Critical { get; init; }

        public VerificationExpectation ToDomain()
        {
            if (!Field.HasValue ||
                ExpectedValue is null ||
                !Critical.HasValue)
            {
                throw new JsonException(
                    "Verification expectation is missing required fields.");
            }

            return new(Field.Value, ExpectedValue, Critical.Value);
        }
    }
}
