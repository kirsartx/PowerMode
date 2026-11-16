using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class LastAppliedModeTests
{
    [Fact]
    public void RememberCustomProfile_KeepsSaverFallbackAndProfileName()
    {
        var settings = new PowerModeSettings
        {
            LastMode = "high",
            Profiles = [new CustomPowerProfile { Name = "安静办公" }]
        };

        LastAppliedMode.RememberCustomProfile(settings, settings.Profiles[0]);

        Assert.Equal("saver", settings.LastMode);
        Assert.Equal("安静办公", settings.LastCustomProfileName);
    }

    [Fact]
    public void RememberPreset_ClearsCustomProfileName()
    {
        var settings = new PowerModeSettings
        {
            LastMode = "saver",
            LastCustomProfileName = "安静办公"
        };

        LastAppliedMode.RememberPreset(settings, "balanced");

        Assert.Equal("balanced", settings.LastMode);
        Assert.Null(settings.LastCustomProfileName);
    }

    [Fact]
    public void SelectForStartup_ReappliesNamedProfileInsteadOfSaverFallback()
    {
        var profile = new CustomPowerProfile { Name = "安静办公", CpuMax = 45 };
        var settings = new PowerModeSettings
        {
            ApplyLastModeOnStartup = true,
            LastMode = "saver",
            LastCustomProfileName = "安静办公",
            Profiles = [profile]
        };

        var selection = LastAppliedMode.SelectForStartup(settings);

        Assert.Same(profile, selection.Profile);
        Assert.Null(selection.Preset);
    }

    [Fact]
    public void SelectForStartup_DisabledFlagDoesNothing()
    {
        var settings = new PowerModeSettings
        {
            ApplyLastModeOnStartup = false,
            LastMode = "high",
            LastCustomProfileName = "安静办公",
            Profiles = [new CustomPowerProfile { Name = "安静办公" }]
        };

        var selection = LastAppliedMode.SelectForStartup(settings);

        Assert.False(selection.HasWork);
    }

    [Fact]
    public void SelectForStartup_MissingProfileFallsBackToLastMode()
    {
        var settings = new PowerModeSettings
        {
            ApplyLastModeOnStartup = true,
            LastMode = "saver",
            LastCustomProfileName = "已删除",
            Profiles = [new CustomPowerProfile { Name = "安静办公" }]
        };

        var selection = LastAppliedMode.SelectForStartup(settings);

        Assert.Null(selection.Profile);
        Assert.Equal("saver", selection.Preset);
    }

    [Fact]
    public void SelectForStartup_WithoutCustomNameUsesLastMode()
    {
        var settings = new PowerModeSettings
        {
            ApplyLastModeOnStartup = true,
            LastMode = "remote"
        };

        var selection = LastAppliedMode.SelectForStartup(settings);

        Assert.Null(selection.Profile);
        Assert.Equal("remote", selection.Preset);
    }

    [Fact]
    public void Normalize_PreservesCustomProfileNameWithoutChangingLastModeContract()
    {
        var settings = SettingsStore.Normalize(new PowerModeSettings
        {
            LastMode = "custom:安静办公",
            LastCustomProfileName = "  安静办公  "
        });

        Assert.Equal("balanced", settings.LastMode);
        Assert.Equal("安静办公", settings.LastCustomProfileName);
    }

    [Fact]
    public void Normalize_LegacySettingsOmitCustomProfileName()
    {
        var settings = SettingsStore.Normalize(
            System.Text.Json.JsonSerializer.Deserialize<PowerModeSettings>(
                """{"LastMode":"high"}"""));

        Assert.Equal("high", settings.LastMode);
        Assert.Null(settings.LastCustomProfileName);
    }

    [Fact]
    public void StartupAndSuccessPaths_UseLastAppliedMode()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.AdvancedFeatures.cs"));

        Assert.Contains("LastAppliedMode.SelectForStartup(", source);
        Assert.Contains("LastAppliedMode.RememberCustomProfile(", source);
        Assert.Contains("LastAppliedMode.RememberPreset(", source);
        Assert.DoesNotContain("_featureSettings.LastMode = \"saver\"", source);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PowerMode.slnx")))
                return Path.Combine([directory.FullName, .. path]);
        }

        throw new DirectoryNotFoundException("Could not locate the PowerMode repository root.");
    }
}
