using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace PowerModeWinUI;

public sealed class PowerModeSettings
{
    public int SchemaVersion { get; set; } = SettingsStore.CurrentSchemaVersion;
    public ExperienceMode ExperienceMode { get; set; } = ExperienceMode.Simple;
    public bool AutoSwitchEnabled { get; set; }
    public bool RealTimeMonitoringEnabled { get; set; }
    public bool RestorePlanOnExit { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool ApplyLastModeOnStartup { get; set; }
    public bool PreviewManualSwitches { get; set; }
    public string LastMode { get; set; } = "balanced";
    public int MonitorIntervalSeconds { get; set; } = 15;
    public bool TemperatureProtectionEnabled { get; set; }
    public double TemperatureLimitCelsius { get; set; } = 90;
    public double TemperatureRecoveryCelsius { get; set; } = 75;
    public int LowBatteryThreshold { get; set; } = 30;
    public bool OperationHistoryEnabled { get; set; } = true;
    public bool CheckUpdatesOnStartup { get; set; }
    public string UpdateApiUrl { get; set; } = string.Empty;
    public int ConfigurationBackupCount { get; set; } = 10;
    public string RemoteProcesses { get; set; } = "Hermes,uu,SunloginClient,ToDesk";
    public string PerformanceProcesses { get; set; } = "steam,GameBar,obs64";
    public List<CustomPowerProfile> Profiles { get; set; } = [new() { Name = "安静办公", CpuMax = 45, Brightness = 65, DisplayOffSeconds = 300 }];
    public List<AutomationRule> Rules { get; set; } = [];
}

public sealed class CustomPowerProfile
{
    public string Name { get; set; } = "自定义模式";
    public int CpuMax { get; set; } = 50;
    public int CpuMin { get; set; } = 5;
    public int Brightness { get; set; } = 50;
    public int DisplayOffSeconds { get; set; } = 60;
    public bool DisableBoost { get; set; } = true;
    public bool UseSeparateBatteryValues { get; set; }
    public int BatteryCpuMax { get; set; } = 35;
    public int BatteryBrightness { get; set; } = 45;
    public int BatteryDisplayOffSeconds { get; set; } = 120;
    public override string ToString() => Name;
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private static readonly object FileGate = new();
    internal const int CurrentSchemaVersion = 1;
    public static string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerMode");
    public static string FilePath { get; } = Path.Combine(DirectoryPath, "settings.json");

    public static SettingsLoadResult Load(string? path = null)
    {
        return Load(path, TimeProvider.System, new BclSettingsFileSystem());
    }

    internal static SettingsLoadResult Load(
        string? path,
        TimeProvider timeProvider) =>
        Load(path, timeProvider, new BclSettingsFileSystem());

