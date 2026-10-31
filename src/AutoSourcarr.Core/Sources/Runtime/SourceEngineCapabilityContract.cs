using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Sources.Runtime;

public sealed class SourceEngineCapabilityException(string message) : SuwayomiException(message);

/// <summary>Actual read-only API-shape observations. Never starts an engine, grants readiness, promotes a pin or qualifies native execution.</summary>
public sealed class SourceEngineCapabilityContract : IAsyncDisposable
{
    public const string Baseline = "2.3.2243", Target = "2.4.2366", SettingKey = "reimagined_engine_contract";
    public const string Query = "query ReimaginedEngineContract{__schema{mutationType{fields{name args{name}}} subscriptionType{fields{name args{name}}}}}";
    public const string Limit = "API shapes only. No APK/JAR build, migration, restart, download, event reconnect or native upgrade/rollback was performed.";
    private readonly SettingsRepository _settings;
    private readonly SuwayomiClient _client;
    private readonly string _engine;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly SourceAboutTransportRegistration? _aboutTransport;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly OperationOwner _owner = new();
    private int _disposed;
    private readonly object _closeGate = new();
    private Task? _stop;

    /// <param name="client">The actual GraphQL client for this same configured identity. Its lifetime is owned by the caller.</param>
    /// <param name="identity">Fixed integrated loopback or explicitly configured external engine, including explicit about credentials where required.</param>
    /// <param name="trustedTestAboutTransport">Controlled test HTTP only. Production uses the bounded, no-proxy/no-cookie handler.</param>
    public SourceEngineCapabilityContract(SettingsRepository settings, SuwayomiClient client, SourceEngineCapabilityIdentity identity,
        SupportPrivacy privacy, string engine, TimeProvider? clock = null, TimeSpan? timeout = null, HttpMessageHandler? trustedTestAboutTransport = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings)); _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(privacy);
        if (!identity.Matches(client.BaseUrl)) throw new ArgumentException("Capability transports must use the same configured engine identity");
        if (engine is not ("main" or "source")) throw new ArgumentException("Select the main or source engine", nameof(engine));
        _engine = engine; _clock = clock ?? TimeProvider.System; _timeout = timeout ?? TimeSpan.FromSeconds(20);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _authorization = identity.Authorization(privacy);
        _aboutTransport = trustedTestAboutTransport is null ? null : new(trustedTestAboutTransport);
    }

    public JsonObject Status() => new()
    {
        ["baseline"] = Baseline, ["target"] = Target, ["pin_promoted"] = false, ["native_qualified"] = false,
        ["event_mode"] = "authoritative polling",
        ["event_policy"] = "No event payload is treated as a complete library or queue snapshot. Omission/reconnect requires a full reconciliation.",
        ["last_check"] = _settings.Get(SettingKey, new JsonObject())
    };

    public async Task<JsonObject> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter();
        await using var session = _client.OpenSession(_owner.CancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.CancellationToken, session.CancellationToken); deadline.CancelAfter(_timeout);
        try
        {
            var about = await session.ReadAboutAsync(_authorization, _aboutTransport, deadline.Token).ConfigureAwait(false);
            // Validate identification before sending introspection, matching the released observation order.
            _ = Version(about);
            var data = await session.GraphQlAsync(Query, cancellationToken: deadline.Token).ConfigureAwait(false);
            var report = Report(about, data, _engine, _clock.GetUtcNow()); session.ThrowIfRetired(); deadline.Token.ThrowIfCancellationRequested();
            _settings.Set(SettingKey, report); return report;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_owner.CancellationToken.IsCancellationRequested && !session.CancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Engine capability observation reached its local deadline"); }
        catch (SourceWorkDeferredException) { throw; }
        catch (SourceEngineCapabilityException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or SuwayomiException or ObjectDisposedException)
        {
            session.ThrowIfRetired();
            deadline.Token.ThrowIfCancellationRequested();
            throw new SourceEngineCapabilityException("Engine capability observation failed; no complete report was saved");
        }
    }

    public static JsonObject Report(JsonNode? about, JsonObject data, string engine, DateTimeOffset checkedAt)
    {
        var version = Version(about);
        if (data["__schema"] is not JsonObject schema || schema["mutationType"] is not JsonObject mutations || mutations["fields"] is not JsonArray fields)
            throw new SourceEngineCapabilityException("Engine capability introspection is unavailable");
        if (fields.Count > 50000 || fields.Any(row => row is not JsonObject obj || obj["name"] is not JsonValue scalar || !scalar.TryGetValue<string>(out _)))
            throw new SourceEngineCapabilityException("Invalid engine capability fields");
        var names = fields.Select(row => row!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var required = new[] { "updateManga", "enqueueChapterDownloads", "fetchMangaAndChapters", "fetchChapterPages" }.Order(StringComparer.Ordinal).ToArray();
        var requiredFlags = new JsonObject(); foreach (var name in required) requiredFlags[name] = names.Contains(name);
        return new()
        {
            ["checked_at"] = Timestamp(checkedAt), ["engine"] = engine, ["version"] = version,
            ["required_mutations"] = requiredFlags, ["batch_queue_reorder"] = names.Contains("reorderChapterDownloads"),
            ["single_queue_reorder"] = names.Contains("reorderChapterDownload"), ["contract_ok"] = required.All(names.Contains),
            ["native_qualified"] = false, ["limit"] = Limit
        };
    }

    public Task StopAsync() { lock (_closeGate) return _stop ??= StopCoreAsync(_owner.StopAsync()); }
    private async Task StopCoreAsync(Task drain)
    {
        await Task.Yield();
        try { await drain.ConfigureAwait(false); }
        finally { _aboutTransport?.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _owner.DisposeAsync().ConfigureAwait(false); }
    }

    internal static string IdentifyVersion(JsonNode? about) => Version(about);

    private static string Version(JsonNode? about)
    {
        if (about is not JsonObject obj) throw new SourceEngineCapabilityException("Engine about endpoint returned an incomplete response");
        var selected = Truth(obj["version"]) ? obj["version"] : obj["serverVersion"];
        if (selected is not JsonValue scalar || !scalar.TryGetValue<string>(out var version) || version.Length == 0 || version.EnumerateRunes().Count() > 100)
            throw new SourceEngineCapabilityException("Engine did not identify its version");
        return version;
    }
    private static bool Truth(JsonNode? value) => value switch
    {
        null => false, JsonArray array => array.Count > 0, JsonObject obj => obj.Count > 0,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Length > 0,
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.Number => scalar.GetValue<double>() != 0, _ => true
    };
    private static string Timestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime(); var micros = utc.Ticks % TimeSpan.TicksPerSecond / 10;
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (micros == 0 ? "" : "." + micros.ToString("D6", CultureInfo.InvariantCulture)) + "+00:00";
    }
}
