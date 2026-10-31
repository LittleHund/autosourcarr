namespace AutoSourcarr.Core.Concurrency;

public sealed record SourceBudget(TimeSpan Execution, TimeSpan Admission);

public static class SourceWorkContext
{
    private static readonly AsyncLocal<bool> LowPriority = new();
    private static readonly AsyncLocal<SourceBudget?> Budget = new();
    public static bool IsBackground => LowPriority.Value;
    public static SourceBudget? CurrentBudget => Budget.Value;
    public static IDisposable Background(bool enabled = true)
    {
        var previous = LowPriority.Value; LowPriority.Value = enabled;
        return new Scope(() => LowPriority.Value = previous);
    }
    public static IDisposable WithBudget(TimeSpan execution, TimeSpan? admission = null)
    {
        if (execution <= TimeSpan.Zero || execution > TimeSpan.FromHours(1)
            || admission is { } wait && (wait <= TimeSpan.Zero || wait > TimeSpan.FromMinutes(5))) throw new ArgumentOutOfRangeException(nameof(execution));
        var previous = Budget.Value; Budget.Value = new(execution, admission ?? TimeSpan.FromSeconds(3));
        return new Scope(() => Budget.Value = previous);
    }
    private sealed class Scope(Action close) : IDisposable
    {
        private Action? _close = close;
        public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke();
    }
}
