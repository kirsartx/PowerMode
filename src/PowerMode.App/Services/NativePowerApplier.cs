using System.Globalization;

namespace PowerModeWinUI;

/// <summary>
/// Applies the four standard presets through powrprof directly, mirroring the PowerShell
/// engine's JSON apply path value-for-value (same GUIDs, same CPU clamp semantics, same
/// expectation set) so the observable contract is identical to an engine apply — only the
/// ~4s cold start is removed. Custom profiles, snapshots, and WiFi toggles stay on the
/// engine; <see cref="TryApplyPreset"/> returns null for those so the caller falls back.
/// </summary>
internal sealed class NativePowerApplier
{
    private readonly IPowerNativeApi _api;

    public NativePowerApplier(IPowerNativeApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public BackendOperationResult? TryApplyPreset(
        Guid operationId,
        PowerModeTarget target,
        int? cpuMaximumPercent)
    {
        if (target.Preset is not { } preset)
            return null; // custom / snapshot targets stay on the engine

        var scheme = SchemeFor(preset);
        var cpu = CpuFor(preset, cpuMaximumPercent);
        var brightness = preset is PowerModePreset.Balanced or PowerModePreset.High ? 100 : 50;
        int? displayTimeout = preset is PowerModePreset.Remote or PowerModePreset.Saver ? 60 : null;
        var cpuMinimum = preset == PowerModePreset.Saver ? 3 : 5;
        var disableBoost = preset is PowerModePreset.Remote or PowerModePreset.Saver;

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var beforeState = new NativePowerStateReader(_api).TryRead();
        var steps = new List<BackendStepResult>();
        var criticalFailed = false;

        if (!Activate(scheme, steps))
            criticalFailed = true;

        criticalFailed |= WritePair(scheme, NativePowerStateReader.CpuSubgroup,
            NativePowerStateReader.CpuMaximum, cpu, "cpu-maximum", critical: true, steps);
        criticalFailed |= WritePair(scheme, NativePowerStateReader.CpuSubgroup,
            NativePowerStateReader.CpuMinimum, cpuMinimum, "cpu-minimum", critical: true, steps);
        if (disableBoost)
        {
            criticalFailed |= WritePair(scheme, NativePowerStateReader.CpuSubgroup,
                NativePowerStateReader.BoostMode, 0, "processor-boost", critical: true, steps);
        }
        WritePair(scheme, NativePowerStateReader.VideoSubgroup,
            NativePowerStateReader.Brightness, brightness, "brightness", critical: false, steps);
        if (displayTimeout is int timeout)
        {
            criticalFailed |= WritePair(scheme, NativePowerStateReader.VideoSubgroup,
                NativePowerStateReader.DisplayTimeout, timeout, "display-timeout", critical: true, steps);
        }
        criticalFailed |= WritePair(scheme, NativePowerStateReader.SleepSubgroup,
            NativePowerStateReader.SleepTimeout, 0, "sleep-timeout", critical: true, steps);
        criticalFailed |= WritePair(scheme, NativePowerStateReader.SleepSubgroup,
            NativePowerStateReader.HibernateTimeout, 0, "hibernate-timeout", critical: true, steps);

        if (!Activate(scheme, steps))
            criticalFailed = true;

        var afterState = new NativePowerStateReader(_api).TryRead();
        var outcome = criticalFailed
            ? BackendOperationOutcome.Failed
            : BackendOperationOutcome.Succeeded;

        return new BackendOperationResult(
            operationId,
            "apply",
            target.Key,
            startedAt,
            stopwatch.Elapsed,
            outcome,
            beforeState,
            afterState,
            steps,
            BuildExpectations(scheme, cpu, brightness, displayTimeout),
            null);
    }

    private bool Activate(Guid scheme, List<BackendStepResult> steps)
    {
        var ok = _api.TrySetActiveScheme(scheme);
        steps.Add(new("set-active-plan", true, ok ? 0 : 1, false, ok ? null : "PowerSetActiveScheme failed"));
        return ok;
    }

    private bool WritePair(
        Guid scheme,
        Guid subgroup,
        Guid setting,
        int value,
        string name,
        bool critical,
        List<BackendStepResult> steps)
    {
        var ok = _api.TryWriteAcValue(scheme, subgroup, setting, value) &&
            _api.TryWriteDcValue(scheme, subgroup, setting, value);
        steps.Add(new(name, critical, ok ? 0 : 1, false, ok ? null : "native power write failed"));
        return critical && !ok;
    }

    private static Guid SchemeFor(PowerModePreset preset) => preset switch
    {
        PowerModePreset.Remote or PowerModePreset.Saver => NativePowerStateReader.SchemeSaver,
        PowerModePreset.Balanced => NativePowerStateReader.SchemeBalanced,
        _ => NativePowerStateReader.SchemeHigh
    };

    // Mirrors the engine's Limit-CpuMax ($Value < 20 -> default, > 50 -> 50, else value).
    private static int CpuFor(PowerModePreset preset, int? cpuMaximumPercent) => preset switch
    {
        PowerModePreset.Remote => LimitCpuMax(cpuMaximumPercent, 32),
        PowerModePreset.Saver => LimitCpuMax(cpuMaximumPercent, 30),
        _ => 100
    };

    private static int LimitCpuMax(int? value, int fallback) => value switch
    {
        null => fallback,
        { } v when v < 20 => fallback,
        { } v when v > 50 => 50,
        { } v => v
    };

    // Mirrors Add-JsonStandardExpectations; value strings use the same invariant formatting
    // as ModeSwitchCoordinator so IsApplyContractValid's set-equality holds.
    private static IReadOnlyList<VerificationExpectation> BuildExpectations(
        Guid scheme,
        int cpu,
        int brightness,
        int? displayTimeout)
    {
        var list = new List<VerificationExpectation>
        {
            new(PowerModeStateField.ActiveSchemeId, scheme.ToString(), true),
            new(PowerModeStateField.CpuMaximumAcPercent, cpu.ToString(CultureInfo.InvariantCulture), true),
            new(PowerModeStateField.CpuMaximumDcPercent, cpu.ToString(CultureInfo.InvariantCulture), true),
            new(PowerModeStateField.BrightnessAcPercent, brightness.ToString(CultureInfo.InvariantCulture), false),
            new(PowerModeStateField.BrightnessDcPercent, brightness.ToString(CultureInfo.InvariantCulture), false)
        };
        if (displayTimeout is int timeout)
        {
            var value = timeout.ToString(CultureInfo.InvariantCulture);
            list.Add(new(PowerModeStateField.DisplayTimeoutAcSeconds, value, true));
            list.Add(new(PowerModeStateField.DisplayTimeoutDcSeconds, value, true));
        }
        list.Add(new(PowerModeStateField.SleepTimeoutAcSeconds, "0", true));
        list.Add(new(PowerModeStateField.SleepTimeoutDcSeconds, "0", true));
        list.Add(new(PowerModeStateField.HibernateTimeoutAcSeconds, "0", true));
        list.Add(new(PowerModeStateField.HibernateTimeoutDcSeconds, "0", true));
        return list;
    }
}
