using System.Diagnostics;

namespace PowerModeWinUI.Tests;

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string TestHostExe =>
        RequireFile(Path.ChangeExtension(
            typeof(PowerMode.TestHost.Marker).Assembly.Location,
            ".exe"));

    public static string Repo(params string[] parts) =>
        Path.Combine([RepositoryRoot, .. parts]);

    public static string ReadFixture(string name) =>
        File.ReadAllText(Repo("tests", "PowerMode.App.Tests", "Fixtures", name));

    private static string RequireFile(string path) =>
        File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "Test host runtime output was not copied.", path);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PowerMode.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "PowerMode repository root was not found.");
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
}

internal static class ProcessTestAssertions
{
    public static async Task<bool> ExitedAsync(
        int processId,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"PowerModeTests-{Guid.NewGuid():N}");

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        var full = System.IO.Path.GetFullPath(Path);
        var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !System.IO.Path.GetFileName(full).StartsWith(
                "PowerModeTests-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing to remove an unsafe test path.");
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }
}
