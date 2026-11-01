using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Collections;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Translation;

/// <summary>
/// translation.py TranslationClient: a LibreTranslate-compatible client (POST {base}/translate {q, source, target,
/// format, api_key}; GET {base}/languages for health). TRANSLATION_MODE off disables it; builtin (or auto with integrated
/// app services and a TRANSLATION_API_URL) uses the bundled service on 127.0.0.1:5000 without a key; otherwise
/// TRANSLATION_API_URL with TRANSLATION_API_KEY. Results are cached durably in translation_cache and in a 4096-entry hot
/// set. MAT-20: a translation is a search hint only — it is never stored as a title, alias or match evidence and never
/// displayed as the title; callers add it to transient source-search queries. Every failure answers "" (no translation).
/// </summary>
public sealed class TranslationClient : IDisposable
{
    public const string BuiltinUrl = "http://127.0.0.1:5000";
    private readonly SqliteDatabase _database;
    private readonly Func<AutoSourcarrSettings> _settings;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly BoundedLruCache<(string Text, string Target), string> _memory = new(4096);
    private readonly SemaphoreSlim _slots;

    /// <param name="handler">The HTTP handler (tests pass a fake); default: no redirects.</param>
    public TranslationClient(SqliteDatabase database, Func<AutoSourcarrSettings> settings, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(settings);
        _database = database; _settings = settings; _clock = clock ?? TimeProvider.System;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) }, disposeHandler: true)
        // A translation or the language list is small; a larger answer is refused instead of buffered.
        { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };
        var slots = Math.Max(1, settings().TranslationConcurrency);
        _slots = new SemaphoreSlim(slots, slots);
    }

    /// <summary>Whether the bundled service is used (no API key is sent to it).</summary>
    public bool Builtin
    {
        get
        {
            var settings = _settings();
            return settings.TranslationMode == "builtin"
                || settings.TranslationMode == "auto" && settings.AppServicesMode == "integrated" && settings.TranslationApiUrl.Trim().Length > 0;
        }
    }

    /// <summary>The service address in use; empty when translation is off or not configured.</summary>
    public string BaseUrl => _settings().TranslationMode == "off" ? "" : Builtin ? BuiltinUrl : _settings().TranslationApiUrl.Trim().TrimEnd('/');

    public bool Enabled => BaseUrl.Length > 0;

    /// <summary>The cached translation of <paramref name="text"/> into <paramref name="target"/>, or "" (no network).</summary>
    public string Cached(string text, string target)
    {
        text = (text ?? "").Trim(); target = (target ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0 || target.Length == 0) return "";
        if (_memory.TryGet((text, target), out var hot)) return hot;
        var value = _database.Read(DatabaseFile.Catalog, s => s.Scalar("SELECT translated_text FROM translation_cache WHERE source_text=$text AND target_lang=$target",
            ("$text", text), ("$target", target)) as string) ?? "";
        if (value.Length > 0) _memory.Set((text, target), value);
        return value;
    }

    /// <summary>
    /// translate: the cached value, else one bounded request (TRANSLATION_TIMEOUT, TRANSLATION_CONCURRENCY at a time). A
    /// translation that normalizes to the original is not one. Answers "" on any failure or when translation is off.
    /// </summary>
    public async Task<string> TranslateAsync(string text, string target, string source = "auto", CancellationToken cancellationToken = default)
    {
        text = (text ?? "").Trim(); target = (target ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0 || target.Length == 0) return "";
        var cached = Cached(text, target);
        if (cached.Length > 0 || !Enabled) return cached;
        var settings = _settings();
        var payload = new JsonObject { ["q"] = text, ["source"] = string.IsNullOrWhiteSpace(source) ? "auto" : source, ["target"] = target, ["format"] = "text" };
        if (settings.TranslationApiKey.Length > 0 && !Builtin) payload["api_key"] = settings.TranslationApiKey;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.5, settings.TranslationTimeout)));
            await _slots.WaitAsync(deadline.Token).ConfigureAwait(false);
            string translated;
            try
            {
                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(BaseUrl + "/translate", content, deadline.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return "";
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)) as JsonObject;
                translated = (body?["translatedText"] is JsonValue value && value.TryGetValue<string>(out var answer) ? answer : "").Trim();
            }
            finally { _slots.Release(); }
            if (translated.Length == 0 || TextMatcher.NormalizeTitle(translated) == TextMatcher.NormalizeTitle(text)) return "";
            Persist(text, target, source, translated);
            _memory.Set((text, target), translated);
            return translated;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return "";
        }
    }

    /// <summary>health: {ok, configured, url, languages | error}, bounded by min(3 s, TRANSLATION_TIMEOUT).</summary>
    public async Task<JsonObject> HealthAsync(CancellationToken cancellationToken = default)
    {
        var url = BaseUrl;
        if (url.Length == 0) return new JsonObject { ["ok"] = false, ["configured"] = false, ["url"] = "", ["error"] = "Translation service not configured" };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings().TranslationTimeout, 0.5, 3.0)));
            using var response = await _http.GetAsync(url + "/languages", deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var languages = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
            return new JsonObject { ["ok"] = true, ["configured"] = true, ["url"] = url, ["languages"] = languages is JsonArray list ? list.Count : 0 };
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new JsonObject { ["ok"] = false, ["configured"] = true, ["url"] = url, ["error"] = error is OperationCanceledException ? "Timed out"
                // /api/v1/health is a guest read: the error kind only, not the remote message.
                : "Request failed (" + error.GetType().Name + ")" };
        }
    }

    private void Persist(string text, string target, string source, string translated) =>
        _database.Write(DatabaseFile.Catalog, s => s.Execute("""
            INSERT INTO translation_cache(source_text,source_lang,target_lang,translated_text,provider,updated_at) VALUES($text,$source,$target,$translated,'libretranslate',$at)
            ON CONFLICT(source_text,target_lang) DO UPDATE SET translated_text=excluded.translated_text,source_lang=excluded.source_lang,
              provider=excluded.provider,updated_at=excluded.updated_at
            """, ("$text", text), ("$source", string.IsNullOrWhiteSpace(source) ? "auto" : source), ("$target", target), ("$translated", translated),
            ("$at", _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture))));

    public void Dispose() { _http.Dispose(); _slots.Dispose(); }
}
