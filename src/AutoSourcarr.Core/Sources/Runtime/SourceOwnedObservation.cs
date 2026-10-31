using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Sources.Inventory;
using Microsoft.Win32.SafeHandles;

namespace AutoSourcarr.Core.Sources.Runtime;

public readonly record struct SourceObservationRootIdentity(ulong Device, ulong Inode);

/// <summary>Exact retained bytes only. This is not loaded-source, working-source or current-UA proof.</summary>
public sealed class SourceOwnedBytes
{
    private readonly byte[] _contents;
    internal SourceOwnedBytes(string kind, string leaf, SourceObservationRootIdentity root, byte[] contents)
    {
        Kind = kind; Leaf = leaf; Root = root; _contents = (byte[])contents.Clone();
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(_contents));
    }
    public string Kind { get; }
    public string Leaf { get; }
    public SourceObservationRootIdentity Root { get; }
    public int Length => _contents.Length;
    public string Sha256 { get; }
    public byte[] Contents() => (byte[])_contents.Clone();
    public override string ToString() => $"Retained {Kind} byte observation ({Length} bytes)";
}

/// <summary>
/// Source-UID read-only helper for the fixed native observer, not a general path API.
/// The native receiver must authenticate the inherited channel/expected child first.
/// This helper independently checks actual dropped credentials and held-root identity;
/// it does not create a native-session qualification or engine-ready receipt.
/// </summary>
public sealed class SourceOwnedObservation : IAsyncDisposable
{
    public const int UserAgentStatusMaximumBytes = 64 * 1024;
    public const string UserAgentStatusLeaf = "autosourcarr-user-agent.properties";
    private readonly SourceObservationRoot _root;
    private readonly BoundedWorkScheduler _io;
    private readonly OperationOwner _operations = new();
    private readonly Action? _controlledBeforeRead;
    private readonly object _stopGate = new();
    private Task? _stop;

    private SourceOwnedObservation(SourceObservationRoot root, BoundedWorkScheduler io, Action? controlledBeforeRead = null)
    { _root = root; _io = io; _controlledBeforeRead = controlledBeforeRead; }

    /// <summary>
    /// Borrows and duplicates a native-parent-supplied directory descriptor. No caller path
    /// is accepted: production always checks the fixed Tachidesk absolute root as well.
    /// expectedParent/IDs/root must come from the authenticated native boot/operation binding.
    /// The caller retains the input handle and scheduler. This instance owns its duplicate.
    /// </summary>
    public static SourceOwnedObservation AcceptInheritedRoot(SafeFileHandle heldRoot,
        SourceObservationRootIdentity expectedRoot, uint uid, uint gid, int expectedParent,
        BoundedWorkScheduler io)
    {
        ArgumentNullException.ThrowIfNull(io);
        return new(SourceObservationRoot.AcceptNative(heldRoot, expectedRoot, uid, gid, expectedParent), io);
    }

    // Controlled real-disk/lifetime tests only. This bypasses the native credential and
    // fixed production path guard, never the actual Linux descriptor/file checks.
    internal static SourceOwnedObservation Controlled(string syntheticRoot, BoundedWorkScheduler io, Action? beforeRead = null)
        => new(SourceObservationRoot.Controlled(syntheticRoot), io, beforeRead);

    internal static void RequireInstalledJarLeaf(string leaf)
    {
        if (leaf is null || !leaf.EndsWith(".jar", StringComparison.Ordinal)
            || !SourceInventoryFiles.IsInstalledFilename(leaf) || leaf.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("Select a validated installed JAR leaf.", nameof(leaf));
    }

    public Task<SourceOwnedBytes?> ReadInstalledJarAsync(string leaf, CancellationToken cancellationToken = default)
    {
        RequireInstalledJarLeaf(leaf);
        return ReadAsync("installed-jar", "extensions", leaf, SourcePackage.MaximumBytes, allowEmpty: false, cancellationToken);
    }

    public Task<SourceOwnedBytes?> ReadUserAgentStatusAsync(CancellationToken cancellationToken = default)
        => ReadAsync("retained-ua-status", "cache", UserAgentStatusLeaf, UserAgentStatusMaximumBytes, allowEmpty: true, cancellationToken);

    private async Task<SourceOwnedBytes?> ReadAsync(string kind, string directory, string leaf, int maximum,
        bool allowEmpty, CancellationToken cancellationToken)
    {
        using var operation = _operations.Enter();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operations.CancellationToken);
        Exception? physicalFailure = null;
        try
        {
            return await _io.RunAsync(() =>
            {
                try { return ReadCore(kind, directory, leaf, maximum, allowEmpty, lifetime.Token); }
                catch (Exception error) { physicalFailure = error; throw; }
            }, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (physicalFailure is not null && physicalFailure is not OperationCanceledException)
        {
            // The shared scheduler joins physical work even when cancelled. Retain a late
            // physical fault as well, rather than losing it to its cancellation conversion.
            throw new AggregateException("Cancelled source observation retained a physical failure.", error, physicalFailure);
        }
    }

    private SourceOwnedBytes? ReadCore(string kind, string directory, string leaf, int maximum, bool allowEmpty, CancellationToken token)
    {
        SourceObservationRoot.ReadLease? input = null;
        SourceOwnedBytes? result = null;
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            try { input = _root.Open(directory, leaf, maximum); }
            catch (FileNotFoundException) { _root.RequireCurrent(); return null; }
            _controlledBeforeRead?.Invoke();
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var count = input.File.Read(buffer, 0, Math.Min(buffer.Length, maximum + 1 - checked((int)output.Length)));
                if (count == 0) break;
                output.Write(buffer, 0, count);
                if (output.Length > maximum) throw new InvalidDataException("Source observation exceeds its byte limit.");
            }
            if ((!allowEmpty && output.Length == 0) || (ulong)output.Length != input.Before.Size)
                throw new InvalidDataException("Source observation is empty or changed length.");
            _root.RequireUnchanged(input, directory, leaf, maximum);
            token.ThrowIfCancellationRequested();
            result = new(kind, leaf, _root.Identity, output.ToArray());
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { input?.Dispose(); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    public Task StopAsync() { lock (_stopGate) return _stop ??= StopCoreAsync(); }
    private async Task StopCoreAsync()
    {
        await Task.Yield();
        var errors = new List<Exception>();
        try { await _operations.StopAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        // StopAsync has physically joined every admitted scheduler worker even if its
        // cancellation callbacks reported failure. Only now may the held root close.
        try { _root.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { await _operations.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Source observation shutdown retained failures.", errors);
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