    internal static SettingsLoadResult Load(
        string? path,
        TimeProvider timeProvider,
        ISettingsFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(fileSystem);

        lock (FileGate)
        {
            var source = Path.GetFullPath(path ?? FilePath);
            bool exists;
            try
            {
                exists = fileSystem.Exists(source);
            }
            catch (Exception exception)
            {
                return CorruptResult(
                    new PowerModeSettings(),
                    null,
                    $"Settings existence check failed: {exception.Message}");
            }

            if (!exists)
            {
                return new(SettingsLoadState.FirstRun, new PowerModeSettings());
            }

            byte[] bytes;
            try
            {
                bytes = fileSystem.ReadAllBytes(source);
            }
            catch (Exception exception)
            {
                return CorruptResult(
                    new PowerModeSettings(),
                    null,
                    $"Settings read failed: {exception.Message}");
            }

            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Settings root must be a JSON object.");

                var state = SettingsLoadState.Migrated;
                var schema = default(JsonElement);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(
                            property.Name,
                            "schemaVersion",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        schema = property.Value;
                        break;
                    }
                }

                if (schema.ValueKind != JsonValueKind.Undefined)
                {
                    if (schema.ValueKind != JsonValueKind.Number ||
                        !schema.TryGetInt32(out var schemaVersion))
                        throw new JsonException("Settings schemaVersion is invalid.");
                    if (schemaVersion != CurrentSchemaVersion)
                        throw new InvalidDataException(
                            $"Unsupported settings schema version {schemaVersion}.");
                    state = SettingsLoadState.Loaded;
                }

                var settings = DeserializeStrict(Encoding.UTF8.GetString(bytes));
                return new(state, settings);
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or
                    NotSupportedException or FormatException or
                    OverflowException or ArgumentException)
            {
                return QuarantineCorrupt(
                    source,
                    bytes,
                    timeProvider,
                    fileSystem,
                    exception.Message);
            }
        }
    }
    public static PowerModeSettings LoadStrict(string? path = null)
    {
        lock (FileGate)
        {
            var source = Path.GetFullPath(path ?? FilePath);
            if (!File.Exists(source))
                throw new FileNotFoundException("PowerMode settings file was not found.", source);
            return DeserializeStrict(File.ReadAllText(source));
        }
    }
    public static void Save(PowerModeSettings settings)
    {
        lock(FileGate)
        {
            Directory.CreateDirectory(DirectoryPath);
            var temporary=Path.Combine(DirectoryPath,$"settings.{Guid.NewGuid():N}.tmp");
            try{File.WriteAllText(temporary,JsonSerializer.Serialize(Normalize(settings),JsonOptions));File.Move(temporary,FilePath,true);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
    public static PowerModeSettings Clone(PowerModeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return DeserializeStrict(JsonSerializer.Serialize(Normalize(settings), JsonOptions));
    }
    public static void Export(PowerModeSettings settings, string path) { lock(FileGate)File.WriteAllText(path, JsonSerializer.Serialize(Normalize(settings), JsonOptions)); }
    public static PowerModeSettings Import(string path) { lock(FileGate)return Normalize(JsonSerializer.Deserialize<PowerModeSettings>(File.ReadAllText(path), JsonOptions)); }
    internal static PowerModeSettings DeserializeStrict(string json)
    {
        var settings = JsonSerializer.Deserialize<PowerModeSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("PowerMode settings JSON cannot be null.");
        return Normalize(settings);
    }
    internal static PowerModeSettings Normalize(PowerModeSettings? settings)
    {
        settings ??= new();
        settings.SchemaVersion = CurrentSchemaVersion;
        if (!Enum.IsDefined(settings.ExperienceMode))
            settings.ExperienceMode = ExperienceMode.Simple;
        settings.Profiles ??= []; settings.Rules ??= []; settings.RemoteProcesses ??= string.Empty; settings.PerformanceProcesses ??= string.Empty;
        settings.MonitorIntervalSeconds = Math.Max(10, settings.MonitorIntervalSeconds);
        settings.LowBatteryThreshold = Math.Clamp(settings.LowBatteryThreshold, 5, 95);
        settings.TemperatureLimitCelsius = Math.Clamp(settings.TemperatureLimitCelsius, 50, 110);
        settings.TemperatureRecoveryCelsius = Math.Clamp(settings.TemperatureRecoveryCelsius, 40, 100);
        settings.TemperatureRecoveryCelsius = Math.Min(settings.TemperatureRecoveryCelsius, settings.TemperatureLimitCelsius - 1);
        settings.ConfigurationBackupCount = Math.Clamp(settings.ConfigurationBackupCount, 1, 50);
        var lastMode=settings.LastMode?.Trim().ToLowerInvariant();
        settings.LastMode = lastMode is "remote" or "saver" or "balanced" or "high" ? lastMode : "balanced";
        foreach(var profile in settings.Profiles)
        {
            profile.Name=string.IsNullOrWhiteSpace(profile.Name)?"自定义模式":profile.Name.Trim();
            profile.CpuMax=Math.Clamp(profile.CpuMax,5,100);profile.CpuMin=Math.Clamp(profile.CpuMin,0,profile.CpuMax);
            profile.Brightness=Math.Clamp(profile.Brightness,0,100);profile.DisplayOffSeconds=Math.Clamp(profile.DisplayOffSeconds,0,86400);
            profile.BatteryCpuMax=Math.Clamp(profile.BatteryCpuMax,5,100);profile.BatteryBrightness=Math.Clamp(profile.BatteryBrightness,0,100);profile.BatteryDisplayOffSeconds=Math.Clamp(profile.BatteryDisplayOffSeconds,0,86400);
        }
        foreach(var rule in settings.Rules){if(rule.Id==Guid.Empty)rule.Id=Guid.NewGuid();rule.Name=string.IsNullOrWhiteSpace(rule.Name)?"Automation rule":rule.Name.Trim();var target=rule.TargetMode?.Trim().ToLowerInvariant();rule.TargetMode=target is "remote" or "saver" or "balanced" or "high"?target:"balanced";rule.Conditions??=[];}
        return settings;
    }

    private static SettingsLoadResult QuarantineCorrupt(
        string source,
        byte[] bytes,
        TimeProvider timeProvider,
        ISettingsFileSystem fileSystem,
        string primaryError)
    {
        string? quarantinePath = null;
        string error = $"Settings load failed: {primaryError}";
        try
        {
            var sourceDirectory = Path.GetDirectoryName(source)
                ?? throw new DirectoryNotFoundException(
                    "Settings directory could not be determined.");
            var quarantineDirectory = Path.Combine(sourceDirectory, "quarantine");
            fileSystem.CreateDirectory(quarantineDirectory);
            var timestamp = timeProvider.GetUtcNow().UtcDateTime.ToString(
                "yyyyMMdd'T'HHmmssfff'Z'",
                CultureInfo.InvariantCulture);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            quarantinePath = Path.Combine(
                quarantineDirectory,
                $"settings.{timestamp}.{hash}.json");
            fileSystem.WriteAllBytes(quarantinePath, bytes);
        }
        catch (Exception quarantineException)
        {
            error += $"; quarantine failed: {quarantineException.Message}";
            quarantinePath = null;
        }

        return CorruptResult(
            new PowerModeSettings(),
            quarantinePath,
            error);
    }

    private static SettingsLoadResult CorruptResult(
        PowerModeSettings settings,
        string? quarantinePath,
        string error) =>
        new(SettingsLoadState.Corrupt, settings, quarantinePath, error);
}
