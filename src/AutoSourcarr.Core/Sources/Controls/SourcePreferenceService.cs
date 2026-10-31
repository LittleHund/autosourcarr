using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Sources.Controls;

/// <summary>Safe message only: never retain an arbitrary extension/transport exception or request body.</summary>
public sealed class SourceControlFailedException(string message, bool refreshRequired = false) : SuwayomiException(message)
{
    public bool RefreshRequired { get; } = refreshRequired;
}

/// <summary>Per-instance native preference operations. This service owns sharedSourceAdmin; callers must not acquire it first.</summary>
public sealed class SourcePreferenceService : IAsyncDisposable
{
    private const int MaximumObservedScreens = 256;
    private readonly SuwayomiClient _client;
    private readonly SemaphoreSlim _sourceAdmin;
    private readonly Func<(bool Ready, long Generation)> _state;
    private readonly SupportPrivacy _privacy;
    private readonly OperationOwner _owner = new();
    private readonly object _screenGate = new();
    private readonly Dictionary<string, ObservedScreen> _screens = new(StringComparer.Ordinal);
    private long _screenSequence;

    /// <param name="sharedSourceAdmin">The actual per-instance source maintenance lane, shared with installation, restart and repair owners.</param>
    /// <param name="currentState">The actual engine owner's readiness/generation snapshot; no default always-ready implementation is supplied.</param>
    public SourcePreferenceService(SuwayomiClient client, SemaphoreSlim sharedSourceAdmin, Func<(bool Ready, long Generation)> currentState, SupportPrivacy privacy)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(sharedSourceAdmin); ArgumentNullException.ThrowIfNull(currentState); ArgumentNullException.ThrowIfNull(privacy);
        _client = client; _sourceAdmin = sharedSourceAdmin; _state = currentState; _privacy = privacy;
    }
    public bool Ready => !_owner.IsStopping && _state().Ready;

    public async Task<JsonObject> SnapshotAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        var id = SourcePreferenceContract.SourceId(sourceId); using var lease = Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken);
        Generation(); await using var session = _client.OpenSession(linked.Token);
        await _sourceAdmin.WaitAsync(session.CancellationToken).ConfigureAwait(false);
        try
        {
            var generation = Generation(); var result = await SnapshotCoreAsync(session, id, generation, session.CancellationToken).ConfigureAwait(false);
            session.ThrowIfRetired(); linked.Token.ThrowIfCancellationRequested(); Observe(id, result, generation, session.Engine!); return result;
        }
        finally { _sourceAdmin.Release(); }
    }

    /// <summary>Once admitted, a disconnected caller joins the mutation instead of cancelling and releasing its maintenance lane early.</summary>
    public async Task<JsonObject> UpdateAsync(string sourceId, JsonNode? change, CancellationToken cancellationToken = default)
    {
        var id = SourcePreferenceContract.SourceId(sourceId); var input = change as JsonObject ?? throw new ArgumentException("Provide one source setting change");
        input = (JsonObject)input.DeepClone(); using var lease = Enter();
        Generation(); await using var session = _client.OpenSession(_owner.CancellationToken);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
        await _sourceAdmin.WaitAsync(admission.Token).ConfigureAwait(false);
        try
        {
            session.ThrowIfRetired(); admission.Token.ThrowIfCancellationRequested(); var generation = Generation(); RequireObserved(id, SourcePreferenceContract.Text(input["revision"]), generation, session.Engine!);
            var work = UpdateCoreAsync(session, id, input, generation, session.CancellationToken);
            try { return await work.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // No request cancellation token reaches admitted physical mutation I/O.
                // Always join its result/failure before releasing source-admin ownership.
                try { await work.ConfigureAwait(false); } catch (Exception) { }
                throw new OperationCanceledException(cancellationToken);
            }
        }
        finally { _sourceAdmin.Release(); }
    }

    public Task StopAsync() => _owner.StopAsync();
    public ValueTask DisposeAsync() => _owner.DisposeAsync();

    private async Task<JsonObject> SnapshotCoreAsync(SourceTransportSession session, string id, long generation, CancellationToken token)
    {
        JsonObject data;
        try { data = await session.GraphQlAsync(SourcePreferenceContract.SnapshotQuery, new() { ["id"] = id }, token).ConfigureAwait(false); }
        catch (SourceWorkDeferredException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { session.ThrowIfRetired(); throw new SourceControlFailedException("This source could not provide its settings. Update the extension or check source-engine logs."); }
        session.ThrowIfRetired(); CheckGeneration(generation);
        if (data["source"] is not JsonObject source || SourceResponseContract.Identity(source["id"]) != id)
            throw new ArgumentException("Source is no longer installed. Refresh Sources before opening its settings.");
        try
        {
            var raw = source["preferences"] is null ? new JsonArray() : source["preferences"] as JsonArray ?? throw new ArgumentException("Malformed source preference metadata");
            var result = SourcePreferenceContract.Normalize(source, raw); RegisterSecrets(result); return result;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { throw new SourceControlFailedException("This source could not provide its settings. Update the extension or check source-engine logs."); }
    }

    private async Task<JsonObject> UpdateCoreAsync(SourceTransportSession session, string id, JsonObject change, long generation, CancellationToken token)
    {
        var current = await SnapshotCoreAsync(session, id, generation, token).ConfigureAwait(false);
        var checkedChange = SourcePreferenceContract.Change(current, change); RegisterChangedSecret(current, checkedChange);
        session.ThrowIfRetired(); CheckGeneration(generation); Forget(id);
        JsonObject data;
        try
        {
            data = await session.GraphQlAsync(SourcePreferenceContract.MutationQuery, new() { ["input"] = new JsonObject { ["source"] = id, ["change"] = checkedChange } }, token).ConfigureAwait(false);
        }
        catch (SourceWorkDeferredException) { throw; }
        // Retirement interrupts this logical mutation, invalidates its screen and never permits automatic replay.
        catch (OperationCanceledException) when (session.Engine?.IsRetired == true && !_owner.IsStopping) { throw; }
        catch (Exception)
        { throw new SourceControlFailedException("The source could not confirm the setting change. Reload settings to check its saved value before retrying.", refreshRequired: true); }
        var after = _state();
        if (_owner.IsStopping || !after.Ready || after.Generation != generation)
            throw new SourceControlFailedException("Source engine changed while saving settings. Reload settings to check the saved value before retrying.", refreshRequired: true);
        if (data["updateSourcePreference"] is not JsonObject payload || payload["source"] is not JsonObject source
            || SourceResponseContract.Identity(source["id"]) != id || payload["preferences"] is not JsonArray raw)
            throw new SourceControlFailedException("The source did not confirm the setting change. Reload settings before retrying.", refreshRequired: true);
        JsonObject result;
        try { result = SourcePreferenceContract.Normalize(source, raw); RegisterSecrets(result); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { throw new SourceControlFailedException("The source did not confirm the setting change. Reload settings before retrying.", refreshRequired: true); }
        session.ThrowIfRetired(); CheckGeneration(generation); Observe(id, result, generation, session.Engine!); return result;
    }

    private IDisposable Enter()
    {
        if (_owner.IsStopping) throw new SourceEngineUnavailableException("Source settings are unavailable during shutdown or reset; retry shortly");
        try { return _owner.Enter(); }
        catch (OperationCanceledException) { throw new SourceEngineUnavailableException("Source settings are unavailable during shutdown or reset; retry shortly"); }
    }
    private long Generation()
    {
        var state = _state(); if (_owner.IsStopping || !state.Ready) throw new SourceEngineUnavailableException("Source engine is starting or unavailable; retry shortly"); return state.Generation;
    }
    private void CheckGeneration(long generation)
    { if (Generation() != generation) throw new SourcePreferencesConflictException("Source engine changed. Reload its settings before saving."); }
    private void Observe(string id, JsonObject snapshot, long generation, SourceTransportEpoch transport)
    {
        lock (_screenGate)
        {
            _screens[id] = new(SourcePreferenceContract.Text(snapshot["revision"]), generation, ++_screenSequence, transport);
            while (_screens.Count > MaximumObservedScreens) _screens.Remove(_screens.MinBy(pair => pair.Value.Sequence).Key);
        }
    }
    private void RequireObserved(string id, string revision, long generation, SourceTransportEpoch transport)
    {
        lock (_screenGate)
            if (!_screens.TryGetValue(id, out var observed) || observed.Revision != revision || observed.Generation != generation || observed.Transport.IsRetired || !ReferenceEquals(observed.Transport, transport))
                throw new SourcePreferencesConflictException("Source settings changed. Reload them before saving your changes.");
    }
    private void Forget(string id) { lock (_screenGate) _screens.Remove(id); }
    private void RegisterSecrets(JsonObject snapshot)
    {
        foreach (var pref in snapshot["preferences"]!.AsArray().OfType<JsonObject>().Where(pref => SourcePreferenceContract.Flag(pref["secret"])))
        { Register(pref["value"]); Register(pref["default"]); }
    }
    private void RegisterChangedSecret(JsonObject snapshot, JsonObject change)
    {
        var position = change["position"]!.GetValue<long>();
        if (SourcePreferenceContract.Flag(snapshot["preferences"]![(int)position]!["secret"])) foreach (var pair in change.Where(pair => pair.Key != "position")) Register(pair.Value);
    }
    private void Register(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length > 0) _privacy.Register(new Dictionary<string, string?> { ["password"] = text });
        else if (value is JsonArray values) foreach (var item in values) Register(item);
    }
    private sealed record ObservedScreen(string Revision, long Generation, long Sequence, SourceTransportEpoch Transport);
}
