namespace PowerModeWinUI;

internal static class PowerModeCliRequests
{
    public static ProcessExecutionRequest Language(string cliPath, string language) =>
        new(
            "cmd.exe",
            // Separate arguments: ArgumentList escapes embedded quotes as \" which cmd.exe does not understand.
            ["/d", "/c", cliPath, "lang", language],
            TimeSpan.FromSeconds(10),
            Path.GetDirectoryName(cliPath),
            new Dictionary<string, string?> { ["PM_NO_PAUSE"] = "1" });
}
