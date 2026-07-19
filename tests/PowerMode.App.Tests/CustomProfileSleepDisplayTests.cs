using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class CustomProfileSleepDisplayTests
{
    [Theory]
    [InlineData(300, "300s / 0s")]
    [InlineData(60, "60s / 0s")]
    [InlineData(0, "0s / 0s")]
    public void Format_UsesDisplayOffThenSleep_MatchingStatusCardLabel(
        int displayOffSeconds,
        string expected)
    {
        // Status card label is 关屏 / 睡眠 (Display off / Sleep).
        // ApplyStatus formats: FormatDuration(display) / FormatDuration(sleep).
        // Custom presets set VIDEOIDLE=displayOffSeconds and STANDBYIDLE=0.
        var actual = CustomProfileSleepDisplay.Format(displayOffSeconds);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyCustomProfile_SuccessPath_UsesSharedDisplayFormatter()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.Features.cs"));

        Assert.Contains("CustomProfileSleepDisplay.Format(profile.DisplayOffSeconds)", source);
        Assert.DoesNotContain(
            "SleepValue.Text=$\"0s / {profile.DisplayOffSeconds}s\"",
            source);
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
