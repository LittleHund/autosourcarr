using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Sources.Inventory;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Sources.Runtime;

/// <summary>One per-instance transaction owns capture, off-actor file reading and
/// native cleanup. Neither cancellation nor file-I/O refusal abandons a ticket.</summary>
internal sealed class NativeSourceSnapshots : IInstalledSourceBinaryReader, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Channel _channel;
    private readonly SourceSnapshotFiles _files;
    private readonly BoundedWorkScheduler _io;
    private Operation? _active;
    private NativeSourceOwnerConnection.SnapshotLease? _residual;
    private int _pauses;
    private bool _stopping;
    private Task? _stop;

    private sealed class Operation(CancellationToken caller)
    {
        internal readonly CancellationTokenSource Cancel = CancellationTokenSource.CreateLinkedTokenSource(caller);
        internal Task Drain = Task.CompletedTask;
        internal Exception? Failure;
    }
    private sealed record Channel(NativeSourceOwnerProofMessage Owner,
        Func<NativeSourceSnapshotRequest, CancellationToken, Task<NativeSourceOwnerConnection.SnapshotLease>> Observe,
        Func<NativeSourceOwnerConnection.SnapshotLease, bool> Owns);
    private NativeSourceSnapshots(Channel channel, SourceSnapshotFiles files, BoundedWorkScheduler io)
    { _channel = channel; _files = files; _io = io; }

    internal static async Task<NativeSourceSnapshots> CreateAsync(NativeSourceOwnerConnection connection, BoundedWorkScheduler io,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(io);
        SourceSnapshotFiles? physical = null;
        try
        {
            var files = await io.RunAsync(() => physical = SourceSnapshotFiles.OpenFixed(connection), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new NativeSourceSnapshots(new(connection.OwnerMessage, connection.ObserveAsync, connection.OwnsSnapshot), files, io);
            connection.AttachSnapshots(result); return result;
        }
        catch (Exception primary)
        {
            if (physical is not null)
                try { await Task.Run(physical.Dispose).ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException("Snapshot receiver startup and close both failed.", primary, cleanup); }
            throw;
        }
    }
    /// <summary>Controlled channel/file seam; never creates a production native connection or fixed Linux root.</summary>
    internal static NativeSourceSnapshots Controlled(NativeSourceOwnerConnection.ControlledHarness harness,
        NativeSourceOwnerProofMessage expectedOwner, SourceSnapshotFiles syntheticFiles, BoundedWorkScheduler io)
    {
        ArgumentNullException.ThrowIfNull(harness); ArgumentNullException.ThrowIfNull(syntheticFiles); ArgumentNullException.ThrowIfNull(io);
        return new(new(expectedOwner, harness.ObserveAsync, harness.OwnsSnapshot), syntheticFiles, io);
    }
    public async Task<byte[]> ReadInstalledJarAsync(string leaf, long expectedGeneration, CancellationToken cancellationToken)
    {
        SourceOwnedObservation.RequireInstalledJarLeaf(leaf);
        if (expectedGeneration <= 0) throw new SourceWorkDeferredException("A current ready source generation is required for installed bytes.");
        return await ReadAsync(new((ulong)expectedGeneration, NativeSourceSnapshotKind.InstalledJar, leaf), cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The installed JAR snapshot is absent.");
    }
    internal Task<byte[]?> ReadRetainedUserAgentAsync(ulong generation, CancellationToken cancellationToken = default)
        => ReadAsync(new(generation, NativeSourceSnapshotKind.RetainedUserAgent, ""), cancellationToken);

    private Task<byte[]?> ReadAsync(NativeSourceSnapshotRequest request, CancellationToken caller)
    {
        request.RequireValid(); caller.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopping) throw new OperationCanceledException("Native snapshot admission is closed.");
            if (_pauses != 0 || _active is { Drain.IsCompleted: false }) throw new SourceWorkDeferredException("The native snapshot transaction is busy or paused.");
            _active?.Cancel.Dispose(); // Its actual drain is complete; no cancellation race.
            var operation = new Operation(caller); _active = operation;
            var result = RunAsync(operation, request);
            operation.Drain = DrainAsync(operation, result); return result;
        }
    }
    private static async Task DrainAsync(Operation operation, Task<byte[]?> work)
    {
        try { _ = await work.ConfigureAwait(false); }
        catch (Exception failure) { operation.Failure = failure; }
        finally { work = null!; }
    }
    private async Task<byte[]?> RunAsync(Operation operation, NativeSourceSnapshotRequest request)
    {
        // Assignment of the actual drain task completes under the admission lock
        // before this continuation can begin. Pause always joins that real task.
        await Task.Yield(); lock (_gate) { if (!ReferenceEquals(_active, operation)) throw new InvalidOperationException("Snapshot transaction ownership changed."); }
        var token = operation.Cancel.Token;
        NativeSourceOwnerConnection.SnapshotLease? lease = null;
        byte[]? bytes = null; Exception? primary = null, cleanup = null;
        try
        {
            await ReconcileResidualAsync().ConfigureAwait(false); token.ThrowIfCancellationRequested();
            lease = await _channel.Observe(request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); RequireLease(lease);
            if (lease.Observation.Result.Outcome == NativeSourceSnapshotOutcome.Complete)
                bytes = await ReadPhysicalAsync(lease, token).ConfigureAwait(false);
            else if (lease.Observation.Result.Outcome != NativeSourceSnapshotOutcome.Missing)
                throw new IOException("Native source bytes changed or could not be captured; source execution was retained.");
            token.ThrowIfCancellationRequested();
        }
        catch (Exception failure) { primary = failure; }
        if (lease is not null)
        {
            try
            {
                var reply = await lease.ReleaseAsync().ConfigureAwait(false);
                if (reply.Result != NativeSourceOwnerResult.Ok) throw new IOException("The native snapshot ticket cleanup is unresolved.");
            }
            catch (Exception failure) { lock (_gate) _residual = lease; cleanup = failure; }
        }
        if (token.IsCancellationRequested && primary is null) primary = new OperationCanceledException(token);
        if (primary is not null || cleanup is not null)
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (primary is not null && cleanup is not null) throw new AggregateException("Snapshot read and native cleanup both failed.", primary, cleanup);
            ExceptionDispatchInfo.Capture(primary ?? cleanup!).Throw();
        }
        return bytes;
    }
    private async Task<byte[]> ReadPhysicalAsync(NativeSourceOwnerConnection.SnapshotLease lease, CancellationToken token)
    {
        byte[]? physical = null; Exception? failure = null;
        try
        {
            return await _io.RunAsync(() =>
            {
                try { return physical = _files.Read(lease.Observation, _channel.Owner, () => RequireLease(lease), token); }
                catch (Exception error) { failure = error; throw; }
            }, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (physical is not null) CryptographicOperations.ZeroMemory(physical);
            if (error is WorkDeferredException) throw new SourceWorkDeferredException("Snapshot file-I/O capacity is busy; retry shortly.");
            if (error is OperationCanceledException && failure is not null && failure is not OperationCanceledException)
                throw new AggregateException("Cancelled snapshot I/O retained its physical failure.", error, failure);
            throw;
        }
    }
    private void RequireLease(NativeSourceOwnerConnection.SnapshotLease lease)
    {
        if (!_channel.Owns(lease)) throw new IOException("The snapshot lease is no longer owned by this live native channel.");
    }
    private async Task ReconcileResidualAsync()
    {
        NativeSourceOwnerConnection.SnapshotLease? residual; lock (_gate) residual = _residual;
        if (residual is null) return;
        var reply = await residual.ReconcileAsync().ConfigureAwait(false);
        if (reply.Result != NativeSourceOwnerResult.Ok) throw new IOException("The previous exact snapshot ticket remains unresolved.");
        lock (_gate) { if (ReferenceEquals(_residual, residual)) _residual = null; }
    }

    internal async Task<T> WithPausedAsync<T>(Func<Task<T>> nativeOperation)
    {
        ArgumentNullException.ThrowIfNull(nativeOperation);
        Operation? active; lock (_gate) { _pauses++; active = _active is { Drain.IsCompleted: false } work ? work : null; }
        var errors = new List<Exception>(); T result = default!;
        try
        {
            await CancelAndJoinAsync(active, errors).ConfigureAwait(false);
            try { await ReconcileResidualAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            // Even a failed read/cleanup must not abandon an explicitly requested
            // physical native Stop. Its failure is retained alongside primary errors.
            try { result = await nativeOperation().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        }
        finally { lock (_gate) _pauses--; }
        if (errors.Count != 0) throw new AggregateException("Snapshot drain and native lifecycle retained failures.", errors);
        return result;
    }
    private static bool OnlyCancellation(Exception error) => error is OperationCanceledException
        || error is AggregateException aggregate && aggregate.InnerExceptions.All(OnlyCancellation);
    private static async Task CancelAndJoinAsync(Operation? active, List<Exception> errors)
    {
        if (active is null) return;
        try { await active.Cancel.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) when (active.Drain.IsCompleted) { }
        catch (Exception error) { errors.Add(error); }
        try { await active.Drain.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (active.Failure is { } failure && !OnlyCancellation(failure)) errors.Add(failure);
    }
    internal Task StopAsync()
    {
        lock (_gate) { _stopping = true; return _stop ??= StopCoreAsync(); }
    }
    internal async Task<T> StopBeforeAsync<T>(Func<Task<T>> nativeShutdown)
    {
        ArgumentNullException.ThrowIfNull(nativeShutdown);
        var errors = new List<Exception>(); T result = default!;
        try { await StopAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { result = await nativeShutdown().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Snapshot and native shutdown retained failures.", errors);
        return result;
    }
    private async Task StopCoreAsync()
    {
        await Task.Yield(); Operation? active; lock (_gate) active = _active;
        var errors = new List<Exception>(); await CancelAndJoinAsync(active is { Drain.IsCompleted: false } ? active : null, errors).ConfigureAwait(false);
        try { active?.Cancel.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { await ReconcileResidualAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        // One cached stop owns this one close, independent of a saturated or
        // already-closed normal read scheduler. It is physically awaited.
        try { await Task.Run(_files.Dispose).ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Native snapshot shutdown retained failures.", errors);
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
