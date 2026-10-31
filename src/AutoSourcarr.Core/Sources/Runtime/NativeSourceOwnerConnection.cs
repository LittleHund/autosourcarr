using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Privacy;

[assembly: InternalsVisibleTo("AutoSourcarr.Api")]

namespace AutoSourcarr.Core.Sources.Runtime;

/// <summary>
/// Owns one fixed inherited native-owner channel. The immutable image/private
/// native fork/exec chain remains a launch prerequisite; anonymous pipes and
/// decoded messages alone do not authenticate PID1 or establish source readiness.
/// </summary>
internal sealed partial class NativeSourceOwnerConnection : IAsyncDisposable
{
    private static int _inheritedClaimed;
    private readonly Actor _actor;
    private readonly object _snapshotGate = new();
    private NativeSourceSnapshots? _snapshots;
    private Task<Termination>? _shutdownWithSnapshots;
    private NativeSourceOwnerConnection(Actor actor) => _actor = actor;
    internal SourceStartupRequest StartupRequest => _actor.Binding!.StartupRequest;
    internal NativeSourceOwnerProofMessage OwnerMessage => _actor.Binding!.ExpectedOwner!;
    /// <summary>Early terminal fencing, NOT a drain/reap receipt. Null denotes requested shutdown.</summary>
    internal Task<Exception?> RetirementStarted => _actor.RetirementStarted;
    internal Task<OwnerExit> OwnerExited => _actor.OwnerExited;
    internal void RequireDiagnosticPrivacy(SupportPrivacy privacy) => _actor.RequireDiagnosticPrivacy(privacy, allowMissing: false);

