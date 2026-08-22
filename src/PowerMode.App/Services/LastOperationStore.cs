using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerModeWinUI;

internal sealed record LastOperationReadResult(
    LastOperationRecord? Record,
    string? Error)
{
    public bool Succeeded => Error is null;
}

internal sealed record LastOperationWriteResult(
    bool Succeeded,
    bool Conflict,
    string? Error);

internal interface ILastOperationStore
{
    Task<LastOperationReadResult> ReadAsync(
        CancellationToken cancellationToken = default);

    Task<LastOperationWriteResult> WriteAsync(
        LastOperationRecord record,
        Guid? expectedOperationId = null,
        CancellationToken cancellationToken = default);
}

internal interface IAtomicFileOperations
{
    Task WriteThroughAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken);

    void Replace(string sourcePath, string destinationPath);
    void MoveNew(string sourcePath, string destinationPath);
    void DeleteIfExists(string path);
}

internal sealed class BclAtomicFileOperations : IAtomicFileOperations
{
    public async Task WriteThroughAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Replace(string sourcePath, string destinationPath) =>
        File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);

    public void MoveNew(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath);

    public void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

internal sealed class LastOperationStore : ILastOperationStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly IAtomicFileOperations _fileOperations;
    private readonly SemaphoreSlim _gate;

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerMode",
        "last-operation.json");

    public LastOperationStore(string? filePath = null)
        : this(filePath, new BclAtomicFileOperations())
    {
    }

    internal LastOperationStore(
        string? filePath,
        IAtomicFileOperations fileOperations)
    {
        FilePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath);
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _gate = Gates.GetOrAdd(FilePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string FilePath { get; }

    public async Task<LastOperationReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LastOperationWriteResult> WriteAsync(
        LastOperationRecord record,
        Guid? expectedOperationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = FilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (expectedOperationId.HasValue || record.Status == LastOperationStatus.Prepared)
            {
                var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
                if (!current.Succeeded)
                    return new(false, false, current.Error);
                if (expectedOperationId.HasValue &&
                    current.Record?.OperationId != expectedOperationId)
                    return new(Succeeded: false, Conflict: true, Error: "Last operation changed.");
                if (!expectedOperationId.HasValue &&
                    current.Record is { } unresolved &&
                    unresolved.OperationId != record.OperationId &&
                    IsRecoveryPending(unresolved.Status))
                {
                    return new(
                        Succeeded: false,
                        Conflict: true,
                        Error: "An unresolved last operation must be recovered first.");
                }
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                LastOperationDto.FromRecord(record),
                JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await _fileOperations.WriteThroughAsync(
                temporaryPath,
                bytes,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(FilePath))
                _fileOperations.Replace(temporaryPath, FilePath);
            else
                _fileOperations.MoveNew(temporaryPath, FilePath);
            return new(true, false, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(false, false, exception.Message);
        }
        finally
        {
            _fileOperations.DeleteIfExists(temporaryPath);
            _gate.Release();
        }
    }

    private async Task<LastOperationReadResult> ReadUnlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
            return new(null, null);

        try
        {
            var bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken)
                .ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<LastOperationDto>(bytes, JsonOptions)
                ?? throw new JsonException("Last operation is empty.");
            if (dto.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException(
                    $"Unsupported last-operation schema version {dto.SchemaVersion}.");
            return new(dto.ToRecord(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or
                ArgumentException or FormatException)
        {
            var message = exception is JsonException
                ? $"Invalid JSON in last-operation journal: {exception.Message}"
                : exception.Message;
            return new(null, message);
        }
        catch (IOException exception)
        {
            return new(null, exception.Message);
        }
    }

    private static bool IsRecoveryPending(LastOperationStatus status) =>
        status is LastOperationStatus.Prepared or
            LastOperationStatus.Applying or
            LastOperationStatus.Uncertain;

    private sealed record LastOperationDto
    {
        public int SchemaVersion { get; init; }
        public Guid OperationId { get; init; }
        public string? Source { get; init; }
        public string? Reason { get; init; }
        public string? Preset { get; init; }
        public CustomPowerProfileSnapshot? CustomProfile { get; init; }
        public int? CpuMaximumPercent { get; init; }
        public bool DisableWifi { get; init; }
        public DateTimeOffset StartedAtUtc { get; init; }
        public PowerModeState? BeforeState { get; init; }
        public LastOperationStatus Status { get; init; }
        public ModeSwitchOutcome? Outcome { get; init; }
        public RollbackResult? Rollback { get; init; }

        public static LastOperationDto FromRecord(LastOperationRecord record) => new()
        {
            SchemaVersion = CurrentSchemaVersion,
            OperationId = record.OperationId,
            Source = record.Source,
            Reason = record.Reason,
            Preset = record.Target.Preset?.ToString(),
            CustomProfile = record.Target.CustomProfile,
            CpuMaximumPercent = record.CpuMaximumPercent,
            DisableWifi = record.DisableWifi,
            StartedAtUtc = record.StartedAtUtc,
            BeforeState = record.BeforeState,
            Status = record.Status,
            Outcome = record.Outcome,
            Rollback = record.Rollback
        };

        public LastOperationRecord ToRecord()
        {
            var target = CustomProfile is not null
                ? PowerModeTarget.ForCustom(CustomProfile)
                : PowerModeTarget.ForPreset(ParsePreset(Preset));
            return new(
                SchemaVersion,
                OperationId,
                Source ?? string.Empty,
                Reason,
                target,
                CpuMaximumPercent,
                DisableWifi,
                StartedAtUtc,
                BeforeState ?? throw new JsonException("Before state is missing."),
                Status,
                Outcome,
                Rollback);
        }

        private static PowerModePreset ParsePreset(string? value) =>
            Enum.TryParse<PowerModePreset>(value, ignoreCase: true, out var preset)
                ? preset
                : throw new JsonException("Last operation preset is invalid.");
    }
}
