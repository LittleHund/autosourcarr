namespace AutoSourcarr.Core.Jobs;

/// <summary>Phase 13 audit 6: ends a long-running job's work while an exclusive job (backup, restore, reset) waits.</summary>
public static class ExclusiveYield
{
    /// <summary>Polls <paramref name="pending"/> every second until <paramref name="done"/>; on the first true it sets the flag and cancels.</summary>
    public static async Task WatchAsync(Func<bool> pending, Action yielded, Task done)
    {
        while (!done.IsCompleted)
        {
            if (pending()) { yielded(); return; }
            await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }
    }
}
