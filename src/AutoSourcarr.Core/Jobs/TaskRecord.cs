namespace AutoSourcarr.Core.Jobs;

public sealed record TaskRecord
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string State { get; init; } = "running";
    public int Current { get; init; }
    public int Total { get; init; }
    public string Message { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    public string StartedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
    public string? FinishedAt { get; init; }
    public string Error { get; init; } = "";
    public bool Cancellable { get; init; }
    public bool CancelRequested { get; init; }
    public string Activity { get; init; } = "running";
    public string WaitingSince { get; init; } = "";
    public string Context { get; init; } = "";
    public string Origin { get; init; } = "automatic";
    public string Kind { get; init; } = "";
    public bool Interrupted { get; init; }
}
