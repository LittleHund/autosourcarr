using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Jobs;

namespace AutoSourcarr.Tests;

public static class TaskCoordinationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("task coordination: idle waits own no whole-task capacity and manual work progresses", () =>
        {
            var lanes = new WholeTaskCoordinator(new AutoSourcarrSettings { BackgroundIdleSeconds = .01 }, new TaskRegistry());
            var interactive = lanes.BeginInteractive(); using var cancel = new CancellationTokenSource();
            var started = false;
            var automatic = lanes.RunBackgroundAsync(_ => { started = true; return Task.FromResult(1); }, waitForIdle: true, cancellationToken: cancel.Token);
            try
            {
                Check.Equal(0, lanes.Snapshot.BackgroundActive); Check.Equal(0, lanes.Snapshot.BackgroundWaiting);
                using (SourceWorkContext.Background())
                    Check.Equal(2, lanes.RunManualAsync(_ => { Check.True(!SourceWorkContext.IsBackground); return Task.FromResult(2); }).GetAwaiter().GetResult());
                Check.True(!started); cancel.Cancel();
                Check.Throws<OperationCanceledException>(() => automatic.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
            }
            finally { interactive.Dispose(); lanes.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        suite.Add("task coordination: automatic and manual lanes stay independent and shutdown joins work", () =>
        {
            var lanes = new WholeTaskCoordinator(new AutoSourcarrSettings { BackgroundTaskConcurrency = 1 }, new TaskRegistry());
            var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var background = lanes.RunBackgroundAsync(async _ => { Check.True(SourceWorkContext.IsBackground); return await release.Task.ConfigureAwait(false); });
            var stop = Task.CompletedTask;
            try
            {
                Check.Equal(1, lanes.Snapshot.BackgroundActive);
                Check.Equal(2, lanes.RunManualAsync(_ => Task.FromResult(2)).GetAwaiter().GetResult());
                stop = lanes.DisposeAsync().AsTask(); Check.True(!stop.IsCompleted);
                release.TrySetResult(3); Check.Equal(3, background.GetAwaiter().GetResult());
                stop.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check.Equal(0, lanes.Snapshot.BackgroundActive);
            }
            finally { release.TrySetResult(3); background.GetAwaiter().GetResult(); lanes.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }
}
