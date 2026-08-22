namespace PowerModeWinUI;

internal enum PowerModePreset
{
    Remote,
    Saver,
    Balanced,
    High
}

internal enum PowerSourceKind
{
    Unknown,
    Ac,
    Battery,
    Charging,
    Full
}

internal enum BackendOperationOutcome
{
    Succeeded,
    Partial,
    Failed,
    TimedOut,
    Cancelled,
    InvalidResponse,
    ContractIncompatible
}

internal sealed record CustomPowerProfileSnapshot(
    string Name,
    int CpuMaximumAcPercent,
    int CpuMaximumDcPercent,
    int CpuMinimumPercent,
    int BrightnessAcPercent,
    int BrightnessDcPercent,
    int DisplayTimeoutAcSeconds,
    int DisplayTimeoutDcSeconds,
    bool DisableBoost)
{
    public static CustomPowerProfileSnapshot FromSettings(
        CustomPowerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new(
            profile.Name,
            profile.CpuMax,
            profile.UseSeparateBatteryValues
                ? profile.BatteryCpuMax
                : profile.CpuMax,
            profile.CpuMin,
            profile.Brightness,
            profile.UseSeparateBatteryValues
                ? profile.BatteryBrightness
                : profile.Brightness,
            profile.DisplayOffSeconds,
            profile.UseSeparateBatteryValues
                ? profile.BatteryDisplayOffSeconds
                : profile.DisplayOffSeconds,
            profile.DisableBoost);
    }
}

internal sealed record PowerModeTarget
{
    public string Key { get; }
    public PowerModePreset? Preset { get; }
    public CustomPowerProfileSnapshot? CustomProfile { get; }

    private PowerModeTarget(
        string key,
        PowerModePreset? preset,
        CustomPowerProfileSnapshot? customProfile)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            (preset.HasValue == (customProfile is not null)))
        {
            throw new ArgumentException(
                "A target must contain exactly one preset or custom profile.");
        }

        Key = key;
        Preset = preset;
        CustomProfile = customProfile;
    }

    public static PowerModeTarget ForPreset(PowerModePreset preset)
    {
        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        return new(preset.ToString().ToLowerInvariant(), preset, null);
    }

    public static PowerModeTarget ForCustom(
        CustomPowerProfileSnapshot profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException(
                "Custom profile name is required.",
                nameof(profile));
        }

        return new($"custom:{profile.Name}", null, profile);
    }
}

internal sealed record PowerModeState(
    Guid? ActiveSchemeId,
    string? ActiveSchemeName,
    PowerModePreset? DetectedMode,
    PowerSourceKind PowerSource,
    double? DiscreteGpuPowerWatts,
    int? CpuMaximumAcPercent,
    int? CpuMaximumDcPercent,
    int? CpuMinimumAcPercent,
    int? CpuMinimumDcPercent,
    int? ProcessorBoostModeAc,
    int? ProcessorBoostModeDc,
    int? BrightnessAcPercent,
    int? BrightnessDcPercent,
    int? DisplayTimeoutAcSeconds,
    int? DisplayTimeoutDcSeconds,
    int? SleepTimeoutAcSeconds,
    int? SleepTimeoutDcSeconds,
    int? HibernateTimeoutAcSeconds,
    int? HibernateTimeoutDcSeconds,
    bool? WifiDisabled);

internal enum PowerModeStateField
{
    ActiveSchemeId,
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
    WifiDisabled
}

internal sealed record BackendStepResult(
    string Name,
    bool Critical,
    int? ExitCode,
    bool TimedOut,
    string? Error);

internal sealed record VerificationExpectation(
    PowerModeStateField Field,
    string ExpectedValue,
    bool Critical);

internal sealed record BackendProcessDiagnostics(
    string StandardOutput,
    string StandardError,
    string? StartError,
    int? ProcessExitCode,
    bool TimedOut,
    bool Cancelled);

internal sealed record BackendOperationResult(
    Guid OperationId,
    string Action,
    string? RequestedTargetKey,
    DateTimeOffset? StartedAtUtc,
    TimeSpan Duration,
    BackendOperationOutcome Outcome,
    PowerModeState? BeforeState,
    PowerModeState? AfterState,
    IReadOnlyList<BackendStepResult> Steps,
    IReadOnlyList<VerificationExpectation> Expectations,
    string? ContractError,
    BackendProcessDiagnostics? ProcessDiagnostics = null)
{
    public static BackendOperationResult ContractFailure(
        BackendOperationOutcome outcome,
        string error) =>
        new(
            Guid.Empty,
            "contract",
            null,
            null,
            TimeSpan.Zero,
            outcome,
            null,
            null,
            Array.Empty<BackendStepResult>(),
            Array.Empty<VerificationExpectation>(),
            error);
}