    /// <summary>Final Main must call this before loading private configuration.
    /// A normal exec resets the native parent's dumpability setting.</summary>
    internal static void RequirePostExecPrivacyBeforeConfiguration()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Native startup privacy requires Linux x64.");
        LinuxPipes.RequirePostExecPrivacy();
    }

    /// <summary>
    /// Must run before the final host admits work. Any failure is fatal startup:
    /// a partially adopted handoff can close and join local IO without proving
    /// owner retirement, so the host must exit for native PID1 reconciliation.
    /// It must never catch this failure and continue or try a second owner.
    /// </summary>
    internal static async Task<NativeSourceOwnerConnection?> AcceptInheritedAsync(SourceStartupRequest currentRequest, CancellationToken startupCancellation,
        SupportLogStore? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(currentRequest);
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Native source-owner handoff requires Linux x64.");
        RequirePostExecPrivacyBeforeConfiguration();
        if (currentRequest.Mode == "integrated") ArgumentNullException.ThrowIfNull(diagnostics);
        if (Interlocked.CompareExchange(ref _inheritedClaimed, 1, 0) != 0)
            throw new InvalidOperationException("The inherited native boot channel was already claimed.");
        var actor = new Actor(new LinuxPipes(), currentRequest, startupCancellation, diagnostics);
        var binding = await AcceptActorAsync(actor, startupCancellation).ConfigureAwait(false);
        return binding.ExpectedOwner is null ? null : new(actor);
    }
    private static async Task<BootMessage> AcceptActorAsync(Actor actor, CancellationToken startupCancellation)
    {
        try
        {
            var binding = await actor.Acceptance.ConfigureAwait(false);
            startupCancellation.ThrowIfCancellationRequested();
            return binding;
        }
        catch (Exception primary)
        {
            try { await actor.CloseAndJoinAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (ReferenceEquals(primary, cleanup)) { }
            catch (Exception cleanup) { throw new AggregateException("Native bootstrap and cleanup failed", primary, cleanup); }
            throw;
        }
    }

    internal Task<NativeSourceOwnerResponseMessage> StartAsync(IReadOnlyList<KeyValuePair<string, string>> projectedLaunch, CancellationToken caller)
        => _actor.Call(NativeSourceOwnerOpcode.Start, projectedLaunch, caller);
    internal Task<NativeSourceOwnerResponseMessage> StatusAsync(CancellationToken caller) => _actor.Call(NativeSourceOwnerOpcode.Status, [], caller);
    internal Task<SnapshotLease> ObserveAsync(NativeSourceSnapshotRequest request, CancellationToken caller) => _actor.Observe(request, caller);
    internal bool OwnsSnapshot(SnapshotLease lease) => _actor.OwnsSnapshot(lease);
    internal void RequireSnapshotMainIdentity() => _actor.RequireMainForSnapshot();
    internal void AttachSnapshots(NativeSourceSnapshots snapshots)
    {
        lock (_snapshotGate)
        {
            if (_snapshots is not null || _shutdownWithSnapshots is not null || RetirementStarted.IsCompleted)
                throw new InvalidOperationException("A snapshot owner is already attached or native retirement has started.");
            _snapshots = snapshots;
        }
    }
    internal Task<NativeSourceOwnerResponseMessage> StopAsync()
    {
        NativeSourceSnapshots? snapshots; lock (_snapshotGate) snapshots = _snapshots;
        return snapshots is null ? _actor.Stop() : snapshots.WithPausedAsync(_actor.Stop);
    }
    internal Task<Termination> ShutdownAsync()
    {
        lock (_snapshotGate) return _shutdownWithSnapshots ??= _snapshots is null ? _actor.ShutdownAsync() : _snapshots.StopBeforeAsync(_actor.ShutdownAsync);
    }
    public async ValueTask DisposeAsync() => _ = await ShutdownAsync().ConfigureAwait(false);

    internal sealed record OwnerExit(uint OwnerPid, int WaitStatus);
    /// <summary>One actor-issued lease, not authority constructed from decoded
    /// message values. Release is owned and uncancelled after admission.</summary>
    internal sealed class SnapshotLease(object authority, NativeSourceOwnerResponseMessage response,
        NativeSourceObservationMessage observation, Func<Task<NativeSourceOwnerResponseMessage>> release)
    {
        private readonly object _gate = new();
        private Task<NativeSourceOwnerResponseMessage>? _release;
        internal NativeSourceOwnerResponseMessage Response { get; } = response;
        internal NativeSourceObservationMessage Observation { get; } = observation;
        internal bool MatchesAuthority(object candidate) => ReferenceEquals(authority, candidate);
        internal Task<NativeSourceOwnerResponseMessage> ReleaseAsync() { lock (_gate) return _release ??= release(); }
        // Explicit reconciliation can observe an already absent exact ticket after
        // a previous failed close. There is no automatic retry loop or new owner.
        internal Task<NativeSourceOwnerResponseMessage> ReconcileAsync()
        {
            lock (_gate)
            {
                if (_release is null || _release.IsCompleted && (!_release.IsCompletedSuccessfully || _release.Result.Result != NativeSourceOwnerResult.Ok)) _release = release();
                return _release;
            }
        }
        public override string ToString() => "Owned native source snapshot lease.";
    }
    internal sealed record Termination(NativeSourceOwnerResponseMessage Reply, OwnerExit Exit);
    internal readonly record struct PipeIdentity(ulong Device, ulong Inode, int Access);
    internal sealed record BootMessage(uint MainPid, uint OriginalUid, uint OriginalGid, uint GroupCount, uint KvmGid,
        string Nonce, SourceStartupRequest StartupRequest, NativeSourceOwnerProofMessage? ExpectedOwner, IReadOnlyList<PipeIdentity> Pipes)
    { public override string ToString() => "Native boot message."; }
    internal sealed class LifetimeFailure(Exception inner, NativeSourceOwnerResponseMessage? last, OwnerExit? observed)
        : IOException("Native source-owner lifetime failed; the instance must exit without claiming a clean drain.", inner)
    {
        internal NativeSourceOwnerResponseMessage? LastResponse { get; } = last;
        // A parsed record may lack its required EOF; this is retained partial evidence.
        internal OwnerExit? ObservedExitRecord { get; } = observed;
    }

    /// <summary>Pure auxiliary message decoding; never creates a connection or native authority.</summary>
    internal static BootMessage ParseBootMessage(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 192 || !frame[..8].SequenceEqual("ASBIND1\n"u8) || U32(frame, 8) != 1 || U32(frame, 16) != 1 || U32(frame, 28) != 0) throw Invalid();
        var pid = U32(frame, 12); var uid = U32(frame, 20); var gid = U32(frame, 24); var groups = U32(frame, 104); var kvm = U32(frame, 108);
        var rawLength = U32(frame, 96); var proofLength = U32(frame, 100);
        if (pid is <= 1 or > int.MaxValue || uid == uint.MaxValue || gid == uint.MaxValue || groups > 1 || kvm == uint.MaxValue
            || (groups == 0 && kvm != 0) || rawLength is 0 or > SourceStartupRequest.MaximumFrameBytes || proofLength is not (0 or 76)
            || frame.Length != 192L + rawLength + proofLength) throw Invalid();
        var nonce = Encoding.ASCII.GetString(frame.Slice(32, 64));
        foreach (var value in frame.Slice(32, 64)) if (value is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'a' and <= (byte)'f')) throw Invalid();
        SourceStartupRequest request;
        try { request = SourceStartupRequest.Decode(frame.Slice(192, (int)rawLength), nonce); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException) { throw Invalid(); }
        var integrated = request.Mode == "integrated"; var pipes = new PipeIdentity[5];
        for (var index = 0; index < pipes.Length; index++)
        {
            pipes[index] = new(U64(frame, 112 + index * 16), U64(frame, 120 + index * 16), index == 0 ? 1 : 0);
            var present = integrated || index == 3;
            if (present ? pipes[index].Inode == 0 : pipes[index].Device != 0 || pipes[index].Inode != 0) throw Invalid();
            for (var previous = 0; present && previous < index; previous++)
                if (pipes[previous].Inode != 0 && pipes[previous].Device == pipes[index].Device && pipes[previous].Inode == pipes[index].Inode) throw Invalid();
        }
        NativeSourceOwnerProofMessage? expected = null;
        if (integrated)
        {
            if (proofLength != 76) throw Invalid();
            var proof = frame[(192 + (int)rawLength)..];
            expected = new(U32(proof, 40), U32(proof, 44), U32(proof, 48), U32(proof, 52), new(U64(proof, 56), U64(proof, 64)), (NativeSourceOwnerProfile)U32(proof, 72));
            if (expected.OwnerPid <= 1 || expected.OwnerPid == pid || expected.ParentPid != 1 || expected.Root.Inode == 0 || expected.Profile != NativeSourceOwnerProfile.Production) throw Invalid();
            NativeSourceOwnerProtocol.DecodeProof(proof, Convert.FromHexString(nonce), expected);
        }
        else if (proofLength != 0) throw Invalid();
        return new(pid, uid, gid, groups, kvm, nonce, request, expected, Array.AsReadOnly(pipes));
    }
    internal static OwnerExit ParseReapMessage(ReadOnlySpan<byte> frame, string nonce, uint ownerPid)
    {
        if (nonce is null || nonce.Length != 64 || nonce.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw Invalid();
        if (frame.Length != 48 || !frame[..8].SequenceEqual("ASREAP1\n"u8) || ownerPid is <= 1 or > int.MaxValue
            || U32(frame, 40) != ownerPid || !CryptographicOperations.FixedTimeEquals(frame.Slice(8, 32), Convert.FromHexString(nonce))) throw Invalid();
        return new(ownerPid, BinaryPrimitives.ReadInt32LittleEndian(frame[44..]));
    }
    private static void RequireResolvedIdentity(BootMessage message)
    {
        if (message.ExpectedOwner is not { } owner) return;
        if (message.OriginalUid != 0)
        {
            // Released non-root startup inherits actual original IDs, including
            // when unused requested IDs are negative or wider than uint32.
            if (owner.Uid != message.OriginalUid || owner.Gid != message.OriginalGid) throw Invalid();
            return;
        }
        foreach (var (requested, observed) in new[] { (message.StartupRequest.Uid, owner.Uid), (message.StartupRequest.Gid, owner.Gid) })
        {
            if (requested.IsZero) continue; // inference belongs to native's held source root
            if (requested <= 0 || requested >= uint.MaxValue || (uint)requested != observed) throw Invalid();
        }
    }
    private static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
    private static InvalidDataException Invalid() => new("Invalid native source-owner boot or lifecycle message.");

    // This seam exercises the real actor/codec/joins against controlled IO. It
    // never constructs or returns NativeSourceOwnerConnection/production binding.
    internal abstract class ControlledPeer
    {
        internal abstract long Milliseconds { get; }
        internal abstract void OpenBoot();
        internal abstract void RequireMainIdentity(uint pid, uint groups, uint kvmGid);
        internal abstract PipeIdentity Inspect(int slot);
        internal abstract void AdoptIntegrated();
        internal abstract int Read(int slot, Span<byte> destination); // -1 means EAGAIN/EINTR,0 EOF
        internal abstract int Write(ReadOnlySpan<byte> bytes);
        internal abstract void Poll(bool writing, int milliseconds);
        internal abstract void Close(int slot);
        internal abstract void CloseAll();
        internal virtual void BeforePublication(NativeSourceOwnerResponseMessage response) { }
    }
    internal sealed class ControlledHarness : IAsyncDisposable
    {
        private readonly Actor _actor; private readonly Task _acceptance;
        internal ControlledHarness(ControlledPeer peer, SourceStartupRequest request, CancellationToken cancellation = default, SupportLogStore? diagnostics = null)
        { _actor = new(new ControlledPipes(peer), request, cancellation, diagnostics); _acceptance = AcceptActorAsync(_actor, cancellation); }
        internal Task Acceptance => _acceptance;
        internal Task<Exception?> RetirementStarted => _actor.RetirementStarted;
        internal Task<OwnerExit> OwnerExited => _actor.OwnerExited;
        internal SupportLogStore.NativeSourceCapture.CaptureState? SourceOutputState => _actor.SourceOutputState;
        internal void RequireDiagnosticPrivacy(SupportPrivacy privacy) => _actor.RequireDiagnosticPrivacy(privacy, allowMissing: true);
        internal Task<NativeSourceOwnerResponseMessage> StartAsync(IReadOnlyList<KeyValuePair<string, string>> launch, CancellationToken caller = default) => _actor.Call(NativeSourceOwnerOpcode.Start, launch, caller);
        internal Task<NativeSourceOwnerResponseMessage> StatusAsync(CancellationToken caller = default) => _actor.Call(NativeSourceOwnerOpcode.Status, [], caller);
        internal Task<SnapshotLease> ObserveAsync(NativeSourceSnapshotRequest request, CancellationToken caller = default) => _actor.Observe(request, caller);
        internal bool OwnsSnapshot(SnapshotLease lease) => _actor.OwnsSnapshot(lease);
        internal Task<NativeSourceOwnerResponseMessage> StopAsync() => _actor.Stop();
        internal Task<Termination> ShutdownAsync() => _actor.ShutdownAsync();
        public async ValueTask DisposeAsync() => await _actor.CloseAndJoinAsync().ConfigureAwait(false);
    }
    private interface IPipes
    {
        long Milliseconds { get; }
        void Begin(); void RequireMainIdentity(uint pid, uint groups, uint kvm); PipeIdentity Inspect(int slot); void AdoptIntegrated();
        int Read(int slot, Span<byte> destination); int Write(ReadOnlySpan<byte> bytes); void Poll(bool writing, int milliseconds);
        void Close(int slot); void CloseAll(); void BeforePublication(NativeSourceOwnerResponseMessage response);
    }
    private sealed class ControlledPipes(ControlledPeer peer) : IPipes
    {
        public long Milliseconds => peer.Milliseconds;
        public void Begin() => peer.OpenBoot();
        public void RequireMainIdentity(uint pid, uint groups, uint kvm) => peer.RequireMainIdentity(pid, groups, kvm);
        public PipeIdentity Inspect(int slot) => peer.Inspect(slot);
        public void AdoptIntegrated() => peer.AdoptIntegrated();
        public int Read(int slot, Span<byte> destination) => peer.Read(slot, destination);
        public int Write(ReadOnlySpan<byte> bytes) => peer.Write(bytes);
        public void Poll(bool writing, int milliseconds) => peer.Poll(writing, milliseconds);
        public void Close(int slot) => peer.Close(slot);
        public void CloseAll() => peer.CloseAll();
        public void BeforePublication(NativeSourceOwnerResponseMessage response) => peer.BeforePublication(response);
    }
    private sealed class Pending : IDisposable
    {
        internal readonly NativeSourceOwnerOpcode Opcode;
        internal readonly KeyValuePair<string, string>[] Environment;
        internal readonly CancellationToken Caller;
        internal readonly TaskCompletionSource<NativeSourceOwnerResponseMessage> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        private int _decision;
        internal ulong Id; internal byte[]? Frame; internal int Written; internal long StartedAt;
        internal NativeSourceOwnerResponseMessage? Reply; internal Pending? Cleanup;
        internal NativeSourceSnapshotRequest? SnapshotRequest;
        internal NativeSourceObservationMessage? Observation;
        internal Pending? ReleaseOwner;
        internal Pending(NativeSourceOwnerOpcode opcode, KeyValuePair<string, string>[] environment, CancellationToken caller)
        {
            Opcode = opcode; Environment = environment; Caller = caller;
            _registration = caller.Register(() => Interlocked.CompareExchange(ref _decision, 1, 0));
        }
        internal bool Cancelled { get { if (Caller.IsCancellationRequested) Interrupt(); return Volatile.Read(ref _decision) == 1; } }
        internal void Interrupt() => Interlocked.CompareExchange(ref _decision, 1, 0);
        internal bool ChooseSuccess() => !Cancelled && Interlocked.CompareExchange(ref _decision, 2, 0) == 0;
        internal void Cancel() { Dispose(); Completion.TrySetCanceled(Caller); }
        public void Dispose() { _registration.Dispose(); Array.Clear(Environment); if (Frame is { } frame) { CryptographicOperations.ZeroMemory(frame); Frame = null; } }
    }
    private sealed class Actor
    {
        private const int IoBudget = 5000, TerminalBudget = 20000;
        private readonly object _gate = new(); private readonly IPipes _pipes; private readonly SourceStartupRequest _request; private readonly CancellationToken _startup;
        private readonly long _bootDeadline;
        private readonly Thread _thread; private readonly Task _joined;
        private readonly TaskCompletionSource<BootMessage> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<OwnerExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception?> _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Exception> _errors = [];
        private readonly SupportLogStore.NativeSourceCapture? _logs;
        private Pending? _ordinary, _control, _writing, _snapshot; private ulong _lastId; private bool _closing, _shutdownCommitted;
        private BootMessage? _stagedRetirement;
        private long? _terminalAt; private long _lastClock; private Exception? _finalError; private Task<Termination>? _shutdown;
        private NativeSourceOwnerResponseMessage? _lastResponse, _shutdownReply; private OwnerExit? _exitRecord, _exit;
        private readonly byte[] _response = new byte[NativeSourceOwnerProtocol.MaximumIncomingBytes], _reap = new byte[48];
        private int _responseSize, _reapSize, _responseWanted = NativeSourceOwnerProtocol.IncomingHeaderBytes;
        private long? _responseAt, _reapAt; private bool _responseEof, _reapEof, _responseClosed, _reapClosed, _discardResponse, _discardReap;
        internal BootMessage? Binding { get; private set; }
        private BootMessage RetirementContext => Binding ?? _stagedRetirement ?? throw Invalid();
        internal Task<BootMessage> Acceptance => _accepted.Task;
        internal Task<OwnerExit> OwnerExited => _exited.Task;
        internal Task<Exception?> RetirementStarted => _retired.Task;
        internal SupportLogStore.NativeSourceCapture.CaptureState? SourceOutputState => _logs?.Snapshot;
        internal void RequireMainForSnapshot()
        {
            var binding = Binding ?? throw new InvalidOperationException("Native boot acceptance has not completed.");
            if (binding.ExpectedOwner is null) throw new SourceEngineUnavailableException("No accepted native snapshot owner is available.");
            _pipes.RequireMainIdentity(binding.MainPid, binding.GroupCount, binding.KvmGid);
        }
        internal bool OwnsSnapshot(SnapshotLease lease)
        {
            lock (_gate) return !_closing && lease.MatchesAuthority(this)
                && _snapshot is { Observation: { } observation, Cleanup: null } pending
                && pending.Completion.Task.IsCompletedSuccessfully && ReferenceEquals(observation, lease.Observation);
        }
        internal void RequireDiagnosticPrivacy(SupportPrivacy privacy, bool allowMissing)
        {
            if (_logs is null) { if (!allowMissing) throw new InvalidOperationException("An actual source output capture is required."); }
            else _logs.RequirePrivacy(privacy);
        }
        internal Actor(IPipes pipes, SourceStartupRequest request, CancellationToken startup, SupportLogStore? diagnostics = null)
        {
            _pipes = pipes; _request = request; _startup = startup; _bootDeadline = pipes.Milliseconds + IoBudget;
            _logs = request.Mode == "integrated" ? diagnostics?.CaptureNativeSourceOutput() : null;
            _thread = new Thread(Run) { Name = "AutoSourcarr source-owner IO", IsBackground = false };
            try { _thread.Start(); }
            catch (Exception primary)
            {
                try { _logs?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception cleanup) { throw new AggregateException("Native IO startup and output cleanup failed", primary, cleanup); }
                throw;
            }
            _joined = JoinCoreAsync();
        }
        internal Task<NativeSourceOwnerResponseMessage> Call(NativeSourceOwnerOpcode opcode, IReadOnlyList<KeyValuePair<string, string>> environment, CancellationToken caller)
        {
            caller.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(environment);
            var count = environment.Count;
            if (count is < 0 or > NativeSourceOwnerProtocol.MaximumEnvironmentCount) throw Invalid();
            var snapshot = new KeyValuePair<string, string>[count];
            for (var index = 0; index < count; index++) snapshot[index] = environment[index];
            var binding = Binding ?? throw new InvalidOperationException("Native boot acceptance has not completed.");
            var validation = NativeSourceOwnerProtocol.EncodeCommand(Convert.FromHexString(binding.Nonce), 1, opcode, snapshot); CryptographicOperations.ZeroMemory(validation);
            var pending = new Pending(opcode, snapshot, caller);
            try
            {
                lock (_gate)
                {
                    if (_closing) throw new OperationCanceledException("Native source-owner admission is closed.");
                    if (binding.ExpectedOwner is null) throw new SourceEngineUnavailableException("External source mode has no native process owner.");
                    if (_snapshot is not null && opcode != NativeSourceOwnerOpcode.Status) throw new SourceWorkDeferredException("An owned snapshot must be reconciled before changing the source generation.");
                    if (_ordinary is not null || _control is not null) throw new SourceWorkDeferredException("The native source-owner command lane is busy; retry shortly.");
                    _ordinary = pending; return pending.Completion.Task;
                }
            }
            catch { pending.Dispose(); throw; }
        }
        internal Task<SnapshotLease> Observe(NativeSourceSnapshotRequest request, CancellationToken caller)
        {
            caller.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); request.RequireValid();
            var binding = Binding ?? throw new InvalidOperationException("Native boot acceptance has not completed.");
            var validation = NativeSourceObservationProtocol.EncodeObserve(Convert.FromHexString(binding.Nonce), 1, request); CryptographicOperations.ZeroMemory(validation);
            // Cancellation after admission belongs to the consumer transaction.
            // It must still receive the issued ticket and physically release it.
            var pending = new Pending(NativeSourceOwnerOpcode.Observe, [], CancellationToken.None) { SnapshotRequest = request };
            try
            {
                lock (_gate)
                {
                    caller.ThrowIfCancellationRequested();
                    if (_closing) throw new OperationCanceledException("Native source-owner admission is closed.");
                    if (binding.ExpectedOwner is null) throw new SourceEngineUnavailableException("External source mode has no native snapshot owner.");
                    if (_snapshot is not null || _ordinary is not null || _control is not null) throw new SourceWorkDeferredException("The native snapshot slot is busy; retry shortly.");
                    _snapshot = pending; return FinishObservationAsync(pending);
                }
            }
            catch { pending.Dispose(); throw; }
        }
        private async Task<SnapshotLease> FinishObservationAsync(Pending pending)
        {
            var response = await pending.Completion.Task.ConfigureAwait(false);
            if (response.Result is NativeSourceOwnerResult.Busy or NativeSourceOwnerResult.Unavailable)
                throw new SourceWorkDeferredException("The native snapshot prerequisite or worker capacity is unavailable; source execution was retained.");
            if (response.Result != NativeSourceOwnerResult.Ok || pending.Observation is not { } observation)
                throw new IOException("The native source snapshot request did not complete.");
            return new(this, response, observation, () => ReleaseSnapshot(pending));
        }
        private Task<NativeSourceOwnerResponseMessage> ReleaseSnapshot(Pending owner)
        {
            lock (_gate)
            {
                if (_closing) throw new OperationCanceledException("Native source-owner admission is closed.");
                if (!ReferenceEquals(_snapshot, owner) || owner.Observation is null || !owner.Completion.Task.IsCompletedSuccessfully)
                    throw new InvalidOperationException("The snapshot is not an issued lease of this native owner.");
                if (owner.Cleanup is { } active) return active.Completion.Task;
                // A status or reserved Stop may be in flight. Cleanup gets its own
                // bounded pending request and cannot be abandoned on ordinary busy.
                var cleanup = new Pending(NativeSourceOwnerOpcode.ReleaseSnapshot, [], CancellationToken.None) { ReleaseOwner = owner };
                owner.Cleanup = cleanup; return cleanup.Completion.Task;
            }
        }
        internal Task<NativeSourceOwnerResponseMessage> Stop()
        {
            lock (_gate)
            {
                if (_closing) throw new OperationCanceledException("Native source-owner admission is closed.");
                if (Binding?.ExpectedOwner is null) throw new SourceEngineUnavailableException("No accepted native source owner is available.");
                // Stop is a barrier for an earlier admitted START, including the
                // window before any frame byte commits. A committed START still
                // joins the reserved STOP facts before its cancellation completes.
                if (_ordinary is { Opcode: NativeSourceOwnerOpcode.Start } start) start.Interrupt();
                return (_control ??= new(NativeSourceOwnerOpcode.Stop, [], CancellationToken.None)).Completion.Task;
            }
        }
        internal Task<Termination> ShutdownAsync()
        {
            lock (_gate)
            {
                if (_shutdown is not null) return _shutdown;
                _closing = true; _retired.TrySetResult(null);
                return _shutdown = FinishShutdownAsync();
            }
        }
        internal Task CloseAndJoinAsync()
        {
            lock (_gate) { _closing = true; _retired.TrySetResult(null); }
            return FinishCloseAsync();
        }
        private async Task FinishCloseAsync()
        { await _joined.ConfigureAwait(false); if (_finalError is { } error) ExceptionDispatchInfo.Capture(error).Throw(); }
        private async Task<Termination> FinishShutdownAsync()
        {
            await _joined.ConfigureAwait(false);
            if (_finalError is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            if (Binding?.ExpectedOwner is null) throw new SourceEngineUnavailableException("External mode has no native-owner termination observation.");
            return new(_shutdownReply!, _exit!);
        }
        private long Now()
        {
            var now = _pipes.Milliseconds;
            if (now < _lastClock) throw new IOException("Native pipe monotonic clock moved backwards.");
            return _lastClock = now;
        }
        private void Run()
        {
            try
            {
                var deadline = _bootDeadline;
                if (Now() >= deadline) throw new TimeoutException("Native boot acceptance timed out before its IO thread started.");
                _pipes.Begin(); var header = ReadExact(6, 192, deadline);
                try
                {
                    var raw = U32(header, 96); var proof = U32(header, 100);
                    if (raw is 0 or > SourceStartupRequest.MaximumFrameBytes || proof is not (0 or 76)) throw Invalid();
                    var rest = ReadExact(6, checked((int)(raw + proof)), deadline); var frame = new byte[192 + rest.Length];
                    try
                    {
                        header.CopyTo(frame, 0); rest.CopyTo(frame, 192); var message = ParseBootMessage(frame);
                        RequireEof(6, deadline); message.StartupRequest.RequireSameRequest(_request); RequireResolvedIdentity(message);
                        _pipes.RequireMainIdentity(message.MainPid, message.GroupCount, message.KvmGid);
                        if (_pipes.Inspect(6) != message.Pipes[3]) throw Invalid();
                        if (message.ExpectedOwner is not null)
                        {
                            foreach (var slot in new[] { 3, 4, 5, 7 }) if (_pipes.Inspect(slot) != message.Pipes[slot - 3]) throw Invalid();
                            _pipes.AdoptIntegrated();
                            // This private context grants retirement ownership only.
                            // Actual proof/EOF and Acceptance remain incomplete.
                            _stagedRetirement = message;
                            var actual = ReadExact(5, 76, deadline);
                            try { NativeSourceOwnerProtocol.DecodeProof(actual, Convert.FromHexString(message.Nonce), message.ExpectedOwner); RequireEof(5, deadline); }
                            finally { CryptographicOperations.ZeroMemory(actual); }
                            _pipes.Close(5);
                        }
                        _pipes.Close(6); Binding = message;
                    }
                    finally { CryptographicOperations.ZeroMemory(rest); CryptographicOperations.ZeroMemory(frame); }
                }
                finally { CryptographicOperations.ZeroMemory(header); }
                if (Binding.ExpectedOwner is null) return;
                // Once every bound channel/proof/EOF is accepted, factory
                // cancellation must take the actual SHUTDOWN/reap path below.
                _accepted.TrySetResult(Binding);
                RunOwnedLoop();
            }
            catch (Exception error)
            {
                if (_stagedRetirement is null) { _errors.Add(error); _retired.TrySetResult(error); }
                else
                {
                    Fault(error); // close the sole command writer; do not send commands before actual proof
                    foreach (var slot in new[] { 5, 6 }) try { _pipes.Close(slot); } catch (Exception cleanup) { _errors.Add(cleanup); }
                    try { RunOwnedLoop(); } catch (Exception cleanup) { _errors.Add(cleanup); }
                }
            }
            finally
            {
                try { _pipes.CloseAll(); } catch (Exception error) { _errors.Add(error); }
                CryptographicOperations.ZeroMemory(_response); CryptographicOperations.ZeroMemory(_reap); _stopped.TrySetResult();
            }
        }
        private void RunOwnedLoop()
        {
            while (true)
            {
                try { Advance(); PumpResponse(); PumpReap(); Advance(); }
                catch (Exception error) { Fault(error); }
                if (_errors.Count != 0 && !_responseClosed && !_responseEof) CloseCommand();
                var now = Now();
                lock (_gate) if (_closing && _terminalAt is null) _terminalAt = now + TerminalBudget;
                if (_closing && (_responseEof || _responseClosed) && (_reapEof || _reapClosed)) break;
                if (_terminalAt is { } expires && now >= expires) { Fault(new TimeoutException("Native retirement is unresolved after its terminal reconciliation budget.")); break; }
                try { _pipes.Poll(_writing is not null, 100); } catch (Exception error) { Fault(error); CloseCommand(); }
            }
        }
        private byte[] ReadExact(int slot, int count, long deadline)
        {
            var bytes = new byte[count]; var at = 0;
            try
            {
                while (at < bytes.Length)
                {
                    _startup.ThrowIfCancellationRequested(); if (Now() >= deadline) throw new TimeoutException("Native boot acceptance timed out.");
                    var read = _pipes.Read(slot, bytes.AsSpan(at)); if (read == 0) throw Invalid();
                    if (read > 0) at += read; else _pipes.Poll(false, 100);
                }
                return bytes;
            }
            catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        }
        private void RequireEof(int slot, long deadline)
        {
            Span<byte> extra = stackalloc byte[1];
            while (true)
            {
                _startup.ThrowIfCancellationRequested(); if (Now() >= deadline) throw new TimeoutException("Native boot EOF was not observed.");
                var read = _pipes.Read(slot, extra); if (read == 0) return; if (read > 0) throw Invalid(); _pipes.Poll(false, 100);
            }
        }
        private void Fault(Exception error)
        {
            if (!_errors.Contains(error)) _errors.Add(error);
            lock (_gate) _closing = true;
            _retired.TrySetResult(error); CloseCommand();
        }
        private void CloseCommand()
        {
            try { _pipes.Close(3); } catch (Exception error) { if (!_errors.Contains(error)) _errors.Add(error); }
            _writing?.Dispose(); _writing = null;
        }
        private void Advance()
        {
            lock (_gate)
            {
                if (_errors.Count != 0) return;
                // A native owner which never reconciles a committed request cannot
                // hold the admin lane forever. Expiry is fatal uncertainty, never
                // proof that its process or descendants stopped.
                if ((_control is { Id: > 0, Reply: null } control && Now() - control.StartedAt >= TerminalBudget)
                    || (_ordinary is { Id: > 0, Reply: null } waiting && Now() - waiting.StartedAt >= TerminalBudget))
                { _terminalAt = Now(); throw new TimeoutException("Native command reconciliation is unresolved after its owned budget."); }
                var ordinary = _ordinary;
                if (_closing) ordinary?.Interrupt();
                if (ordinary is not null && ordinary.Cancelled)
                {
                    if (ordinary.Id == 0) { _ordinary = null; ordinary.Cancel(); ordinary = null; }
                    else if (ordinary.Opcode == NativeSourceOwnerOpcode.Start && ordinary.Cleanup is null)
                    {
                        if (_control is null && !_closing) _control = new(NativeSourceOwnerOpcode.Stop, [], CancellationToken.None);
                        ordinary.Cleanup = _control;
                    }
                }
                if (_closing && _control is null && !_shutdownCommitted) _control = new(NativeSourceOwnerOpcode.Shutdown, [], CancellationToken.None);
                if (ordinary is { Cancelled: true, Opcode: NativeSourceOwnerOpcode.Start, Cleanup: null } && _control is not null) ordinary.Cleanup = _control;
                if (ordinary?.Reply is not null)
                {
                    if (ordinary.Cancelled)
                    {
                        if (ordinary.Opcode != NativeSourceOwnerOpcode.Start || ordinary.Cleanup?.Completion.Task.IsCompletedSuccessfully == true)
                        { _ordinary = null; ordinary.Cancel(); }
                    }
                    else
                    {
                        _pipes.BeforePublication(ordinary.Reply);
                        if (ordinary.ChooseSuccess()) { _ordinary = null; ordinary.Dispose(); ordinary.Completion.TrySetResult(ordinary.Reply); }
                    }
                }
                AdvanceSnapshot();
                if (_writing is null)
                {
                    var next = _control is { Id: 0, Opcode: not NativeSourceOwnerOpcode.Shutdown } ? _control
                        : _snapshot?.Cleanup is { Id: 0 } release ? release
                        : _control is { Id: 0 } && _snapshot is null ? _control
                        : _snapshot is { Id: 0 } snapshot ? snapshot
                        : _ordinary is { Id: 0 } ? _ordinary : null;
                    if (next is not null)
                    {
                        next.Id = _lastId = NativeSourceOwnerProtocol.NextRequestId(_lastId); next.StartedAt = Now();
                        next.Frame = EncodePending(next);
                        _writing = next; if (next.Opcode == NativeSourceOwnerOpcode.Shutdown) _shutdownCommitted = true;
                    }
                }
                if (_writing is { } writing)
                {
                    if (Now() - writing.StartedAt >= IoBudget) throw new TimeoutException("Native command frame write timed out; it will not be retried.");
                    var frame = writing.Frame ?? throw Invalid();
                    var wrote = _pipes.Write(frame.AsSpan(writing.Written));
                    if (wrote == 0) throw new IOException("Native command writer made no progress.");
                    if (wrote > 0)
                    {
                        writing.Written += wrote;
                        if (writing.Written == frame.Length) { CryptographicOperations.ZeroMemory(frame); writing.Frame = null; _writing = null; }
                    }
                }
            }
        }
        private byte[] EncodePending(Pending pending)
        {
            var nonce = Convert.FromHexString(Binding!.Nonce);
            try
            {
                if (pending.Opcode == NativeSourceOwnerOpcode.Observe)
                    return NativeSourceObservationProtocol.EncodeObserve(nonce, pending.Id, pending.SnapshotRequest ?? throw Invalid());
                if (pending.Opcode == NativeSourceOwnerOpcode.ReleaseSnapshot)
                    return NativeSourceObservationProtocol.EncodeRelease(nonce, pending.Id, pending.ReleaseOwner?.Observation?.Ticket ?? throw Invalid());
                return NativeSourceOwnerProtocol.EncodeCommand(nonce, pending.Id, pending.Opcode, pending.Environment);
            }
            finally { CryptographicOperations.ZeroMemory(nonce); }
        }
        private void AdvanceSnapshot()
        {
            if (_snapshot is not { } snapshot) return;
            if (_closing && snapshot.Id == 0)
            { _snapshot = null; snapshot.Interrupt(); snapshot.Cancel(); return; }
            if (snapshot.Reply is { } reply && !snapshot.Completion.Task.IsCompleted)
            {
                _pipes.BeforePublication(reply);
                if (snapshot.ChooseSuccess())
                {
                    if (reply.Result != NativeSourceOwnerResult.Ok) _snapshot = null;
                    snapshot.Dispose(); snapshot.Completion.TrySetResult(reply);
                }
            }
            if (snapshot.Cleanup is { Reply: { } released } cleanup)
            {
                snapshot.Cleanup = null;
                if (released.Result == NativeSourceOwnerResult.Ok) _snapshot = null;
                cleanup.Dispose(); cleanup.Completion.TrySetResult(released);
                if (_closing && released.Result != NativeSourceOwnerResult.Ok)
                    throw new IOException("Native snapshot cleanup failed during owned shutdown.");
            }
            if (_closing && ReferenceEquals(_snapshot, snapshot) && snapshot.Reply?.Result == NativeSourceOwnerResult.Ok && snapshot.Cleanup is null)
                snapshot.Cleanup = new(NativeSourceOwnerOpcode.ReleaseSnapshot, [], CancellationToken.None) { ReleaseOwner = snapshot };
        }
        private void PumpResponse()
        {
            if (_responseClosed || _responseEof) return;
            try
            {
                for (var round = 0; round < 8; round++)
                {
                    var wanted = _discardResponse ? _response.Length : _responseWanted;
                    var read = _pipes.Read(4, _response.AsSpan(_responseSize, wanted - _responseSize));
                    if (read < 0) break;
                    if (read == 0)
                    {
                        _responseEof = true; _pipes.Close(4);
                        if (_responseSize != 0 || (!_shutdownCommitted && _errors.Count == 0)) Fault(new IOException("Native response EOF was incomplete or unexpected.")); return;
                    }
                    _responseAt ??= Now(); _responseSize += read;
                    if (!_discardResponse && _responseSize == NativeSourceOwnerProtocol.IncomingHeaderBytes
                        && _responseWanted == NativeSourceOwnerProtocol.IncomingHeaderBytes)
                    {
                        try { _responseWanted = NativeSourceOwnerProtocol.IncomingFrameBytes(_response.AsSpan(0, _responseSize), Convert.FromHexString(RetirementContext.Nonce)); }
                        catch (Exception error) { _discardResponse = true; Fault(error); }
                    }
                    if (_responseSize == (_discardResponse ? _response.Length : _responseWanted))
                    {
                        if (!_discardResponse)
                        {
                            try
                            {
                                var frame = _response.AsSpan(0, _responseWanted);
                                if (NativeSourceOwnerProtocol.IsLogFrame(frame))
                                {
                                    if (Binding?.ExpectedOwner is null || _logs is null) throw Invalid();
                                    var header = NativeSourceOwnerProtocol.DecodeLogHeader(frame[..NativeSourceOwnerProtocol.IncomingHeaderBytes], Convert.FromHexString(Binding.Nonce));
                                    // Only bounded copying. Redaction/disk/consumer waits never run on this IO thread.
                                    _ = _logs.Write(header.Generation, header.RawOffset, frame[NativeSourceOwnerProtocol.IncomingHeaderBytes..], header.Gap);
                                }
                                else if (NativeSourceObservationProtocol.IsEvent(frame)) ReceiveObservation(frame);
                                else ReceiveResponse(frame);
                            }
                            catch (Exception error) { _discardResponse = true; Fault(error); }
                        }
                        CryptographicOperations.ZeroMemory(_response); _responseSize = 0; _responseAt = null;
                        _responseWanted = NativeSourceOwnerProtocol.IncomingHeaderBytes;
                    }
                }
                if (_responseAt is { } at && Now() - at >= IoBudget) throw new TimeoutException("Native partial response timed out.");
            }
            catch (Exception error) { _responseClosed = true; try { _pipes.Close(4); } catch (Exception cleanup) { _errors.Add(cleanup); } Fault(error); }
        }
        private void ReceiveResponse(ReadOnlySpan<byte> frame)
        {
            var id = U64(frame, 40);
            lock (_gate)
            {
                var pending = _ordinary?.Id == id && id != 0 ? _ordinary : _control?.Id == id && id != 0 ? _control
                    : _snapshot?.Id == id && id != 0 ? _snapshot : _snapshot?.Cleanup?.Id == id && id != 0 ? _snapshot.Cleanup : null;
                if (pending is null || pending.Reply is not null || pending.Frame is not null) throw Invalid();
                var context = RetirementContext;
                var response = NativeSourceOwnerProtocol.DecodeResponse(frame, Convert.FromHexString(context.Nonce), id, pending.Opcode, context.ExpectedOwner!.Root);
                if (pending.Opcode == NativeSourceOwnerOpcode.Observe
                    && (response.Result == NativeSourceOwnerResult.Ok) != (pending.Observation is not null)) throw Invalid();
                _logs?.ObserveNativeResponse(response.Generation, response.DiscardedLogBytes,
                    (response.Facts & NativeSourceOwnerFacts.StreamsDrained) != 0);
                pending.Reply = response; _lastResponse = response;
                if (pending.Opcode is NativeSourceOwnerOpcode.Stop or NativeSourceOwnerOpcode.Shutdown)
                {
                    if (!CleanStop(response)) throw new IOException("Native stop returned incomplete or failed physical facts.");
                    if (pending.Opcode == NativeSourceOwnerOpcode.Shutdown) _shutdownReply = response;
                    else { _control = null; pending.Dispose(); pending.Completion.TrySetResult(response); }
                }
            }
        }
        private void ReceiveObservation(ReadOnlySpan<byte> frame)
        {
            lock (_gate)
            {
                var id = U64(frame, 40); var pending = _snapshot;
                if (id == 0 || pending is null || pending.Id != id || pending.Frame is not null
                    || pending.Reply is not null || pending.Observation is not null || pending.SnapshotRequest is null) throw Invalid();
                var context = RetirementContext;
                pending.Observation = NativeSourceObservationProtocol.DecodeEvent(frame, Convert.FromHexString(context.Nonce), id,
                    pending.SnapshotRequest, context.ExpectedOwner ?? throw Invalid());
            }
        }
        private void PumpReap()
        {
            if (_reapClosed || _reapEof) return;
            try
            {
                Span<byte> extra = stackalloc byte[1];
                for (var round = 0; round < 8; round++)
                {
                    var destination = _reapSize == 48 ? extra : _reap.AsSpan(_reapSize);
                    var read = _pipes.Read(7, destination); if (read < 0) break;
                    if (read == 0)
                    {
                        _reapEof = true; _pipes.Close(7);
                        if (_reapSize != 48 || _discardReap || _exitRecord is null) { Fault(new IOException("Native reap notification was absent or incomplete.")); return; }
                        _exit = _exitRecord; _exited.TrySetResult(_exit);
                        if (!_closing || _exit.WaitStatus != 0) Fault(new IOException("Native owner exited unexpectedly or unsuccessfully.")); return;
                    }
                    _reapAt ??= Now();
                    if (_reapSize == 48) { _discardReap = true; throw Invalid(); }
                    _reapSize += read;
                    if (_reapSize == 48)
                    { var context = RetirementContext; _exitRecord = ParseReapMessage(_reap, context.Nonce, context.ExpectedOwner!.OwnerPid); }
                }
                if (_reapAt is { } at && Now() - at >= IoBudget) throw new TimeoutException("Native partial reap notification timed out.");
            }
            catch (Exception error) { _reapClosed = true; try { _pipes.Close(7); } catch (Exception cleanup) { _errors.Add(cleanup); } Fault(error); }
        }
        private static bool CleanStop(NativeSourceOwnerResponseMessage response) => response.Result == NativeSourceOwnerResult.Ok && response.State == NativeSourceOwnerState.Idle
            && response.Diagnostic == NativeSourceOwnerDiagnostic.None
            && (response.Facts & (NativeSourceOwnerFacts.NoChildren | NativeSourceOwnerFacts.StreamsDrained)) == (NativeSourceOwnerFacts.NoChildren | NativeSourceOwnerFacts.StreamsDrained)
            && (response.Facts & (NativeSourceOwnerFacts.Failed | NativeSourceOwnerFacts.PhysicalTest)) == 0
            && (response.LeaderPid == 0 || (response.Facts & NativeSourceOwnerFacts.Exit) != 0);
        private async Task JoinCoreAsync()
        {
            await _stopped.Task.ConfigureAwait(false);
            while (true)
            {
                try { _thread.Join(); break; }
                catch (ThreadInterruptedException error) { _errors.Add(error); } // retain interruption, still join the physical thread
            }
            if (Binding?.ExpectedOwner is not null && _errors.Count == 0 && (_shutdownReply is null || !_responseEof || !_reapEof || _exit?.WaitStatus != 0))
                _errors.Add(new IOException("Native terminal retirement lacks its exact response, EOF and reap observations."));
            if (_logs is not null) try { await _logs.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { _errors.Add(error); }
            if (_errors.Count != 0)
            {
                Exception inner = _errors.Count == 1 ? _errors[0] : new AggregateException("Native owner and cleanup errors", _errors);
                _finalError = Binding is null && _stagedRetirement is null ? inner : new LifetimeFailure(inner, _lastResponse, _exitRecord);
            }
            if (_finalError is not null)
            {
                _accepted.TrySetException(_finalError); _exited.TrySetException(_finalError); _ = _exited.Task.Exception;
                _retired.TrySetResult(_finalError);
            }
            else { _accepted.TrySetResult(Binding!); _retired.TrySetResult(null); }
            Pending? ordinary, control, snapshot;
            lock (_gate) { ordinary = _ordinary; control = _control; snapshot = _snapshot; _ordinary = null; _control = null; _snapshot = null; _closing = true; }
            foreach (var pending in new[] { control, ordinary, snapshot?.Cleanup, snapshot }.OfType<Pending>())
            {
                pending.Dispose();
                if (_finalError is { } error)
                {
                    if (pending.Cancelled) pending.Completion.TrySetException(new AggregateException(new OperationCanceledException(pending.Caller), error));
                    else pending.Completion.TrySetException(error);
                    _ = pending.Completion.Task.Exception;
                }
                else if (pending.Cancelled) pending.Completion.TrySetCanceled(pending.Caller);
                else if (pending.Reply is { } reply) pending.Completion.TrySetResult(reply);
                else pending.Completion.TrySetCanceled();
            }
        }
    }

    private sealed unsafe partial class LinuxPipes : IPipes
    {
        private readonly Dictionary<int, int> _owned = []; private readonly HashSet<int> _originals = [];
        public long Milliseconds => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
        public static void RequirePostExecPrivacy()
        {
            if (Prctl(4, 0, 0, 0, 0) != 0 || Prctl(3, 0, 0, 0, 0) != 0)
                throw Io("Native final-main process privacy was not established");
            uint group = 0; var count = GetGroups(0, null);
            if (count is < 0 or > 1 || (count == 1 && GetGroups(1, &group) != 1))
                throw Io("Cannot inspect final-main startup groups");
            // Reject root/capable/foreign-parent CLR invocations before reading
            // configuration. The accepted boot later binds this actual group
            // to the native parent's exact optional KVM-group receipt.
            new LinuxPipes().RequireMainIdentity(GetPid(), (uint)count, group);
        }
        public void Begin()
        {
            uint group = 0; var count = GetGroups(0, null);
            if (count is < 0 or > 1 || (count == 1 && GetGroups(1, &group) != 1)) throw Io("Cannot inspect final-main supplementary groups");
            RequireMainIdentity(GetPid(), (uint)count, group);
            if (SigEmptySet(out var blocked) != 0 || SigAddSet(ref blocked, 13) != 0) throw Io("Cannot prepare the owned IO thread signal mask");
            var result = SignalMask(0, ref blocked, out _);
            if (result != 0) throw new IOException("Cannot block SIGPIPE on the owned IO thread (thread error " + result + ").");
            result = CurrentMask(0, IntPtr.Zero, out var actual);
            if (result != 0 || SigIsMember(ref actual, 13) != 1) throw new IOException("Cannot verify the owned IO thread SIGPIPE mask.");
            _originals.Add(6); _ = InspectFd(6, 0); Duplicate(6); CloseOriginal(6);
        }
        public void RequireMainIdentity(uint pid, uint groups, uint kvm)
        {
            if (GetPid() != pid || GetPpid() != 1 || GetResUid(out var r, out var e, out var s) != 0 || r != 568 || e != 568 || s != 568
                || GetResGid(out var gr, out var ge, out var gs) != 0 || gr != 568 || ge != 568 || gs != 568
                || unchecked((uint)SetFsUid(uint.MaxValue)) != 568 || unchecked((uint)SetFsGid(uint.MaxValue)) != 568
                || Prctl(39, 0, 0, 0, 0) != 1 || Prctl(3, 0, 0, 0, 0) != 0
                || GetGroups(0, null) != groups) throw Io("Native final-main identity does not match the handoff");
            uint group = 0;
            if (groups == 1 && (GetGroups(1, &group) != 1 || group != kvm)) throw Io("Native final-main supplementary group differs");
            var header = new CapabilityHeader { Version = 0x20080522 };
            if (CapGet(ref header, out var caps) != 0 || caps.Effective0 != 0 || caps.Permitted0 != 0 || caps.Inheritable0 != 0
                || caps.Effective1 != 0 || caps.Permitted1 != 0 || caps.Inheritable1 != 0) throw Io("Native final-main capabilities were not cleared");
            var ended = false;
            for (uint capability = 0; capability < 256; capability++)
            {
                var value = Prctl(23, capability, 0, 0, 0);
                if (value < 0 && Marshal.GetLastPInvokeError() == 22) { ended = capability != 0; break; }
                // Bounding bits are the inherited ceiling, not active privilege.
                // Emptying them would require an extra CAP_SETPCAP prerequisite.
                if (value is not (0 or 1) || Prctl(47, 1, capability, 0, 0) != 0) throw Io("Cannot verify native final-main ambient capabilities");
            }
            if (!ended) throw Io("Cannot bound the native capability check");
        }
        public PipeIdentity Inspect(int slot) => InspectFd(_owned.GetValueOrDefault(slot, slot), slot == 3 ? 1 : 0);
        public void AdoptIntegrated()
        {
            foreach (var slot in new[] { 3, 4, 5, 7 }) _originals.Add(slot);
            foreach (var slot in new[] { 3, 4, 5, 7 }) Duplicate(slot);
            foreach (var slot in new[] { 3, 4, 5, 7 }) CloseOriginal(slot);
        }
        private void Duplicate(int slot)
        {
            var before = InspectFd(slot, slot == 3 ? 1 : 0); var duplicate = Fcntl(slot, 1030, 8);
            if (duplicate < 0) throw Io("Cannot duplicate an inherited native pipe");
            _owned.Add(slot, duplicate);
            if (InspectFd(duplicate, before.Access) != before) throw Io("Inherited native pipe changed during duplication");
            var flags = Fcntl(duplicate, 3, 0);
            if (flags < 0 || Fcntl(duplicate, 4, flags | 0x800) != 0) throw Io("Cannot make an owned pipe nonblocking");
        }
        private static PipeIdentity InspectFd(int fd, int access)
        {
            var flags = Fcntl(fd, 3, 0);
            if (flags < 0 || (flags & 3) != access || Statx(fd, "", 0x1000, 0x7ff, out var stat) != 0
                || (stat.Mask & 0x101) != 0x101 || (stat.Mode & 0xf000) != 0x1000 || stat.Inode == 0) throw Io("Inherited descriptor is not the required pipe");
            var dev = (stat.DeviceMinor & 0xffUL) | ((stat.DeviceMajor & 0xfffUL) << 8) | ((stat.DeviceMinor & ~0xffUL) << 12) | ((stat.DeviceMajor & ~0xfffUL) << 32);
            return new(dev, stat.Inode, access);
        }
        public int Read(int slot, Span<byte> destination)
        {
            if (!_owned.TryGetValue(slot, out var fd)) throw Io("Native pipe is no longer owned");
            fixed (byte* bytes = destination) return Result(ReadFd(fd, bytes, (nuint)destination.Length));
        }
        public int Write(ReadOnlySpan<byte> bytes)
        {
            if (!_owned.TryGetValue(3, out var fd)) throw Io("Native command pipe is closed");
            fixed (byte* data = bytes) return Result(WriteFd(fd, data, (nuint)bytes.Length));
        }
        private static int Result(nint count)
        {
            if (count >= 0) return checked((int)count);
            if (Marshal.GetLastPInvokeError() is 4 or 11) return -1;
            throw Io("Owned native pipe IO failed");
        }
        public void Poll(bool writing, int milliseconds)
        {
            var waits = stackalloc PollFd[5]; var count = 0;
            foreach (var (slot, fd) in _owned) if (slot != 3 || writing) waits[count++] = new() { Descriptor = fd, Events = (short)(slot == 3 ? 4 : 1) };
            if (PollFdSet(waits, (nuint)count, milliseconds) < 0 && Marshal.GetLastPInvokeError() != 4) throw Io("Owned native pipe poll failed");
            for (var index = 0; index < count; index++) if ((waits[index].Returned & 32) != 0) throw Io("Owned native pipe became invalid");
        }
        public void Close(int slot) { if (_owned.Remove(slot, out var fd)) CloseFdOnce(fd); }
        private void CloseOriginal(int slot) { if (_originals.Remove(slot)) CloseFdOnce(slot); }
        private static void CloseFdOnce(int fd) { if (CloseFd(fd) != 0) throw Io("Native pipe close failed; it will not be retried"); }
        public void CloseAll()
        {
            var errors = new List<Exception>();
            foreach (var slot in _originals.OrderBy(slot => slot == 3 ? 0 : 1).ToArray()) try { CloseOriginal(slot); } catch (Exception error) { errors.Add(error); }
            foreach (var slot in _owned.Keys.OrderBy(slot => slot == 3 ? 0 : 1).ToArray()) try { Close(slot); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("Owned native pipe cleanup failed", errors);
        }
        public void BeforePublication(NativeSourceOwnerResponseMessage response) { }
        private static IOException Io(string message) => new(message + " (errno " + Marshal.GetLastPInvokeError() + ").");
        [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Descriptor; public short Events, Returned; }
        [StructLayout(LayoutKind.Explicit, Size = 256)] private struct Stat
        { [FieldOffset(0)] public uint Mask; [FieldOffset(28)] public ushort Mode; [FieldOffset(32)] public ulong Inode; [FieldOffset(136)] public uint DeviceMajor; [FieldOffset(140)] public uint DeviceMinor; }
        [StructLayout(LayoutKind.Sequential)] private struct CapabilityHeader { public uint Version; public int Pid; }
        [StructLayout(LayoutKind.Sequential)] private struct Capabilities { public uint Effective0, Permitted0, Inheritable0, Effective1, Permitted1, Inheritable1; }
        [StructLayout(LayoutKind.Sequential, Size = 128)] private struct SignalSet { public fixed byte Bytes[128]; }
        [LibraryImport("libc", EntryPoint = "read", SetLastError = true)] private static partial nint ReadFd(int fd, byte* bytes, nuint length);
        [LibraryImport("libc", EntryPoint = "write", SetLastError = true)] private static partial nint WriteFd(int fd, byte* bytes, nuint length);
        [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)] private static partial int PollFdSet(PollFd* fds, nuint count, int milliseconds);
        [LibraryImport("libc", EntryPoint = "close", SetLastError = true)] private static partial int CloseFd(int fd);
        [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static partial int Fcntl(int fd, int command, int argument);
        [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int Statx(int fd, string path, int flags, uint mask, out Stat status);
        [LibraryImport("libc", EntryPoint = "getpid")] private static partial uint GetPid();
        [LibraryImport("libc", EntryPoint = "getppid")] private static partial uint GetPpid();
        [LibraryImport("libc", EntryPoint = "getresuid")] private static partial int GetResUid(out uint real, out uint effective, out uint saved);
        [LibraryImport("libc", EntryPoint = "getresgid")] private static partial int GetResGid(out uint real, out uint effective, out uint saved);
        [LibraryImport("libc", EntryPoint = "setfsuid")] private static partial int SetFsUid(uint uid);
        [LibraryImport("libc", EntryPoint = "setfsgid")] private static partial int SetFsGid(uint gid);
        [LibraryImport("libc", EntryPoint = "getgroups")] private static partial int GetGroups(int count, uint* groups);
        [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)] private static partial int Prctl(int option, nuint a, nuint b, nuint c, nuint d);
        [LibraryImport("libc", EntryPoint = "capget")] private static partial int CapGet(ref CapabilityHeader header, out Capabilities capabilities);
        [LibraryImport("libc", EntryPoint = "sigemptyset", SetLastError = true)] private static partial int SigEmptySet(out SignalSet set);
        [LibraryImport("libc", EntryPoint = "sigaddset", SetLastError = true)] private static partial int SigAddSet(ref SignalSet set, int signal);
        [LibraryImport("libc", EntryPoint = "sigismember", SetLastError = true)] private static partial int SigIsMember(ref SignalSet set, int signal);
        [LibraryImport("libc", EntryPoint = "pthread_sigmask")] private static partial int SignalMask(int how, ref SignalSet set, out SignalSet previous);
        [LibraryImport("libc", EntryPoint = "pthread_sigmask")] private static partial int CurrentMask(int how, IntPtr set, out SignalSet previous);
    }
}
