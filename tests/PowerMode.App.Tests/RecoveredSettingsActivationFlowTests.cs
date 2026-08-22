using System.Collections.Concurrent;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class RecoveredSettingsActivationFlowTests
{
    [Fact]
    public async Task RunAsync_AcceptsRecoveredSettingsButDoesNotApplyPresentationWhenResumeFails()
    {
        var events = new List<string>();
        SettingsLoadResult? accepted = null;
        var flow = new RecoveredSettingsActivationFlow(
            recovered =>
            {
                accepted = recovered;
                events.Add("accept");
            },
            _ =>
            {
                events.Add("resume");
                throw new InvalidOperationException("journal remains unresolved");
            },
            _ => events.Add("apply"));
        var recovered = LoadedSettings();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            flow.RunAsync(recovered));

        Assert.Same(recovered, accepted);
        Assert.Equal(["accept", "resume"], events);
    }

    [Fact]
    public async Task RunAsync_AppliesPresentationOnlyAfterResumeSucceeds()
    {
        var events = new List<string>();
        var flow = new RecoveredSettingsActivationFlow(
            _ => events.Add("accept"),
            _ =>
            {
                events.Add("resume");
                return Task.CompletedTask;
            },
            _ => events.Add("apply"));

        await flow.RunAsync(LoadedSettings());

        Assert.Equal(["accept", "resume", "apply"], events);
    }

    [Fact]
    public async Task RunAsync_AsynchronousResumeReturnsToCapturedSynchronizationContext()
    {
        using var context = new SingleThreadSynchronizationContext();
        var resumeStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResume = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int? presentationThreadId = null;
        SynchronizationContext? presentationContext = null;

        var run = context.RunAsync(async () =>
        {
            var flow = new RecoveredSettingsActivationFlow(
                _ => { },
                async _ =>
                {
                    resumeStarted.TrySetResult(true);
                    await releaseResume.Task.ConfigureAwait(false);
                },
                _ =>
                {
                    presentationThreadId = Environment.CurrentManagedThreadId;
                    presentationContext = SynchronizationContext.Current;
                });

            await flow.RunAsync(LoadedSettings());
        });
        await resumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseResume.TrySetResult(true);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(context.ThreadId, presentationThreadId);
        Assert.Same(context, presentationContext);
    }

    private static SettingsLoadResult LoadedSettings() =>
        new(SettingsLoadState.Loaded, new PowerModeSettings());

    private sealed class SingleThreadSynchronizationContext :
        SynchronizationContext,
        IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)>
            _work = [];
        private readonly TaskCompletionSource<bool> _ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        public SingleThreadSynchronizationContext()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "RecoveredSettingsActivationFlowTests"
            };
            _thread.Start();
            _ready.Task.GetAwaiter().GetResult();
        }

        public int ThreadId { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state) =>
            _work.Add((callback, state));

        public Task RunAsync(Func<Task> action)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try
                {
                    await action();
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }, null);
            return completion.Task;
        }

        public void Dispose()
        {
            _work.CompleteAdding();
            _thread.Join();
            _work.Dispose();
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            ThreadId = Environment.CurrentManagedThreadId;
            _ready.TrySetResult(true);
            foreach (var (callback, state) in _work.GetConsumingEnumerable())
                callback(state);
        }
    }
}
