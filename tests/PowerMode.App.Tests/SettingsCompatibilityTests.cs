using System.Text.Json;
using System.Xml.Linq;
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

    [Fact]
    public void LoadStrict_PropertyTypeMismatch_ThrowsInsteadOfReturningDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"powermode-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, """{"MonitorIntervalSeconds":"fast"}""");

        try
        {
            Assert.Throws<JsonException>(() => SettingsStore.LoadStrict(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreConfigurationBackupAsync_PropertyTypeMismatchDoesNotReplaceLiveSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"powermode-restore-{Guid.NewGuid():N}");
        using var integration = new SystemIntegrationService(
            executablePath: Environment.ProcessPath!,
            dataDirectory: directory);
        Directory.CreateDirectory(integration.BackupDirectory);
        var liveJson = """{"LastMode":"balanced","MonitorIntervalSeconds":15}""";
        await File.WriteAllTextAsync(integration.DefaultConfigurationPath, liveJson);
        var backupPath = Path.Combine(integration.BackupDirectory, "invalid.json");
        await File.WriteAllTextAsync(backupPath, """{"MonitorIntervalSeconds":"fast"}""");

        try
        {
            await Assert.ThrowsAsync<JsonException>(() =>
                integration.RestoreConfigurationBackupAsync(
                    backupPath,
                    integration.DefaultConfigurationPath,
                    createSafetyBackup: true));
            Assert.Equal(liveJson, await File.ReadAllTextAsync(integration.DefaultConfigurationPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SettingsWindow_BrightnessPresentation_UsesNamedContainer()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "SettingsWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var container = document
            .Descendants()
            .SingleOrDefault(element =>
                (string?)element.Attribute(x + "Name") == "BrightnessSettingsPanel");

        Assert.NotNull(container);
        Assert.Equal("StackPanel", container.Name.LocalName);
        var names = container
            .Descendants()
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => name is not null)
            .ToHashSet();
        Assert.Contains("BrightnessLabel", names);
        Assert.Contains("BrightnessValue", names);
        Assert.Contains("BrightnessSlider", names);
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
