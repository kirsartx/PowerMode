namespace PowerModeWinUI;

/// <summary>
/// CPU maximum shared by the native applier, the mode-switch verifier, and the
/// PowerShell engine's Limit-CpuMax. Values below 20 use the preset default;
/// values above 50 clamp to 50. Remote and Saver both accept 20–50.
/// </summary>
internal static class PresetCpuLimit
{
    public const int Minimum = 20;
    public const int Maximum = 50;
    public const int RemoteDefault = 32;
    public const int SaverDefault = 30;

    public static int Normalize(PowerModePreset preset, int? requested)
    {
        if (preset is not (PowerModePreset.Remote or PowerModePreset.Saver))
            return 100;

        var fallback = preset == PowerModePreset.Remote ? RemoteDefault : SaverDefault;
        if (requested is not int value || value < Minimum)
            return fallback;
        return value > Maximum ? Maximum : value;
    }
}
