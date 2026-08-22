namespace PowerModeWinUI;

internal static class PowerModeEngineLocator
{
    public static string Find(
        string baseDirectory,
        string? environmentOverride = null)
    {
        var fullBase = Path.GetFullPath(baseDirectory);
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(environmentOverride))
        {
            candidates.Add(Path.GetFullPath(environmentOverride));
        }

        candidates.Add(Path.GetFullPath(Path.Combine(
            fullBase,
            "..",
            "PowerMode.Engine.ps1")));
        candidates.Add(Path.Combine(fullBase, "PowerMode.Engine.ps1"));

        for (var directory = new DirectoryInfo(fullBase);
             directory is not null;
             directory = directory.Parent)
        {
            candidates.Add(Path.Combine(
                directory.FullName,
                "src",
                "PowerMode.Cli",
                "PowerMode.Engine.ps1"));
        }

        var result = candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(candidate =>
                File.Exists(candidate) &&
                string.Equals(
                    Path.GetFileName(candidate),
                    "PowerMode.Engine.ps1",
                    StringComparison.OrdinalIgnoreCase));

        return result ?? throw new FileNotFoundException(
            $"PowerMode.Engine.ps1 was not found. Tried: {string.Join("; ", candidates)}");
    }
}
