using System.Diagnostics;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CompletedProcess_CapturesBothStreams()
    {
        var result = await new ProcessRunner().RunAsync(new(
            TestPaths.TestHostExe,
            ["echo", "hello", "warning"],
            TimeSpan.FromSeconds(5)));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.StandardOutput);
        Assert.Equal("warning", result.StandardError);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Null(result.StartError);
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ReportsExitCodeAndOutput()
    {
        var result = await new ProcessRunner().RunAsync(new(
            TestPaths.TestHostExe,
            ["exit", "7"],
            TimeSpan.FromSeconds(5)));

        Assert.False(result.Succeeded);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_ConvertsStartFailureToResult()
    {
        var result = await new ProcessRunner().RunAsync(new(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"),
            [],
            TimeSpan.FromSeconds(5)));

        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.NotNull(result.StartError);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task RunAsync_Timeout_KillsTheProcessTree()
    {
        var result = await new ProcessRunner().RunAsync(new(
            TestPaths.TestHostExe,
            ["spawn-child", "30000"],
            TimeSpan.FromSeconds(3)));

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);

        var childPid = int.Parse(result.StandardOutput.Trim());
        Assert.True(await ProcessTestAssertions.ExitedAsync(
            childPid,
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RunAsync_Cancellation_KillsTheProcessTree()
    {
        using var cancellation = new CancellationTokenSource();
        var running = new ProcessRunner().RunAsync(new(
            TestPaths.TestHostExe,
            ["spawn-child", "30000"],
            TimeSpan.FromSeconds(30)),
            cancellation.Token);

        await Task.Delay(300);
        cancellation.Cancel();
        var result = await running;

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);

        var childPid = int.Parse(result.StandardOutput.Trim());
        Assert.True(await ProcessTestAssertions.ExitedAsync(
            childPid,
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RunAsync_PreCancelled_DoesNotStartProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new ProcessRunner().RunAsync(new(
            TestPaths.TestHostExe,
            ["exit", "7"],
            TimeSpan.FromSeconds(5)),
            cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task LanguageRequest_RunsBatchFileFromPathWithSpaces()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pm lang {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var cli = Path.Combine(directory, "Power Mode Switcher.bat");
            await File.WriteAllTextAsync(cli, "@echo off\r\necho args=%*\r\n");

            var result = await new ProcessRunner().RunAsync(
                PowerModeCliRequests.Language(cli, "en"));

            Assert.True(result.Succeeded, result.StandardError);
            Assert.Equal("args=lang en", result.StandardOutput.Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RequiresFinitePositiveTimeout()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new ProcessRunner().RunAsync(new(
                TestPaths.TestHostExe,
                ["exit", "0"],
                Timeout.InfiniteTimeSpan)));
    }
}
