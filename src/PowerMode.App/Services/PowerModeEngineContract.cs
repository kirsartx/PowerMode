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
        public PowerModeState? BeforeState { get; init; }
        public PowerModeState? AfterState { get; init; }
        public List<EngineStepDto>? Steps { get; init; }
        public List<EngineExpectationDto>? Expectations { get; init; }

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

            return new(
                OperationId,
                Action,
                RequestedTargetKey,
                StartedAtUtc,
                TimeSpan.FromMilliseconds(DurationMs.Value),
                Outcome.Value,
                BeforeState,
                AfterState,
                Steps.Select(step => step.ToDomain()).ToArray(),
                Expectations.Select(expectation => expectation.ToDomain()).ToArray(),
                null);
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
