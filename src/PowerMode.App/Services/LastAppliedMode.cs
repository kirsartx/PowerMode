namespace PowerModeWinUI;

/// <summary>
/// Startup restore choice. Exactly one of <see cref="Preset"/> or <see cref="Profile"/>
/// is set when work should run.
/// </summary>
internal readonly record struct StartupModeSelection(string? Preset, CustomPowerProfile? Profile)
{
    public bool HasWork => Profile is not null || !string.IsNullOrEmpty(Preset);
}

/// <summary>
/// Remembers the last successful switch without overloading <see cref="PowerModeSettings.LastMode"/>.
/// A custom profile still records <c>saver</c> as the standard fallback because that is the
/// scheme custom profiles write. Startup reapplies the named profile when it still exists.
/// </summary>
internal static class LastAppliedMode
{
    public static void RememberPreset(PowerModeSettings settings, string preset)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.LastCustomProfileName = null;
        settings.LastMode = preset;
    }

    public static void RememberCustomProfile(PowerModeSettings settings, CustomPowerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        settings.LastCustomProfileName = string.IsNullOrWhiteSpace(profile.Name)
            ? null
            : profile.Name.Trim();
        settings.LastMode = "saver";
    }

    public static StartupModeSelection SelectForStartup(PowerModeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.ApplyLastModeOnStartup)
            return default;

        var name = settings.LastCustomProfileName?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            var profile = settings.Profiles?.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (profile is not null)
                return new(null, profile);
        }

        var preset = settings.LastMode?.Trim().ToLowerInvariant();
        if (preset is not ("remote" or "saver" or "balanced" or "high"))
            preset = "balanced";
        return new(preset, null);
    }
}
