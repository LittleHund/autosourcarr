using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Collections;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Sources;

/// <summary>Persisted website pauses with one generation-owned, reserved hourly probe.</summary>
public sealed class SourceAttention : ISourceAttention, IAsyncDisposable
{
    public const int RetrySeconds = 3600;
    private const string SettingKey = "source_human_attention_v1";
    private static readonly AsyncLocal<(string Key, long Generation)?> Probe = new();
    private readonly SqliteDatabase _database;
    private readonly SettingsRepository _settings;
    private readonly BoundedWorkScheduler _io;
    private readonly Func<double> _clock;
    private readonly SupportPrivacy _privacy;
    private readonly OperationOwner _owner = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _closeGate = new();
    private readonly HashSet<string> _probes = new(StringComparer.Ordinal);
    private readonly BoundedLruCache<long, string> _mangaSources = new(12000);
    private readonly BoundedLruCache<long, string> _chapterSources = new(12000);
    private readonly BoundedLruCache<string, (double At, JsonObject Context)> _contexts = new(12000);
    private JsonObject? _records;
    private Task? _dispose;

    public SourceAttention(SqliteDatabase database, BoundedWorkScheduler io, Func<double>? clock = null, SupportPrivacy? privacy = null)
    {
        _database = database; _settings = new(database); _io = io;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        _privacy = privacy ?? new();
    }

    public void Learn(JsonObject value)
    {
        IEnumerable<JsonNode?> mangas = value["mangas"] is JsonArray many ? many
            : value["manga"] is JsonObject manga ? new[] { manga } : [];
        foreach (var node in mangas)
            if (node is JsonObject row && SourceResponseContract.PositiveId(row["id"], out var id)
                && SourceResponseContract.Identity(row["sourceId"]) is { Length: > 0 } sid) _mangaSources.Set(id, sid);
        if (value["chapters"] is not JsonArray chapters) return;
        foreach (var node in chapters)
            if (node is JsonObject row && SourceResponseContract.TryLong(row["mangaId"], out var parent) && _mangaSources.TryGet(parent, out var sid)
                && SourceResponseContract.PositiveId(row["id"], out var id)) _chapterSources.Set(id, sid);
    }

    public async Task<JsonObject> ContextAsync(string baseUrl, JsonObject operation, CancellationToken cancellationToken)
    {
        using var lease = _owner.Enter();
        using var lifetime = Lifetime(cancellationToken);
        var token = lifetime.Token;
        var op = (JsonObject)operation.DeepClone();
        var kind = SourceResponseContract.Text(op["kind"]);
        var sid = SourceResponseContract.Identity(op["source_id"]);
        if (string.IsNullOrEmpty(sid) && SourceResponseContract.TryLong(op["id"], out var numericId))
            sid = MappedSource(kind, numericId);
        var cacheKey = baseUrl + "|" + (string.IsNullOrEmpty(sid) ? kind + ":" + SourceResponseContract.Identity(op["id"]) : sid);
        JsonObject context;
        if (!_contexts.TryGet(cacheKey, out var cached) || cached.At < _clock() - 60)
        {
            if (!string.IsNullOrEmpty(sid)) op["source_id"] = sid;
            context = await _io.RunAsync(() => Lookup(op), token).ConfigureAwait(false);
            _contexts.Set(cacheKey, (_clock(), (JsonObject)context.DeepClone()));
        }
        else context = (JsonObject)cached.Context.DeepClone();
        var site = Website(SourceResponseContract.Text(context["base_url"]));
        var scope = site.Length > 0 ? site : SourceResponseContract.Text(context["source_id"]);
        if (scope.Length == 0) scope = kind + ":" + SourceResponseContract.Identity(op["id"]);
        var key = Key(baseUrl, scope);
        context["key"] = key; context["website"] = site;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await LoadAsync(token).ConfigureAwait(false);
            foreach (var (oldKey, value) in _records!.ToArray())
            {
                var row = (JsonObject)value!;
                var old = (JsonObject)row["operation"]!;
                var oldKind = SourceResponseContract.Text(old["kind"]);
                var mapped = SourceResponseContract.TryLong(old["id"], out var oldId) ? MappedSource(oldKind, oldId) : null;
                var currentSid = SourceResponseContract.Text(context["source_id"]);
                var related = oldKind == kind && JsonNode.DeepEquals(old["id"], op["id"])
                    || !string.IsNullOrEmpty(mapped) && mapped == currentSid
                    || SourceResponseContract.Text(row["source_id"]) is { Length: > 0 } priorSid && priorSid == currentSid;
                if (!related || SourceResponseContract.Text(row["website"]).Length > 0 || oldKey == key) continue;
                if (_probes.Contains(oldKey)) return (JsonObject)row.DeepClone();
                if (_records![key] is JsonObject existing)
                {
                    existing["occurrences"] = Count(existing, "occurrences") + Count(row, "occurrences");
                    existing["next_retry_at"] = Math.Max(Number(existing, "next_retry_at"), Number(row, "next_retry_at"));
                    existing["generation"] = Count(existing, "generation") + 1;
                }
                else
                {
                    var moved = (JsonObject)row.DeepClone();
                    foreach (var (name, item) in context) moved[name] = item?.DeepClone();
                    moved["id"] = key; moved["generation"] = Count(row, "generation") + 1;
                    _records[key] = moved;
                }
                _records.Remove(oldKey);
                await SaveAsync(token).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
        return context;
    }

    public Task CheckWebsiteAsync(string baseUrl, string host, CancellationToken cancellationToken)
        => CheckAsync(new() { ["key"] = Key(baseUrl, NormalizeHost(host)) }, cancellationToken);

    public async Task CheckAsync(JsonObject context, CancellationToken cancellationToken)
    {
        using var lease = _owner.Enter();
        using var lifetime = Lifetime(cancellationToken);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            await LoadAsync(lifetime.Token).ConfigureAwait(false);
            var key = SourceResponseContract.Text(context["key"]);
            if (_records![key] is JsonObject record && Probe.Value != (key, Count(record, "generation"))) throw Blocked(record);
        }
        finally { _gate.Release(); }
    }

