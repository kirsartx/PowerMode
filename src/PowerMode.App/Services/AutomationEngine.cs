using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PowerModeWinUI;

[JsonConverter(typeof(JsonStringEnumConverter<RuleConditionType>))]
public enum RuleConditionType
{
    PowerSource,
    BatteryLevel,
    TimeOfDay,
    ProcessRunning,
    UserIdleSeconds,
    ForegroundFullscreen,
    RemoteSession
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleComparison>))]
public enum RuleComparison
{
    Equals,
    NotEquals,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Between,
    OutsideRange,
    AnyOf,
    AllOf,
    NoneOf
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleMatchMode>))]
public enum RuleMatchMode
{
    All,
    Any
}

[JsonConverter(typeof(JsonStringEnumConverter<AutomationPowerSource>))]
public enum AutomationPowerSource
{
    Unknown,
    Ac,
    Battery
}

/// <summary>
/// A serializable condition. Value and SecondaryValue are intentionally strings so the
/// settings UI can edit every condition without type-specific JSON converters.
/// Battery and idle values are numbers, time values use HH:mm, process values are a
/// comma/semicolon separated list, and boolean values use true/false.
/// </summary>
public sealed class RuleCondition
{
    public RuleConditionType Type { get; set; }
    public RuleComparison Comparison { get; set; } = RuleComparison.Equals;
    public string Value { get; set; } = string.Empty;
    public string? SecondaryValue { get; set; }
    public List<DayOfWeek> DaysOfWeek { get; set; } = [];
    public bool Negate { get; set; }
}

public sealed class AutomationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New rule";
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; }
    public string TargetMode { get; set; } = "balanced";
    public RuleMatchMode MatchMode { get; set; } = RuleMatchMode.All;
    public List<RuleCondition> Conditions { get; set; } = [];
}

/// <summary>A point-in-time view of the state used by automation rules.</summary>
public sealed class AutomationSnapshot
{
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public AutomationPowerSource PowerSource { get; set; }
    public int? BatteryPercent { get; set; }
    public bool HasBattery { get; set; }
    public bool IsCharging { get; set; }
    public bool IsBatterySaverOn { get; set; }
    public List<string> RunningProcesses { get; set; } = [];
    public TimeSpan UserIdleTime { get; set; }
    public bool IsForegroundFullscreen { get; set; }
    public string ForegroundProcessName { get; set; } = string.Empty;
    public bool IsRemoteSession { get; set; }
}

public sealed class AutomationDecision
{
    public required Guid RuleId { get; init; }
    public required string RuleName { get; init; }
    public required string TargetMode { get; init; }
    public required string Reason { get; init; }
    public string CurrentMode { get; init; } = string.Empty;
    public bool RequiresSwitch => !string.Equals(CurrentMode, TargetMode, StringComparison.OrdinalIgnoreCase);
    public required AutomationSnapshot Snapshot { get; init; }
}

/// <summary>
/// Captures Windows state and evaluates enabled rules in descending priority order.
/// Rules at the same priority retain their list order.
/// </summary>
public sealed class AutomationEngine
{
    public AutomationSnapshot CaptureSnapshot() => SystemStateProbe.Capture();

    public AutomationDecision? Evaluate(
        IEnumerable<AutomationRule>? rules,
        string? currentMode = null,
        AutomationSnapshot? snapshot = null)
    {
        if (rules is null)
        {
            return null;
        }

        snapshot ??= CaptureSnapshot();
        var orderedRules = rules
            .Select((rule, index) => (Rule: rule, Index: index))
            .Where(item => item.Rule is { IsEnabled: true }
                && !string.IsNullOrWhiteSpace(item.Rule.TargetMode)
                && item.Rule.Conditions is { Count: > 0 })
            .OrderByDescending(item => item.Rule.Priority)
            .ThenBy(item => item.Index);

        foreach (var item in orderedRules)
        {
            if (!Matches(item.Rule, snapshot))
            {
                continue;
            }

            var rule = item.Rule;
            return new AutomationDecision
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                TargetMode = rule.TargetMode.Trim(),
                CurrentMode = currentMode?.Trim() ?? string.Empty,
                Reason = string.IsNullOrWhiteSpace(rule.Description) ? rule.Name : rule.Description,
                Snapshot = snapshot
            };
        }

