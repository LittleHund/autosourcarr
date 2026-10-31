using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair.Runner.Protocol;

namespace AutoSourcarr.Core.Repair.Runner.Guest;

/// <summary>
/// Actual stream/file endpoint only. It has no boot, self-test or tool observations and cannot qualify
/// the current host, which requires self-test-v1. Project commit is a typed store API awaiting reviewed
/// host capability/operation integration. The agent/worker UID split remains a required boot prerequisite.
/// </summary>
public sealed class GuestArtifactServer : IAsyncDisposable
{
    private readonly Stream _input, _output;
    private readonly GuestChannel _channel;
    private readonly OperationOwner _owner = new();
    private readonly string _agentRoot;
    private readonly Action<string>? _physical;
    private readonly object _gate = new();
    private Task? _stopping;
    private Task? _closingStreams;
    private GuestPeerIdentity? _peer;
    private GuestArtifactStore? _artifacts;
    private long _lastRequest;
    private int _running;
    private GuestArtifactServer(Stream input, Stream output, string agentRoot, Action<string>? physical)
    { _input = input; _output = output; _channel = new(input, output); _agentRoot = agentRoot; _physical = physical; }
    public GuestArtifactStore Artifacts => _artifacts ?? throw new InvalidOperationException("The artifact session hello has not been accepted");
    /// <summary>Fixed guest-private root. Accepting a hello is identity binding, never self-test/boot qualification.</summary>
    public static async Task<GuestArtifactServer> AcceptAsync(Stream ownedInput, Stream ownedOutput, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownedInput); ArgumentNullException.ThrowIfNull(ownedOutput);
        try { GuestPrivateFiles.RequireAgent(); }
        catch (Exception original)
        {
            try { await CloseOwnedAsync(ownedInput, ownedOutput).ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Guest identity guard and owned stream cleanup both failed", original, cleanup); }
            throw;
        }
        return await AcceptCoreAsync(ownedInput, ownedOutput, "/work/agent-jobs", null, cancellationToken).ConfigureAwait(false);
    }
    internal static Task<GuestArtifactServer> AcceptControlledAsync(Stream ownedInput, Stream ownedOutput, string existingAgentRoot, Action<string>? physical = null, CancellationToken token = default) =>
        AcceptCoreAsync(ownedInput, ownedOutput, existingAgentRoot, physical, token);
    private static async Task<GuestArtifactServer> AcceptCoreAsync(Stream input, Stream output, string root, Action<string>? physical, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(output); var server = new GuestArtifactServer(input, output, root, physical);
        try { await server.AcceptHelloAsync(token).ConfigureAwait(false); return server; }
        catch (Exception original)
        {
            var failure = token.IsCancellationRequested ? new OperationCanceledException(token) : original;
            try { await server.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Guest hello and joined cleanup both failed", failure, cleanup); }
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw;
        }
    }
    private async Task AcceptHelloAsync(CancellationToken token)
    {
        using var admission = _owner.Enter(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _owner.CancellationToken);
        using var stop = linked.Token.Register(static value => ((GuestArtifactServer)value!).RequestStop(), this);
        using var hello = await _channel.ReadAsync(linked.Token).ConfigureAwait(false); var frame = hello.RootElement;
        GuestWire.Exact(frame, "type", "id", "nonce", "payload"); var id = GuestWire.RequestId(frame, 1); var nonce = GuestWire.Text(frame, "nonce"); GuestWire.Identity(nonce, 64);
        if (GuestWire.Text(frame, "type") != "hello") throw new InvalidDataException("A guest artifact session requires hello first");
        var payload = frame.GetProperty("payload"); GuestWire.Exact(payload, "protocol", "agent", "image_sha256", "job_id"); var job = GuestWire.Text(payload, "job_id"); var image = GuestWire.Text(payload, "image_sha256"); GuestWire.Identity(job, 32); GuestWire.Identity(image, 64);
        if (GuestWire.Number(payload, "protocol") != 1 || GuestWire.Text(payload, "agent") != "autosourcarr-controller-v1") throw new InvalidDataException("Unsupported guest controller protocol");
        _peer = new(job, nonce); _artifacts = await GuestArtifactStore.CreateAsync(_agentRoot, _peer, _physical, linked.Token).ConfigureAwait(false); _lastRequest = 1;
        await WriteAsync("hello", id, new { protocol = 1, agent = "autosourcarr-guest-v1", image_sha256 = image, capabilities = new[] { "artifact-transfer-v1" } }, linked.Token).ConfigureAwait(false);
    }
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_peer is null || _artifacts is null) throw new InvalidOperationException("Guest hello has not completed");
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("The guest protocol has exactly one reader and one run");
        Exception? failure = null;
        try
        {
            using var admission = _owner.Enter(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
            using var stop = linked.Token.Register(static value => ((GuestArtifactServer)value!).RequestStop(), this);
            while (true)
            {
                using var request = await _channel.ReadAsync(linked.Token).ConfigureAwait(false); var frame = request.RootElement; var id = CheckFrame(frame, "request"); GuestWire.RequestId(frame, checked(_lastRequest + 1)); _lastRequest++;
                var payload = frame.GetProperty("payload"); var operation = GuestWire.Text(payload, "operation");
                if (operation == "artifact-upload-v1") await ReceiveAsync(id, payload, linked.Token).ConfigureAwait(false);
                else if (operation == "artifact-download-v1") await ProduceAsync(id, payload, linked.Token).ConfigureAwait(false);
                else
                {
                    await WriteAsync("error", id, new { code = "unsupported-operation" }, linked.Token).ConfigureAwait(false);
                    throw new InvalidDataException("Guest self-test, build, inspect, validate, project-commit and egress are not implemented/advertised by this endpoint");
                }
            }
        }
        catch (Exception error)
        {
            await Artifacts.PoisonAsync().ConfigureAwait(false);
            failure = cancellationToken.IsCancellationRequested ? new OperationCanceledException(cancellationToken) : _owner.CancellationToken.IsCancellationRequested ? new OperationCanceledException(_owner.CancellationToken) : error;
        }
        finally
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException("Guest operation and joined cleanup both failed", failure, cleanup); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private async Task ReceiveAsync(string id, JsonElement payload, CancellationToken token)
    {
        var descriptor = GuestWire.Descriptor(payload, GuestArtifactDirection.IntoGuest); var artifact = GuestWire.Text(payload, "artifact_id"); GuestWire.Identity(artifact, 32);
        await using var sink = await Artifacts.BeginReceiveAsync(Binding(artifact, descriptor, GuestArtifactDirection.IntoGuest), token).ConfigureAwait(false);
        await WriteAsync("result", id, new { phase = "open", artifact_id = artifact, index = 0, next_offset = 0 }, token).ConfigureAwait(false);
        long offset = 0; var index = 0;
        while (offset < descriptor.Length)
        {
            using var frame = await ReadAsync(id, "chunk", token).ConfigureAwait(false); var data = frame.RootElement.GetProperty("payload"); GuestWire.Exact(data, "phase", "artifact_id", "index", "offset", "data");
            var wanted = (int)Math.Min(GuestArtifactTransfer.ChunkBytes, descriptor.Length - offset); var encoded = GuestWire.Text(data, "data");
            if (GuestWire.Text(data, "phase") != "data" || GuestWire.Text(data, "artifact_id") != artifact || GuestWire.Number(data, "index") != index || GuestWire.Number(data, "offset") != offset || encoded.Length != ((wanted + 2) / 3) * 4)
                throw new InvalidDataException("Guest input chunk order, identity or bounded size changed");
            var bytes = new byte[wanted]; if (!Convert.TryFromBase64String(encoded, bytes, out var length) || length != wanted || Convert.ToBase64String(bytes) != encoded) throw new InvalidDataException("Guest input is not canonical Base64");
            await sink.AppendAsync(bytes, token).ConfigureAwait(false); offset += wanted;
            await WriteAsync("result", id, new { phase = "data", artifact_id = artifact, index, next_offset = offset }, token).ConfigureAwait(false); index++;
        }
        using (var commit = await ReadAsync(id, "request", token).ConfigureAwait(false)) GuestWire.Commit(commit.RootElement.GetProperty("payload"), artifact, descriptor, index);
        await sink.CompleteAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        await WriteAsync("result", id, new { phase = "commit", artifact_id = artifact, length = descriptor.Length, sha256 = descriptor.Sha256, chunks = index }, token).ConfigureAwait(false);
    }
    private async Task ProduceAsync(string id, JsonElement payload, CancellationToken token)
    {
        var descriptor = GuestWire.Descriptor(payload, GuestArtifactDirection.FromGuest); var artifact = GuestWire.Text(payload, "artifact_id"); GuestWire.Identity(artifact, 32);
        await using var source = await Artifacts.OpenProducedAsync(Binding(artifact, descriptor, GuestArtifactDirection.FromGuest), token).ConfigureAwait(false);
        await WriteAsync("result", id, new { phase = "open", artifact_id = artifact, index = 0, next_offset = 0 }, token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[GuestArtifactTransfer.ChunkBytes]; long offset = 0; var index = 0;
        while (offset < descriptor.Length)
        {
            var wanted = (int)Math.Min(buffer.Length, descriptor.Length - offset); var have = 0;
            while (have < wanted) { var count = await source.ReadAsync(buffer.AsMemory(have, wanted - have), token).ConfigureAwait(false); if (count == 0) throw new InvalidDataException("Guest output ended early"); have += count; }
            hash.AppendData(buffer.AsSpan(0, wanted));
            await WriteAsync("chunk", id, new { phase = "data", artifact_id = artifact, index, offset, data = Convert.ToBase64String(buffer, 0, wanted) }, token).ConfigureAwait(false);
            using (var ack = await ReadAsync(id, "result", token).ConfigureAwait(false)) GuestWire.Ack(ack.RootElement.GetProperty("payload"), artifact, "data", index, offset + wanted);
            offset += wanted; index++;
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0 || Convert.ToHexStringLower(hash.GetHashAndReset()) != descriptor.Sha256) throw new InvalidDataException("Guest output bytes changed");
        await WriteAsync("result", id, new { phase = "commit", artifact_id = artifact, length = descriptor.Length, sha256 = descriptor.Sha256, chunks = index }, token).ConfigureAwait(false);
        using (var ack = await ReadAsync(id, "result", token).ConfigureAwait(false)) GuestWire.Commit(ack.RootElement.GetProperty("payload"), artifact, descriptor, index);
        await source.CompleteAsync(token).ConfigureAwait(false);
    }
    private GuestArtifactBinding Binding(string id, GuestArtifactDescriptor descriptor, GuestArtifactDirection direction) => new(_peer!.JobId, _peer.Nonce, id, direction, descriptor);
    private string CheckFrame(JsonElement frame, string type)
    {
        GuestWire.Exact(frame, "type", "id", "nonce", "payload");
        if (GuestWire.Text(frame, "type") != type || GuestWire.Text(frame, "nonce") != _peer!.Nonce || frame.GetProperty("payload").ValueKind != JsonValueKind.Object) throw new InvalidDataException("Guest frame/session/type changed"); return GuestWire.Text(frame, "id");
    }
    private async Task<JsonDocument> ReadAsync(string id, string type, CancellationToken token)
    {
        var document = await _channel.ReadAsync(token).ConfigureAwait(false);
        try { if (CheckFrame(document.RootElement, type) != id) throw new InvalidDataException("Guest artifact request correlation changed"); return document; } catch { document.Dispose(); throw; }
    }
    private Task WriteAsync(string type, string id, object payload, CancellationToken token) => _channel.WriteAsync(JsonSerializer.SerializeToElement(new { type, id, nonce = _peer!.Nonce, payload }), token);
    private void RequestStop() { _ = StopAsync(); }
    public Task StopAsync()
    {
        TaskCompletionSource completion;
        lock (_gate) { if (_stopping is not null) return _stopping; completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _stopping = completion.Task; }
        _ = StopCoreAsync(completion); return completion.Task;
    }
    private async Task StopCoreAsync(TaskCompletionSource completion)
    {
        var errors = new List<Exception>();
        try
        {
            var drain = _owner.StopAsync(() => _closingStreams = CloseStreamsAsync());
            try { if (_closingStreams is not null) await _closingStreams.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { await drain.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { if (_artifacts is not null) await _artifacts.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            try { _channel.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { await _owner.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count == 0) completion.TrySetResult(); else completion.TrySetException(new AggregateException("Guest endpoint stopped after physical ownership joined", errors));
    }
    private Task CloseStreamsAsync() => CloseOwnedAsync(_input, _output);
    private static async Task CloseOwnedAsync(Stream input, Stream output)
    {
        // At most two fixed owned closures. Initiate both even if a synchronous native
        // Dispose blocks; neither an awaiting writer nor a reader may prevent the other's close.
        var streams = ReferenceEquals(input, output) ? new[] { input } : new[] { input, output };
        var closing = streams.Select(stream => Task.Run(async () => await stream.DisposeAsync().ConfigureAwait(false))).ToArray();
        try { await Task.WhenAll(closing).ConfigureAwait(false); }
        catch { throw new AggregateException("Guest owned stream close failed after all close tasks joined", closing.Where(task => task.IsFaulted).SelectMany(task => task.Exception!.InnerExceptions)); }
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