    public async Task FailedAsync(JsonObject context, JsonObject operation, Exception error, CancellationToken cancellationToken)
    {
        var reason = SourceFailures.AssistanceReason(error);
        if (reason.Length == 0) return;
        using var lease = _owner.Enter();
        using var lifetime = Lifetime(cancellationToken);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            await LoadAsync(lifetime.Token).ConfigureAwait(false);
            var now = _clock();
            var key = SourceResponseContract.Text(context["key"]);
            var previous = _records![key] as JsonObject;
            var record = (JsonObject)context.DeepClone();
            record["id"] = key; record["reason"] = reason;
            record["first_seen"] = previous?["first_seen"]?.DeepClone() ?? JsonValue.Create(now);
            record["last_seen"] = now;
            record["next_retry_at"] = previous?["next_retry_at"]?.DeepClone() ?? JsonValue.Create(now + RetrySeconds);
            record["occurrences"] = (previous is null ? 0 : Count(previous, "occurrences")) + 1;
            record["generation"] = (previous is null ? 0 : Count(previous, "generation")) + 1;
            record["operation"] = operation.DeepClone();
            record["detail"] = Limit(_privacy.Text(error.Message), 6000);
            record["last_retry_error"] = previous?["last_retry_error"]?.DeepClone() ?? JsonValue.Create("");
            _records[key] = record;
            await SaveAsync(lifetime.Token).ConfigureAwait(false);
            throw Blocked(record);
        }
        finally { _gate.Release(); }
    }

    public async Task<JsonObject> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter();
        using var lifetime = Lifetime(cancellationToken);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            await LoadAsync(lifetime.Token).ConfigureAwait(false);
            var items = _records!.Select(pair =>
            {
                var row = (JsonObject)pair.Value!.DeepClone();
                row.Remove("operation"); row.Remove("generation"); row["retrying"] = _probes.Contains(pair.Key);
                // SRC-5: a challenge the host could not solve (image CAPTCHA, WAF) is solved by hand in "Open in WebView"
                // on the source's website; the cookies it gets unblock the source.
                var baseUrl = SourceResponseContract.Text(row["base_url"]);
                var sourceId = SourceResponseContract.Text(row["source_id"]);
                var website = SourceResponseContract.Text(row["website"]);
                var url = baseUrl.Length > 0 ? baseUrl : website.Length > 0 ? "https://" + website + "/" : "";
                row["webview"] = url.Length > 0 || sourceId.Length > 0 ? new JsonObject { ["url"] = url, ["source_id"] = sourceId } : null;
                return row;
            }).OrderBy(row => Number(row, "first_seen")).ToArray();
            return new() { ["items"] = SourceResponseContract.Array(items), ["count"] = items.Length, ["retry_seconds"] = RetrySeconds };
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RetryAsync(string key, ISourceVerificationProbe client, bool manual = false, CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter();
        using var lifetime = Lifetime(cancellationToken);
        var token = lifetime.Token;
        JsonObject context;
        JsonObject operation;
        long generation;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await LoadAsync(token).ConfigureAwait(false);
            if (_records![key] is not JsonObject row || _probes.Contains(key) || !manual && Number(row, "next_retry_at") > _clock()) return false;
            generation = Count(row, "generation");
            row["next_retry_at"] = _clock() + RetrySeconds; row["last_retry_at"] = _clock();
            _probes.Add(key);
            context = (JsonObject)row.DeepClone(); operation = (JsonObject)row["operation"]!.DeepClone();
            try { await SaveAsync(token).ConfigureAwait(false); }
            catch { _probes.Remove(key); throw; }
        }
        finally { _gate.Release(); }
        var previous = Probe.Value;
        Probe.Value = (key, generation);
        try
        {
            using var priority = SourceWorkContext.Background(!manual);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(client.RequestTimeout.TotalSeconds + 5, 10, 120)));
            await client.RetryVerificationAsync(operation, context, deadline.Token).ConfigureAwait(false);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_records![key] is JsonObject current && Count(current, "generation") == generation)
                {
                    _records.Remove(key);
                    try { await SaveAsync(token).ConfigureAwait(false); }
                    catch
                    {
                        // A cancelled physical write may already have committed.
                        // Restore the pause before releasing this probe's owner.
                        _records[key] = current;
                        await SaveAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                }
            }
            finally { _gate.Release(); }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_records![key] is JsonObject row)
                { row["last_retry_error"] = Limit(_privacy.Text(error.Message), 1000); await SaveAsync(token).ConfigureAwait(false); }
            }
            finally { _gate.Release(); }
            return false;
        }
        finally
        {
            Probe.Value = previous;
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _probes.Remove(key); }
            finally { _gate.Release(); }
        }
    }

    public async Task TickAsync(ISourceVerificationProbe client, CancellationToken cancellationToken = default)
    {
        var snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in ((JsonArray)snapshot["items"]!).OfType<JsonObject>())
            if (Number(row, "next_retry_at") <= _clock()) await RetryAsync(SourceResponseContract.Text(row["id"]), client, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RunMonitorAsync(ISourceVerificationProbe client, Action<Exception> recordError, CancellationToken cancellationToken)
    {
        using var lease = _owner.Enter();
        using var lifetime = Lifetime(cancellationToken);
        while (true)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            try { await TickAsync(client, lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
            catch (Exception error) { recordError(error); }
            await Task.Delay(TimeSpan.FromSeconds(10), lifetime.Token).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() { lock (_closeGate) return new(_dispose ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        try { await _owner.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Dispose(); }
    }
    private CancellationTokenSource Lifetime(CancellationToken token) => CancellationTokenSource.CreateLinkedTokenSource(token, _owner.CancellationToken);
    private JsonObject Lookup(JsonObject operation)
    {
        var sid = SourceResponseContract.Identity(operation["source_id"]);
        var kind = SourceResponseContract.Text(operation["kind"]);
        if (string.IsNullOrEmpty(sid) && SourceResponseContract.TryLong(operation["id"], out var id))
        {
            if (kind == "pages") sid = _database.Read(DatabaseFile.Origins, session => session.Query("SELECT source_id FROM chapter_candidates WHERE source_chapter_id=$id LIMIT 1", ("$id", id)).FirstOrDefault()?.Text("source_id"));
            if (kind == "manga") sid = _database.Read(DatabaseFile.Catalog, session =>
            {
                foreach (var table in new[] { "source_matches", "source_variants" })
                    if (session.Query($"SELECT source_id FROM {table} WHERE source_manga_id=$id LIMIT 1", ("$id", id)).FirstOrDefault() is { } row) return row.Text("source_id");
                return null;
            });
        }
        return _database.Read(DatabaseFile.Catalog, session =>
        {
            var row = session.Query("SELECT source_id,name,base_url FROM source_config WHERE source_id=$id", ("$id", sid ?? "")).FirstOrDefault();
            return row is null ? new JsonObject { ["source_id"] = sid ?? "", ["name"] = "Source", ["base_url"] = "" }
                : new JsonObject { ["source_id"] = row.Text("source_id"), ["name"] = row.Text("name"), ["base_url"] = row.Text("base_url") };
        });
    }
    private string? MappedSource(string kind, long id) => (kind == "pages" ? _chapterSources : _mangaSources).TryGet(id, out var sid) ? sid : null;
    private async Task LoadAsync(CancellationToken token)
    {
        if (_records is not null) return;
        var value = await _io.RunAsync(() => _settings.Get(SettingKey, new JsonObject()), token).ConfigureAwait(false);
        if (value is not JsonObject records || records.Any(pair => pair.Value is not JsonObject row || row["operation"] is not JsonObject
            || SourceResponseContract.Text(row["id"]) != pair.Key || SourceResponseContract.Text(row["key"]) != pair.Key
            || !SourceResponseContract.TryLong(row["generation"], out _) || !SourceResponseContract.TryLong(row["occurrences"], out _)))
            throw new SourceWorkDeferredException("Saved website verification state is unreadable; source work remains paused for recovery.");
        _records = records;
    }
    private Task SaveAsync(CancellationToken token)
    {
        var snapshot = _records!.DeepClone();
        return _io.RunAsync(() => { _settings.Set(SettingKey, snapshot); return 0; }, token);
    }
    private HumanAssistanceRequiredException Blocked(JsonObject record) => new(record, (int)Math.Clamp(Math.Ceiling(Number(record, "next_retry_at") - _clock()), 1, int.MaxValue));
    private static long Count(JsonObject value, string key) => SourceResponseContract.TryLong(value[key], out var count) ? count : 0;
    private static double Number(JsonObject value, string key) => value[key] is JsonValue node && node.TryGetValue<double>(out var number) && double.IsFinite(number) ? number
        : SourceResponseContract.TryLong(value[key], out var whole) ? whole : 0;
    private static string Key(string engine, string scope) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(engine + "|" + scope)))[..24];
    private static string NormalizeHost(string host) { host = host.ToLowerInvariant(); return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host; }
    private static string Website(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? NormalizeHost(uri.Host) : "";
    private static string Limit(string text, int count) => text[..Math.Min(count, text.Length)];
}