        return null;
    }

    public bool Matches(AutomationRule? rule, AutomationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (rule is null || !rule.IsEnabled || rule.Conditions is not { Count: > 0 })
        {
            return false;
        }

        return rule.MatchMode == RuleMatchMode.Any
            ? rule.Conditions.Any(condition => Matches(condition, snapshot))
            : rule.Conditions.All(condition => Matches(condition, snapshot));
    }

    public bool Matches(RuleCondition? condition, AutomationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (condition is null)
        {
            return false;
        }

        // Negation applies to a valid condition that evaluated false. It must not turn a
        // malformed persisted value into a matching rule.
        if (!IsConditionValid(condition))
        {
            return false;
        }

        var matched = condition.Type switch
        {
            RuleConditionType.PowerSource => MatchPowerSource(condition, snapshot.PowerSource),
            RuleConditionType.BatteryLevel => MatchNumber(condition, snapshot.BatteryPercent),
            RuleConditionType.TimeOfDay => MatchTime(condition, snapshot.CapturedAt.LocalDateTime),
            RuleConditionType.ProcessRunning => MatchProcesses(condition, snapshot.RunningProcesses),
            RuleConditionType.UserIdleSeconds => MatchNumber(condition, snapshot.UserIdleTime.TotalSeconds),
            RuleConditionType.ForegroundFullscreen => MatchBoolean(condition, snapshot.IsForegroundFullscreen),
            RuleConditionType.RemoteSession => MatchBoolean(condition, snapshot.IsRemoteSession),
            _ => false
        };

        return condition.Negate ? !matched : matched;
    }

    private static bool IsConditionValid(RuleCondition condition) => condition.Type switch
    {
        RuleConditionType.PowerSource => TryParsePowerSource(condition.Value, out _),
        RuleConditionType.BatteryLevel or RuleConditionType.UserIdleSeconds =>
            TryParseNumber(condition.Value, out _)
            && (condition.Comparison is not (RuleComparison.Between or RuleComparison.OutsideRange)
                || TryParseNumber(condition.SecondaryValue, out _)),
        RuleConditionType.TimeOfDay =>
            TryParseTime(condition.Value, out _)
            && (condition.Comparison is not (RuleComparison.Between or RuleComparison.OutsideRange)
                || TryParseTime(condition.SecondaryValue, out _)),
        RuleConditionType.ProcessRunning => SplitProcessNames(condition.Value).Count > 0,
        RuleConditionType.ForegroundFullscreen or RuleConditionType.RemoteSession =>
            string.IsNullOrWhiteSpace(condition.Value) || bool.TryParse(condition.Value, out _),
        _ => false
    };

    /// <summary>Creates safe defaults equivalent to the original automatic switching behavior.</summary>
    public static List<AutomationRule> CreateDefaultRules(
        string? remoteProcesses = "Hermes,uu,SunloginClient,ToDesk",
        string? performanceProcesses = "steam,GameBar,obs64")
    {
        var rules = new List<AutomationRule>();
        if (!string.IsNullOrWhiteSpace(remoteProcesses))
        {
            rules.Add(new AutomationRule
            {
                Name = "Remote application",
                Description = "A remote-control application is running",
                Priority = 400,
                TargetMode = "remote",
                Conditions =
                [
                    new RuleCondition
                    {
                        Type = RuleConditionType.ProcessRunning,
                        Comparison = RuleComparison.AnyOf,
                        Value = remoteProcesses
                    }
                ]
            });
        }

        if (!string.IsNullOrWhiteSpace(performanceProcesses))
        {
            rules.Add(new AutomationRule
            {
                Name = "Performance application",
                Description = "A performance application is running",
                Priority = 300,
                TargetMode = "high",
                Conditions =
                [
                    new RuleCondition
                    {
                        Type = RuleConditionType.ProcessRunning,
                        Comparison = RuleComparison.AnyOf,
                        Value = performanceProcesses
                    }
                ]
            });
        }

        rules.Add(new AutomationRule
        {
            Name = "On battery",
            Description = "The computer is using battery power",
            Priority = 100,
            TargetMode = "saver",
            Conditions =
            [
                new RuleCondition
                {
                    Type = RuleConditionType.PowerSource,
                    Value = nameof(AutomationPowerSource.Battery)
                }
            ]
        });
        rules.Add(new AutomationRule
        {
            Name = "Plugged in",
            Description = "The computer is connected to AC power",
            TargetMode = "balanced",
            Conditions =
            [
                new RuleCondition
                {
                    Type = RuleConditionType.PowerSource,
                    Value = nameof(AutomationPowerSource.Ac)
                }
            ]
        });
        return rules;
    }

    private static bool MatchPowerSource(RuleCondition condition, AutomationPowerSource actual)
    {
        if (!TryParsePowerSource(condition.Value, out var expected))
        {
            return false;
        }

        return condition.Comparison switch
        {
            RuleComparison.NotEquals or RuleComparison.NoneOf => actual != expected,
            _ => actual == expected
        };
    }

    private static bool TryParsePowerSource(string? value, out AutomationPowerSource source)
    {
        if (Enum.TryParse(value, true, out source))
        {
            return true;
        }

        source = value?.Trim().ToLowerInvariant() switch
        {
            "mains" or "plugged" or "pluggedin" or "插电" => AutomationPowerSource.Ac,
            "dc" or "battery power" or "电池" => AutomationPowerSource.Battery,
            _ => AutomationPowerSource.Unknown
        };
        return source != AutomationPowerSource.Unknown
            || string.Equals(value?.Trim(), "unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchNumber(RuleCondition condition, double? actual)
    {
        if (actual is null || !TryParseNumber(condition.Value, out var first))
        {
            return false;
        }

        var value = actual.Value;
        return condition.Comparison switch
        {
            RuleComparison.NotEquals => !NearlyEqual(value, first),
            RuleComparison.LessThan => value < first,
            RuleComparison.LessThanOrEqual => value <= first,
            RuleComparison.GreaterThan => value > first,
            RuleComparison.GreaterThanOrEqual => value >= first,
            RuleComparison.Between => TryParseNumber(condition.SecondaryValue, out var second)
                && IsBetween(value, first, second),
            RuleComparison.OutsideRange => TryParseNumber(condition.SecondaryValue, out var second)
                && !IsBetween(value, first, second),
            _ => NearlyEqual(value, first)
        };
    }

    private static bool TryParseNumber(string? value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
        || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < 0.0001;

    private static bool IsBetween(double value, double first, double second)
    {
        var minimum = Math.Min(first, second);
        var maximum = Math.Max(first, second);
        return value >= minimum && value <= maximum;
    }

    private static bool MatchTime(RuleCondition condition, DateTime localTime)
    {
        if (!TryParseTime(condition.Value, out var first))
        {
            return false;
        }

        var now = TimeOnly.FromDateTime(localTime);
        var hasSecond = TryParseTime(condition.SecondaryValue, out var second);
        if (!MatchScheduledDay(condition, localTime, first, hasSecond ? second : null))
        {
            return false;
        }

        return condition.Comparison switch
        {
            RuleComparison.NotEquals => !SameMinute(now, first),
            RuleComparison.LessThan => now < first,
            RuleComparison.LessThanOrEqual => now <= first,
            RuleComparison.GreaterThan => now > first,
            RuleComparison.GreaterThanOrEqual => now >= first,
            RuleComparison.Between when hasSecond => IsInTimeRange(now, first, second),
            RuleComparison.OutsideRange when hasSecond => !IsInTimeRange(now, first, second),
            RuleComparison.Equals when hasSecond => IsInTimeRange(now, first, second),
            _ => SameMinute(now, first)
        };
    }

    private static bool MatchScheduledDay(
        RuleCondition condition,
        DateTime localTime,
        TimeOnly start,
        TimeOnly? end)
    {
        if (condition.DaysOfWeek is not { Count: > 0 })
        {
            return true;
        }

        var scheduledDay = localTime.DayOfWeek;
        var now = TimeOnly.FromDateTime(localTime);
        if (end is not null && start > end.Value && now <= end.Value)
        {
            scheduledDay = localTime.AddDays(-1).DayOfWeek;
        }

        return condition.DaysOfWeek.Contains(scheduledDay);
    }

    private static bool TryParseTime(string? value, out TimeOnly result)
    {
        if (TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out result))
        {
            return true;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span)
            && span >= TimeSpan.Zero
            && span < TimeSpan.FromDays(1))
        {
            result = TimeOnly.FromTimeSpan(span);
            return true;
        }

        result = default;
        return false;
    }

    private static bool SameMinute(TimeOnly left, TimeOnly right) =>
        left.Hour == right.Hour && left.Minute == right.Minute;

    private static bool IsInTimeRange(TimeOnly value, TimeOnly start, TimeOnly end) =>
        start <= end ? value >= start && value <= end : value >= start || value <= end;

    private static bool MatchProcesses(RuleCondition condition, IReadOnlyCollection<string>? runningProcesses)
    {
        var patterns = SplitProcessNames(condition.Value);
        if (patterns.Count == 0)
        {
            return false;
        }

        var running = runningProcesses ?? [];
        bool IsRunning(string pattern) => running.Any(name => ProcessNameMatches(pattern, name));

        return condition.Comparison switch
        {
            RuleComparison.AllOf => patterns.All(IsRunning),
            RuleComparison.NoneOf or RuleComparison.NotEquals => patterns.All(pattern => !IsRunning(pattern)),
            _ => patterns.Any(IsRunning)
        };
    }

    private static List<string> SplitProcessNames(string? value) =>
        (value ?? string.Empty)
            .Split([',', ';', '|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeProcessName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizeProcessName(string value)
    {
        var trimmed = value.Trim().Trim('"');
        var fileName = Path.GetFileName(trimmed);
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }

    private static bool ProcessNameMatches(string pattern, string processName)
    {
        var normalized = NormalizeProcessName(processName);
        return pattern.IndexOfAny(['*', '?']) >= 0
            ? FileSystemName.MatchesSimpleExpression(pattern, normalized, ignoreCase: true)
            : string.Equals(pattern, normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchBoolean(RuleCondition condition, bool actual)
    {
        var expected = true;
        if (!string.IsNullOrWhiteSpace(condition.Value)
            && !bool.TryParse(condition.Value, out expected))
        {
            // A malformed persisted value must not unexpectedly activate a rule.
            return false;
        }

        return condition.Comparison is RuleComparison.NotEquals or RuleComparison.NoneOf
            ? actual != expected
            : actual == expected;
    }
}

public sealed class SwitchHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string OperationKind { get; set; } = "mode-switch";
    public Guid? RelatedOperationId { get; set; }
    public bool IsUndo { get; set; }
    public string PreviousMode { get; set; } = string.Empty;
    public string TargetMode { get; set; } = string.Empty;
    public string Trigger { get; set; } = "manual";
    public string Reason { get; set; } = string.Empty;
    public Guid? RuleId { get; set; }
    public string? RuleName { get; set; }
    public bool Succeeded { get; set; }
    public long DurationMilliseconds { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Thread-safe JSONL switch history. GetRecentAsync returns newest entries first.
/// Malformed lines are ignored so one interrupted write cannot hide older history.
/// </summary>
public sealed class HistoryStore : IRecoveryHistory
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerMode");
    public static string DefaultFilePath { get; } = Path.Combine(DirectoryPath, "switch-history.jsonl");
    public static HistoryStore Default { get; } = new();

    private readonly SemaphoreSlim _gate;
    private readonly Func<string, string, CancellationToken, Task> _appendTextAsync;

    public HistoryStore(string? filePath = null)
        : this(filePath, AppendTextAsync)
    {
    }

    internal HistoryStore(
        string? filePath,
        Func<string, string, CancellationToken, Task> appendTextAsync)
    {
        FilePath = Path.GetFullPath(string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath);
        _gate = FileGates.GetOrAdd(FilePath, static _ => new SemaphoreSlim(1, 1));
        _appendTextAsync = appendTextAsync
            ?? throw new ArgumentNullException(nameof(appendTextAsync));
    }

    public string FilePath { get; }

    public async Task RecordAsync(SwitchHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Id == Guid.Empty)
        {
            entry.Id = Guid.NewGuid();
        }
        if (entry.Timestamp == default)
        {
            entry.Timestamp = DateTimeOffset.Now;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (await ContainsIdUnlockedAsync(entry.Id, cancellationToken).ConfigureAwait(false))
                return;
            var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            await _appendTextAsync(FilePath, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ContainsIdUnlockedAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
            return false;

        await using var stream = new FileStream(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var existing = JsonSerializer.Deserialize<SwitchHistoryEntry>(line, JsonOptions);
                if (existing?.Id == id)
                    return true;
            }
            catch (JsonException)
            {
                // Match normal history reads: one malformed line must not block later entries.
            }
        }
        return false;
    }

    private static Task AppendTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken) =>
        File.AppendAllTextAsync(path, contents, Utf8NoBom, cancellationToken);

    public async Task<IReadOnlyList<SwitchHistoryEntry>> GetRecentAsync(
        int maximumCount = 200,
        CancellationToken cancellationToken = default)
    {
        maximumCount = Math.Clamp(maximumCount, 1, 10_000);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadRecentUnlockedAsync(maximumCount, cancellationToken).ConfigureAwait(false);
            entries.Reverse();
            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TrimAsync(int maximumEntries = 2_000, CancellationToken cancellationToken = default)
    {
        maximumEntries = Math.Clamp(maximumEntries, 1, 100_000);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var entries = await ReadRecentUnlockedAsync(maximumEntries, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporaryPath = FilePath + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous))
                await using (var writer = new StreamWriter(stream, Utf8NoBom))
                {
                    foreach (var entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(JsonSerializer.Serialize(entry, JsonOptions).AsMemory(), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<SwitchHistoryEntry>> ReadRecentUnlockedAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        var queue = new Queue<SwitchHistoryEntry>(maximumCount);
        await using var stream = new FileStream(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<SwitchHistoryEntry>(line, JsonOptions);
                if (entry is null)
                {
                    continue;
                }

                if (queue.Count == maximumCount)
                {
                    queue.Dequeue();
                }
                queue.Enqueue(entry);
            }
            catch (JsonException)
            {
                // Keep reading: JSONL deliberately isolates a damaged record.
            }
        }

        return [.. queue];
    }
}

internal static class SystemStateProbe
{
    private const int SmRemoteSession = 0x1000;
    private const uint MonitorDefaultToNearest = 2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int WtsClientProtocolType = 16;

    public static AutomationSnapshot Capture()
    {
        var processes = CaptureProcesses();
        var foreground = Native.GetForegroundWindow();
        var foregroundProcess = GetProcessName(foreground);
        var powerSource = AutomationPowerSource.Unknown;
        int? batteryPercent = null;
        var hasBattery = false;
        var isCharging = false;
        var isBatterySaverOn = false;

        if (Native.GetSystemPowerStatus(out var status))
        {
            powerSource = status.ACLineStatus switch
            {
                0 => AutomationPowerSource.Battery,
                1 => AutomationPowerSource.Ac,
                _ => AutomationPowerSource.Unknown
            };
            hasBattery = status.BatteryFlag != byte.MaxValue && (status.BatteryFlag & 128) == 0;
            batteryPercent = hasBattery && status.BatteryLifePercent <= 100
                ? status.BatteryLifePercent
                : null;
            isCharging = hasBattery && (status.BatteryFlag & 8) != 0;
            isBatterySaverOn = status.SystemStatusFlag == 1;
        }

        return new AutomationSnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            PowerSource = powerSource,
            BatteryPercent = batteryPercent,
            HasBattery = hasBattery,
            IsCharging = isCharging,
            IsBatterySaverOn = isBatterySaverOn,
            RunningProcesses = processes,
            UserIdleTime = GetUserIdleTime(),
            IsForegroundFullscreen = IsFullscreen(foreground),
            ForegroundProcessName = foregroundProcess,
            IsRemoteSession = IsRemoteSession()
        };
    }

    private static List<string> CaptureProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(process.ProcessName))
                    {
                        names.Add(process.ProcessName);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        return [.. names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string GetProcessName(nint window)
    {
        if (window == nint.Zero)
        {
            return string.Empty;
        }

        Native.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static TimeSpan GetUserIdleTime()
    {
        var input = new Native.LastInputInfo { Size = (uint)Marshal.SizeOf<Native.LastInputInfo>() };
        if (!Native.GetLastInputInfo(ref input))
        {
            return TimeSpan.Zero;
        }

        var elapsed = unchecked(Native.GetTickCount() - input.Time);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    private static bool IsFullscreen(nint window)
    {
        if (window == nint.Zero
            || window == Native.GetDesktopWindow()
            || window == Native.GetShellWindow()
            || !Native.IsWindowVisible(window)
            || Native.IsIconic(window))
        {
            return false;
        }

        var monitor = Native.MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            return false;
        }

        var monitorInfo = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        Native.Rect windowRect;
        if (Native.DwmGetWindowAttribute(
                window,
                DwmwaExtendedFrameBounds,
                out windowRect,
                Marshal.SizeOf<Native.Rect>()) != 0
            && !Native.GetWindowRect(window, out windowRect))
        {
            return false;
        }

        const int tolerance = 2;
        var screen = monitorInfo.Monitor;
        return windowRect.Left <= screen.Left + tolerance
            && windowRect.Top <= screen.Top + tolerance
            && windowRect.Right >= screen.Right - tolerance
            && windowRect.Bottom >= screen.Bottom - tolerance;
    }

    private static bool IsRemoteSession()
    {
        if (Native.GetSystemMetrics(SmRemoteSession) != 0
            || (Environment.GetEnvironmentVariable("SESSIONNAME")?.StartsWith(
                "RDP-",
                StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return true;
        }

        nint buffer = nint.Zero;
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            var sessionId = unchecked((uint)currentProcess.SessionId);
            if (!Native.WTSQuerySessionInformation(
                    nint.Zero,
                    sessionId,
                    WtsClientProtocolType,
                    out buffer,
                    out var bytesReturned)
                || buffer == nint.Zero
                || bytesReturned < sizeof(short))
            {
                return false;
            }

            return Marshal.ReadInt16(buffer) == 2;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                Native.WTSFreeMemory(buffer);
            }
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            public uint Size;
            public Rect Monitor;
            public Rect WorkArea;
            public uint Flags;
        }

        [DllImport("kernel32.dll")]
        internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        [DllImport("user32.dll")]
        internal static extern bool GetLastInputInfo(ref LastInputInfo input);

        [DllImport("kernel32.dll")]
        internal static extern uint GetTickCount();

        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern nint GetDesktopWindow();

        [DllImport("user32.dll")]
        internal static extern nint GetShellWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(nint window);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out Rect rect);

        [DllImport("user32.dll")]
        internal static extern nint MonitorFromWindow(nint window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(
            nint window,
            int attribute,
            out Rect value,
            int size);

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformation(
            nint server,
            uint sessionId,
            int informationClass,
            out nint buffer,
            out uint bytesReturned);

        [DllImport("wtsapi32.dll")]
        internal static extern void WTSFreeMemory(nint memory);
    }
}
