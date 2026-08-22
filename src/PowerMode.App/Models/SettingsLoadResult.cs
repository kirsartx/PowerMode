namespace PowerModeWinUI;

public enum SettingsLoadState
{
    FirstRun,
    Loaded,
    Migrated,
    Corrupt
}

public sealed record SettingsLoadResult(
    SettingsLoadState State,
    PowerModeSettings Settings,
    string? QuarantinePath = null,
    string? Error = null)
{
    public bool AllowsExternalSideEffects =>
        State is not SettingsLoadState.Corrupt;
}

internal interface ISettingsFileSystem
{
    bool Exists(string path);
    byte[] ReadAllBytes(string path);
    void CreateDirectory(string path);
    void WriteAllBytes(string path, byte[] bytes);
}

internal sealed class BclSettingsFileSystem : ISettingsFileSystem
{
    public bool Exists(string path) => File.Exists(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);
}
