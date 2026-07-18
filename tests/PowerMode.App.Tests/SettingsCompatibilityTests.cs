using System.Text.Json;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class SettingsCompatibilityTests
{
    [Fact]
    public void Normalize_OldSettingsWithoutExperienceMode_DefaultsToSimple()
    {
        var settings = JsonSerializer.Deserialize<PowerModeSettings>("""{"LastMode":"remote"}""");

        var normalized = SettingsStore.Normalize(settings);

        Assert.Equal(ExperienceMode.Simple, normalized.ExperienceMode);
        Assert.Equal("remote", normalized.LastMode);
    }

    [Fact]
    public void Normalize_UnknownExperienceMode_DefaultsToSimple()
    {
        var settings = new PowerModeSettings { ExperienceMode = (ExperienceMode)99 };

        var normalized = SettingsStore.Normalize(settings);

        Assert.Equal(ExperienceMode.Simple, normalized.ExperienceMode);
    }

    [Fact]
    public void Normalize_ProfessionalMode_IsPreserved()
    {
        var settings = new PowerModeSettings { ExperienceMode = ExperienceMode.Professional };

        var normalized = SettingsStore.Normalize(settings);

        Assert.Equal(ExperienceMode.Professional, normalized.ExperienceMode);
    }
}
