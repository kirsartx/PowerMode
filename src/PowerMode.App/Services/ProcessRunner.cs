using System.Diagnostics;
using System.ComponentModel;

namespace PowerModeWinUI;

internal sealed record ProcessExecutionRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

internal sealed record ProcessExecutionResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut,
    bool Cancelled,
    string? StartError)
{
    public bool Succeeded =>
        StartError is null &&
        !TimedOut &&
        !Cancelled &&
        ExitCode == 0;
}

internal interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    public async Task<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTimeout(request.Timeout);

        var stopwatch = Stopwatch.StartNew();
        if (cancellationToken.IsCancellationRequested)
        {
            return CreateResult(
                stopwatch,
                exitCode: null,
                standardOutput: string.Empty,
                standardError: string.Empty,
                timedOut: false,
                cancelled: true,
                startError: null);
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(request)
        };

        try
        {
            if (!process.Start())
            {
                return CreateResult(
                    stopwatch,
                    exitCode: null,
                    standardOutput: string.Empty,
                    standardError: string.Empty,
                    timedOut: false,
                    cancelled: false,
                    startError: "The process could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return CreateResult(
                stopwatch,
                exitCode: null,
                standardOutput: string.Empty,
                standardError: string.Empty,
                timedOut: false,
                cancelled: false,
                startError: exception.Message);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var processExit = process.WaitForExitAsync();
        var timeout = Task.Delay(request.Timeout);
        var cancellation = CreateCancellationSignal(
            cancellationToken,
            out var cancellationRegistration);

        using (cancellationRegistration)
        {
            var completed = await Task.WhenAny(
                    processExit,
                    timeout,
                    cancellation)
                .ConfigureAwait(false);

            var timedOut = completed == timeout;
            var cancelled = completed == cancellation;
            if (timedOut || cancelled)
            {
                TryKillProcessTree(process);
                await DrainAsync(processExit).ConfigureAwait(false);
            }
            else
            {
                await processExit.ConfigureAwait(false);
            }

            var output = await ReadOutputAsync(standardOutput).ConfigureAwait(false);
            var error = await ReadOutputAsync(standardError).ConfigureAwait(false);
            var exitCode = timedOut || cancelled
                ? (int?)null
                : process.ExitCode;

            return CreateResult(
                stopwatch,
                exitCode,
                output,
                error,
                timedOut,
                cancelled,
                startError: null);
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        ProcessExecutionRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var pair in request.Environment)
            {
                if (pair.Value is null)
                {
                    startInfo.Environment.Remove(pair.Key);
                }
                else
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }

        return startInfo;
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The process timeout must be finite and positive.");
        }
    }

    private static Task<bool> CreateCancellationSignal(
        CancellationToken cancellationToken,
        out CancellationTokenRegistration registration)
    {
        var source = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        registration = cancellationToken.Register(
            static state =>
            {
                ((TaskCompletionSource<bool>)state!).TrySetResult(true);
            },
            source);
        return source.Task;
    }

    private static async Task DrainAsync(Task processExit)
    {
        try
        {
            await processExit.WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task<string> ReadOutputAsync(Task<string> output)
    {
        try
        {
            return await output.WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static ProcessExecutionResult CreateResult(
        Stopwatch stopwatch,
        int? exitCode,
        string standardOutput,
        string standardError,
        bool timedOut,
        bool cancelled,
        string? startError) =>
        new(
            exitCode,
            standardOutput,
            standardError,
            stopwatch.Elapsed,
            timedOut,
            cancelled,
            startError);
}
