using System.Text.Json;

namespace PowerModeWinUI;

public sealed class PowerModeSettings
{
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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object FileGate = new();
    public static string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerMode");
    public static string FilePath { get; } = Path.Combine(DirectoryPath, "settings.json");

    public static PowerModeSettings Load()
    {
        try { lock(FileGate)return Normalize(File.Exists(FilePath) ? JsonSerializer.Deserialize<PowerModeSettings>(File.ReadAllText(FilePath), JsonOptions) : null); }
        catch { return new PowerModeSettings(); }
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
}
