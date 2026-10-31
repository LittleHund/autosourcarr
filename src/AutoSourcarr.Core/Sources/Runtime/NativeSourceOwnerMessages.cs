namespace AutoSourcarr.Core.Sources.Runtime;

// These are wire values, not credentials, launch permits or readiness evidence.
internal enum NativeSourceOwnerOpcode : ushort { Start = 1, Stop = 2, Status = 3, Shutdown = 4, PhysicalSelfTest = 5, Observe = 6, ReleaseSnapshot = 7 }
internal enum NativeSourceOwnerState : uint { Idle = 0, Starting = 1, Running = 2, Stopping = 3, Faulted = 4 }
internal enum NativeSourceOwnerResult : ushort { Ok = 0, Unavailable = 1, Busy = 2, ProtocolError = 3, Failed = 4 }
internal enum NativeSourceOwnerDiagnostic : uint { None = 0, Asset = 1, Root = 2, Protocol = 3, Exec = 4, Exit = 5, Signal = 6, Io = 7, Wait = 8 }
internal enum NativeSourceOwnerProfile : uint { Production = 0, PhysicalTestOnly = 1 }

[Flags]
internal enum NativeSourceOwnerFacts : uint
{
    None = 0, Exit = 1, NoChildren = 2, StreamsDrained = 4, PhysicalTest = 8, Failed = 16, SigkillReaped = 32
}

internal readonly record struct NativeSourceOwnerRoot(ulong Device, ulong Inode);

/// <summary>A parsed message snapshot. It neither authenticates PID1 nor grants access to a source root.</summary>
internal sealed record NativeSourceOwnerProofMessage(uint OwnerPid, uint ParentPid, uint Uid, uint Gid,
    NativeSourceOwnerRoot Root, NativeSourceOwnerProfile Profile);

/// <summary>Raw observations only. Generation remains unsigned until a consumer explicitly checks its own range.</summary>
internal sealed record NativeSourceOwnerResponseMessage(ulong RequestId, NativeSourceOwnerOpcode Opcode,
    NativeSourceOwnerResult Result, ulong Generation, NativeSourceOwnerState State, uint LeaderPid,
    uint ProcessGroupId, int ExitStatus, NativeSourceOwnerFacts Facts, uint Reaped, uint AdoptedReaped,
    NativeSourceOwnerDiagnostic Diagnostic, ulong DiscardedLogBytes, NativeSourceOwnerRoot Root);

internal sealed class NativeSourceOwnerCommandMessage
{
    internal NativeSourceOwnerCommandMessage(ulong requestId, NativeSourceOwnerOpcode opcode,
        IEnumerable<KeyValuePair<string, string>> environment, NativeSourceSnapshotRequest? observation = null, string? snapshotTicket = null)
    {
        RequestId = requestId; Opcode = opcode;
        Environment = Array.AsReadOnly(environment.ToArray());
        Observation = observation; SnapshotTicket = snapshotTicket;
    }

    public ulong RequestId { get; }
    public NativeSourceOwnerOpcode Opcode { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Environment { get; }
    public NativeSourceSnapshotRequest? Observation { get; }
    public string? SnapshotTicket { get; }
    // Do not turn an accidental log of this message into an environment export.
    public override string ToString() => "Native source-owner command message.";
}
