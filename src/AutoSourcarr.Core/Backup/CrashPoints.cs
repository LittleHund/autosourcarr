namespace AutoSourcarr.Core.Backup;

/// <summary>
/// Tests only: named points in the backup, restore, swap and update steps where a test can stop the work as a kill
/// would (a <see cref="SimulatedCrash"/> passes through every error handler, like a process that is gone). The app
/// never sets <see cref="Hook"/>.
/// </summary>
internal static class CrashPoints
{
    internal static Action<string>? Hook { get; set; }

    internal static void Reach(string point) => Hook?.Invoke(point);

    /// <summary>True for errors the code may handle; a simulated kill and running out of memory are never handled.</summary>
    internal static bool Handled(Exception error) => error is not (OutOfMemoryException or SimulatedCrash);
}

/// <summary>Stands in for the process being killed at a crash point.</summary>
internal sealed class SimulatedCrash(string point) : Exception("Simulated crash at " + point)
{
    public string Point { get; } = point;
}
