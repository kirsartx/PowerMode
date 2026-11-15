using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class SettingsStoreLoadTests
{
    [Fact]
    public void Load_MissingFile_ReturnsFirstRunWithoutWriting()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settings.json");

        var result = SettingsStore.Load(
            path,
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        Assert.Equal(SettingsLoadState.FirstRun, result.State);
        Assert.False(File.Exists(path));
        Assert.True(result.AllowsExternalSideEffects);
        Assert.NotNull(result.Settings);
    }

    [Fact]
    public void Load_CurrentSchema_ReturnsLoaded()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"lastMode\":\"remote\"}");

        var result = SettingsStore.Load(
            path,
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        Assert.Equal(SettingsLoadState.Loaded, result.State);
        Assert.Equal("remote", result.Settings.LastMode);
    }

    [Fact]
    public void Load_LegacyMissingSchema_ReturnsMigrated()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{\"lastMode\":\"remote\"}");

        var result = SettingsStore.Load(
            path,
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        Assert.Equal(SettingsLoadState.Migrated, result.State);
        Assert.Equal("remote", result.Settings.LastMode);
        Assert.False(result.Settings.SchemaVersion == 0);
    }

    [Fact]
    public void Load_MalformedJson_QuarantinesExactBytesWithoutOverwrite()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settings.json");
        var bytes = Encoding.UTF8.GetBytes("{ invalid");
        File.WriteAllBytes(path, bytes);
        var now = new DateTimeOffset(2026, 8, 9, 1, 2, 3, 456, TimeSpan.Zero);

        var result = SettingsStore.Load(
            path,
            new FixedTimeProvider(now));

        Assert.Equal(SettingsLoadState.Corrupt, result.State);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.NotNull(result.QuarantinePath);
        Assert.Equal(bytes, File.ReadAllBytes(result.QuarantinePath!));
        Assert.Contains("20260809T010203456Z", result.QuarantinePath!);
        Assert.Contains(
            Convert.ToHexString(SHA256.HashData(bytes)),
            result.QuarantinePath!);
        Assert.False(result.AllowsExternalSideEffects);
    }

    [Fact]
    public void Load_FutureSchema_IsCorruptAndPreservesOriginal()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settings.json");
        var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":99}");
        File.WriteAllBytes(path, bytes);

        var result = SettingsStore.Load(
            path,
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        Assert.Equal(SettingsLoadState.Corrupt, result.State);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Contains("schema", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_FutureSchema_ThrowsAndDoesNotNormalize()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "import.json");
        File.WriteAllText(path, "{\"schemaVersion\":99,\"lastMode\":\"remote\"}");

        var error = Assert.Throws<InvalidDataException>(() => SettingsStore.Import(path));

        Assert.Contains("99", error.Message);
    }

    [Fact]
    public void Import_CurrentSchema_NormalizesLikeLoad()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "import.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"lastMode\":\"remote\"}");

        var settings = SettingsStore.Import(path);

        Assert.Equal(SettingsStore.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("remote", settings.LastMode);
        Assert.Equal(ExperienceMode.Simple, settings.ExperienceMode);
    }

    [Fact]
    public void Import_MissingSchema_IsAcceptedAsLegacy()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "import.json");
        File.WriteAllText(path, "{\"lastMode\":\"high\"}");

        var settings = SettingsStore.Import(path);

        Assert.Equal("high", settings.LastMode);
        Assert.Equal(SettingsStore.CurrentSchemaVersion, settings.SchemaVersion);
    }
}
