using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>One setting on the Settings page. Secret values are never returned; <c>is_set</c> says whether one exists.</summary>
public sealed record SettingItem(
    string Key, string Label, string Description, string Group, string Type, string? Value, bool Secret, bool IsSet,
    string? Default, string ComposeVariable, bool ComposeRecommended, bool OverriddenByEnvironment, string? WizardStep,
    bool Advanced, double? Minimum, double? Maximum, IReadOnlyList<SettingChoice> Choices, bool Editable, bool SavedInApp, string? Problem);

public sealed record SettingsGroup(string Name, IReadOnlyList<SettingItem> Items);

public sealed record SettingsOverview(string Revision, bool ConfigFileExists, IReadOnlyList<SettingsGroup> Groups);

public sealed record SettingsSaveRequest(string Revision, Dictionary<string, JsonElement> Values);

public sealed record SettingChangeItem(string Section, string Key, string Kind, bool Secret);

public sealed record SettingsSaveResponse(string Revision, IReadOnlyList<SettingChangeItem> Changes, IReadOnlyList<string> ChangedKeys,
    bool RestartRequired, IReadOnlyList<string> OverriddenByEnvironment, string Message);

public sealed record SettingsSchemaResponse(IReadOnlyList<SettingSchemaEntry> Settings);

/// <summary>Result of the one-time autosourcarr.env → config.json conversion (key names only).</summary>
public sealed record ConversionSummary(
    bool Converted,
    string? ConvertedAt,
    string? BackupDirectory,
    IReadOnlyList<string> SettingKeys,
    IReadOnlyList<string> SourceHostKeys,
    IReadOnlyList<string> ExtraKeys,
    IReadOnlyList<string> EncryptedKeys,
    IReadOnlyList<string> InvalidKeys,
    IReadOnlyList<string> KeysWithoutValue);

public sealed record UpdateNoticeResponse(bool Present, UpdateNotice? Notice);

public sealed record SetupView(bool Completed, string Revision, IReadOnlyList<JsonObject> Fields, JsonObject? Cleanup,
    bool RestartRequired, bool Integrated, bool CanRestart, ReaderConfigResult ReaderBuild);

public sealed record SetupFinishRequest(string Revision, string Transaction, Dictionary<string, JsonElement>? Changes);

public sealed record SetupFinishResponse(bool Completed, IReadOnlyList<string> Changed, bool RestartRequired, string Transaction,
    ReaderConfigResult ReaderBuild, bool CanRestart, IReadOnlyList<SettingChangeItem> Changes);

public sealed record ReaderPreflightRequest(string Revision, Dictionary<string, JsonElement>? Changes);

public sealed record ReaderPreflightResponse(string Revision, ReaderConfigResult ReaderBuild);

public sealed record RestartResponse(bool Restarting);

public sealed record ConfigView(string SourceSuwayomiUrl, string AutosourcarrPublicUrl, string MainSuwayomiUrl, double AutoMatchThreshold,
    double FederatedSourceTimeout, double FederatedOverallTimeout, IReadOnlyList<string> SourceLanguages, bool MetadataIdentityEnabled,
    IReadOnlyList<string> MetadataIdentityProviders, bool MetadataIdentityIncludeAdult, bool AdminTokenRequired);

public sealed record DatabaseStorageView(string ActiveDir, string? ConfiguredDir, long CatalogBytes, long OriginsBytes, long Bytes,
    bool MoveAvailable, string MoveNote, JsonObject? PendingRelocation);

public sealed record ProblemResponse(string Detail);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SettingsOverview))]
[JsonSerializable(typeof(SettingsSaveRequest))]
[JsonSerializable(typeof(SettingsSaveResponse))]
[JsonSerializable(typeof(SettingsSchemaResponse))]
[JsonSerializable(typeof(ConversionSummary))]
[JsonSerializable(typeof(UpdateNoticeResponse))]
[JsonSerializable(typeof(SetupView))]
[JsonSerializable(typeof(SetupFinishRequest))]
[JsonSerializable(typeof(SetupFinishResponse))]
[JsonSerializable(typeof(ReaderPreflightRequest))]
[JsonSerializable(typeof(ReaderPreflightResponse))]
[JsonSerializable(typeof(RestartResponse))]
[JsonSerializable(typeof(ConfigView))]
[JsonSerializable(typeof(DatabaseStorageView))]
[JsonSerializable(typeof(RuntimeMetricsSnapshot))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(ProblemResponse))]
[JsonSerializable(typeof(string))]
public sealed partial class ApiJsonContext : JsonSerializerContext;

/// <summary>What the typed /api/v1 endpoints need; tests and the OpenAPI generator pass stand-ins.</summary>
public sealed class ApiV1Services
{
    public ApiV1Services(Func<AutoSourcarrSettings> settings, string configurationRoot, ConversionReport? conversion,
        IReadOnlyDictionary<string, string?>? environment = null, Action<IReadOnlyDictionary<string, string?>>? registerSecrets = null,
        CatalogueStatusStats? status = null, Action? stop = null, Func<bool>? bundledImage = null,
        Func<CancellationToken, Task<JsonObject>>? health = null)
    {
        Health = health;
        Settings = settings;
        ConfigurationRoot = configurationRoot;
        Conversion = conversion;
        Environment = environment ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        Editor = new SettingsEditor(configurationRoot, settings, Environment, registerSecrets);
        Status = status;
        Stop = stop;
        BundledImage = bundledImage ?? (() => File.Exists("/opt/autosourcarr-image-version"));
    }

    public Func<AutoSourcarrSettings> Settings { get; }
    public string ConfigurationRoot { get; }
    public ConversionReport? Conversion { get; }
    public IReadOnlyDictionary<string, string?> Environment { get; }
    public SettingsEditor Editor { get; }
    public CatalogueStatusStats? Status { get; }
    public Func<CancellationToken, Task<JsonObject>>? Health { get; }
    public Action? Stop { get; }
    public Func<bool> BundledImage { get; }
    internal int Restarting;

    public bool CanRestart => Stop is not null && BundledImage() && Settings().AppServicesMode == "integrated";

    public SettingsOverview Overview()
    {
        var revision = Editor.Revision();
        var saved = Editor.SavedSettings();
        var effective = Editor.Effective();
        var managed = Editor.ManagedKeys();
        var groups = SettingsSchema.Entries
            .GroupBy(entry => entry.Group)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new SettingsGroup(group.Key, group.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry =>
            {
                var value = effective.GetValueOrDefault(entry.Key);
                var overridden = Environment.GetValueOrDefault(entry.Key) is not null && !managed.Contains(entry.Key);
                return new SettingItem(entry.Key, entry.Label, entry.Description, entry.Group, entry.Type,
                    entry.Secret ? null : value, entry.Secret, entry.Secret ? !string.IsNullOrEmpty(value) : saved.ContainsKey(entry.Key),
                    entry.Default, entry.ComposeVariable, entry.ComposeRecommended, overridden,
                    entry.WizardStep, entry.Advanced, entry.Minimum, entry.Maximum, entry.Choices, entry.Editable, managed.Contains(entry.Key),
                    entry.Secret ? null : Problem(entry, value));
            }).ToArray()))
            .ToArray();
        return new(revision, new ConfigStore(ConfigurationRoot).Exists, groups);
    }

    /// <summary>A value that the Settings page would refuse, e.g. a Compose variable outside the schema's limits.</summary>
    private static string? Problem(SettingSchemaEntry entry, string? value)
    {
        if (string.IsNullOrEmpty(value) || entry.Type is "url" or "translation" or "path")
            return null; // addresses and folders are checked where they are used; empty is allowed there
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, ApiJsonContext.Default.String));
            SettingsEditor.Normalize(entry, document.RootElement);
            return null;
        }
        catch (SettingsValidationException error) { return error.Message; }
    }

    public ConversionSummary ConversionSummary() => (Conversion ?? EnvConversion.Saved(ConfigurationRoot)) is { } report
        ? new(true, report.ConvertedAt, report.BackupDirectory, report.SettingKeys, report.SourceHostKeys, report.ExtraKeys,
            report.EncryptedKeys, report.InvalidKeys, report.KeysWithoutValue)
        : new(false, null, null, [], [], [], [], [], []);

    public SetupView Setup()
    {
        var revision = Editor.Revision();
        var effective = Editor.Effective();
        var state = Editor.SetupState();
        var fields = new List<JsonObject>();
        foreach (var (key, definition) in SetupFields.ByKey.OrderBy(pair => SetupOrder(pair.Key)))
        {
            var field = JsonNode.Parse(definition.GetRawText())!.AsObject();
            if (key == SettingsEditor.ExtensionStores)
                field["value"] = new JsonArray(Editor.ExtensionRepositories().Select(url => (JsonNode?)JsonValue.Create(url)).ToArray());
            else
            {
                var text = effective.GetValueOrDefault(key) ?? "";
                var kind = field["kind"]?.GetValue<string>();
                if (kind == "secret")
                {
                    field["value"] = "";
                    field["configured"] = text.Length > 0;
                }
                else field["value"] = kind switch
                {
                    "boolean" => text is "true" or "1" or "yes" or "on",
                    "integer" => long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var whole) ? whole : 0,
                    "number" => double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0,
                    _ => text
                };
            }
            fields.Add(field);
        }
        return new(state?.Completed == true, revision, fields, null, state?.RestartRequired == true,
            Settings().AppServicesMode == "integrated", CanRestart, Reader(effective));
    }

    public ReaderConfigResult Reader(IReadOnlyDictionary<string, string?> values) => ReaderConfigCheck.Evaluate(values,
        allowInsecure: Environment.GetValueOrDefault("AUTOSOURCARR_ALLOW_INSECURE_READER_API") == "1");

    public ConfigView Config()
    {
        var settings = Settings();
        var providers = settings.MetadataIdentityProviders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(provider => provider.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        return new(settings.EffectiveSourceUrl, settings.AutosourcarrPublicUrl, settings.MainSuwayomiUrl, settings.AutoMatchThreshold,
            settings.FederatedSourceTimeout, settings.FederatedOverallTimeout, settings.AllowedLanguages.Order(StringComparer.Ordinal).ToArray(),
            settings.MetadataIdentityEnabled, providers, settings.MetadataIdentityIncludeAdult, settings.AdminToken.Length > 0);
    }

    public DatabaseStorageView DatabaseStorage()
    {
        var settings = Settings();
        var paths = DatabasePaths.Resolve(settings);
        static long Size(string path) => new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists).Sum(file => new FileInfo(file).Length);
        var catalog = Size(paths.Catalog);
        var origins = Size(paths.Origins);
        return new(paths.Directory, settings.DatabaseDir, catalog, origins, catalog + origins, false,
            "Moving the databases from here arrives with the new data layer (conversion phase 2). Until then keep DATABASE_DIR unchanged; changing it alone would start with empty databases.",
            null);
    }

    private static int SetupOrder(string key)
    {
        var index = 0;
        foreach (var name in SetupFields.ByKey.Keys)
        {
            if (name == key)
                return index;
            index++;
        }
        return index;
    }
}

/// <summary>Marks endpoints that belong to the typed /api/v1 contract described by openapi.json.</summary>
public sealed class ApiContractMetadata
{
    public static ApiContractMetadata Instance { get; } = new();
    private ApiContractMetadata() { }
}

public static class ApiV1Endpoints
{
    public static RouteGroupBuilder MapApiV1(this IEndpointRouteBuilder endpoints, ApiV1Services services)
    {
        var group = endpoints.MapGroup("/api/v1")
            .AddEndpointFilter(new AdminAuthorizationFilter(services.Settings))
            .WithMetadata(ApiContractMetadata.Instance);
        var json = ApiJsonContext.Default;

        group.MapGet("/settings", (Func<HttpContext, IResult>)(_ => Results.Json(services.Overview(), json.SettingsOverview)))
            .WithName("getSettings")
            .WithSummary("Effective settings grouped as on the Settings page; secret values are hidden.")
            .Produces<SettingsOverview>(StatusCodes.Status200OK);

        group.MapPut("/settings", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                var body = await Read(context, json.SettingsSaveRequest).ConfigureAwait(false);
                if (body?.Values is null || body.Revision is null) return Problem(400, "Send the revision and the changed values.");
                return Guarded(() =>
                {
                    var result = services.Editor.Save(body.Values, body.Revision);
                    var message = result.ChangedKeys.Count == 0 ? "Nothing changed."
                        : "Saved to config.json. Restart AutoSourcarr to apply the changes."
                          + (result.OverriddenByEnvironment.Count > 0 ? " Some of them are overridden by Compose variables and stay without effect until those are removed: " + string.Join(", ", result.OverriddenByEnvironment) + "." : "");
                    return Results.Json(new SettingsSaveResponse(result.Revision, Changes(result), result.ChangedKeys, result.RestartRequired,
                        result.OverriddenByEnvironment, message), json.SettingsSaveResponse);
                });
            }))
            .WithName("saveSettings")
            .WithSummary("Save changed settings to config.json. Null resets a setting to its default; an empty secret removes it.")
            .Accepts<SettingsSaveRequest>("application/json")
            .Produces<SettingsSaveResponse>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapGet("/settings/schema", (Func<HttpContext, IResult>)(_ =>
                Results.Json(new SettingsSchemaResponse(SettingsSchema.Entries), json.SettingsSchemaResponse)))
            .WithName("getSettingsSchema")
            .WithSummary("The settings schema: name, type, default, description, secret, Compose variable and wizard step.")
            .Produces<SettingsSchemaResponse>(StatusCodes.Status200OK);

        group.MapGet("/settings/conversion", (Func<HttpContext, IResult>)(_ =>
                Results.Json(services.ConversionSummary(), json.ConversionSummary)))
            .WithName("getSettingsConversion")
            .WithSummary("What this start converted from autosourcarr.env to config.json.")
            .Produces<ConversionSummary>(StatusCodes.Status200OK);

        group.MapGet("/settings/update-notice", (Func<HttpContext, IResult>)(_ =>
            {
                var notice = UpdateNotices.Current(services.ConfigurationRoot);
                return Results.Json(new UpdateNoticeResponse(notice is not null, notice), json.UpdateNoticeResponse);
            }))
            .WithName("getUpdateNotice")
            .WithSummary("What the last update changed in your settings, until dismissed.")
            .Produces<UpdateNoticeResponse>(StatusCodes.Status200OK);

        group.MapPost("/settings/update-notice/dismiss", (Func<HttpContext, IResult>)(_ =>
            {
                UpdateNotices.Dismiss(services.ConfigurationRoot);
                return Results.Json(new UpdateNoticeResponse(false, null), json.UpdateNoticeResponse);
            }))
            .WithName("dismissUpdateNotice")
            .WithSummary("Hide the update notice.")
            .Produces<UpdateNoticeResponse>(StatusCodes.Status200OK);

        group.MapGet("/setup", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Guarded(() => Results.Json(services.Setup(), json.SetupView));
            }))
            .WithName("getSetup")
            .WithSummary("Setup wizard fields with the current values; secrets show only whether one is saved.")
            .Produces<SetupView>(StatusCodes.Status200OK);

        group.MapPost("/setup/reader-preflight", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var body = await Read(context, json.ReaderPreflightRequest).ConfigureAwait(false);
                if (body?.Revision is null) return Problem(400, "Send the revision and the draft changes.");
                return Guarded(() =>
                {
                    if (body.Revision != services.Editor.Revision())
                        throw new SettingsConflictException("Settings changed elsewhere. Reload setup before checking the reader draft.");
                    var values = new Dictionary<string, string?>(services.Editor.Effective(), StringComparer.Ordinal);
                    foreach (var (key, raw) in body.Changes ?? new Dictionary<string, JsonElement>())
                    {
                        if (!SetupFields.ByKey.ContainsKey(key))
                            throw new SettingsValidationException("Reader check contains an unsupported setting.");
                        if (ReaderConfigCheck.Keys.Contains(key))
                            values[key] = SettingsEditor.Normalize(SettingsSchema.ByKey[key], raw) ?? "";
                    }
                    return Results.Json(new ReaderPreflightResponse(body.Revision, services.Reader(values)), json.ReaderPreflightResponse);
                });
            }))
            .WithName("checkSetupReader")
            .WithSummary("Check draft reader addresses and key without saving them.")
            .Accepts<ReaderPreflightRequest>("application/json")
            .Produces<ReaderPreflightResponse>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/setup/finish", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var body = await Read(context, json.SetupFinishRequest).ConfigureAwait(false);
                if (body?.Revision is null || body.Transaction is null) return Problem(400, "Send the revision, a request id and the changes.");
                return Guarded(() =>
                {
                    var changes = body.Changes ?? new Dictionary<string, JsonElement>();
                    if (changes.Keys.Any(key => !SetupFields.ByKey.ContainsKey(key)))
                        throw new SettingsValidationException("Setup contains an unsupported setting.");
                    var result = services.Editor.Save(changes, body.Revision, body.Transaction);
                    return Results.Json(new SetupFinishResponse(true, result.ChangedKeys, result.RestartRequired, body.Transaction,
                        services.Reader(services.Editor.Effective()), services.CanRestart, Changes(result)), json.SetupFinishResponse);
                });
            }))
            .WithName("finishSetup")
            .WithSummary("Save the wizard's changes to config.json in one step; a repeated request id returns the first result.")
            .Accepts<SetupFinishRequest>("application/json")
            .Produces<SetupFinishResponse>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status400BadRequest)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/setup/restart", (Func<HttpContext, IResult>)(context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!services.CanRestart)
                    return Problem(409, "Restart this AutoSourcarr service from your container manager to apply settings.");
                if (services.Editor.SetupState()?.Completed != true)
                    return Problem(409, "Finish setup before restarting.");
                if (Interlocked.Exchange(ref services.Restarting, 1) == 0)
                {
                    // A normal shutdown runs every cleanup step; Docker's restart policy starts the container again.
                    var stop = services.Stop!;
                    _ = Task.Delay(TimeSpan.FromSeconds(1.5)).ContinueWith(_ => stop(), TaskScheduler.Default);
                }
                return Results.Json(new RestartResponse(true), json.RestartResponse);
            }))
            .WithName("restartAfterSetup")
            .WithSummary("Restart the integrated image to apply saved settings.")
            .Produces<RestartResponse>(StatusCodes.Status200OK)
            .Produces<ProblemResponse>(StatusCodes.Status409Conflict);

        group.MapGet("/config", (Func<HttpContext, IResult>)(_ => Results.Json(services.Config(), json.ConfigView)))
            .WithName("getConfig")
            .WithSummary("The running values the UI needs to describe this instance.")
            .Produces<ConfigView>(StatusCodes.Status200OK);

        group.MapGet("/storage/database", (Func<HttpContext, IResult>)(_ => Guarded(() => Results.Json(services.DatabaseStorage(), json.DatabaseStorageView))))
            .WithName("getDatabaseStorage")
            .WithSummary("Where the databases are and how large they are.")
            .Produces<DatabaseStorageView>(StatusCodes.Status200OK);

        group.MapGet("/status/stats", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (services.Status is null) return Problem(503, "Status counts are not available in this host.");
                var value = await services.Status.SnapshotAsync(context.RequestAborted).ConfigureAwait(false);
                return Results.Json(value, json.JsonObject);
            }))
            .WithName("getStatusStats")
            .WithSummary("Catalogue, chapter, source, cache and storage counts; refreshed at most every 15 seconds.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapGet("/health", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (services.Health is null) return Problem(503, "Health is not available in this host.");
                return Results.Json(await services.Health(context.RequestAborted).ConfigureAwait(false), json.JsonObject);
            }))
            .WithName("getHealth")
            .WithSummary("Status page: version, databases, catalogue counts, active errors and dependency checks.")
            .Produces<JsonObject>(StatusCodes.Status200OK);

        group.MapGet("/diagnostics/metrics", (Func<HttpContext, IResult>)(_ =>
                Results.Json(RuntimeMetrics.Shared.Snapshot(), json.RuntimeMetricsSnapshot)))
            .WithName("getRuntimeMetrics")
            .WithSummary("Timing samples per work phase (no URLs, titles or keys).")
            .Produces<RuntimeMetricsSnapshot>(StatusCodes.Status200OK);

        group.MapGet("/openapi.json", (Func<HttpContext, IResult>)(context =>
                Results.Text(OpenApiDocumentBuilder.Build(context.RequestServices.GetRequiredService<EndpointDataSource>()).ToJsonString(),
                    "application/json; charset=utf-8")))
            .ExcludeFromDescription();
        return group;
    }

    private static IReadOnlyList<SettingChangeItem> Changes(SettingsSaveResult result) =>
        result.Changes.Select(change => new SettingChangeItem(change.Section, change.Key, change.Kind, change.Secret)).ToArray();

    private static IResult Problem(int status, string detail) =>
        Results.Json(new ProblemResponse(detail), ApiJsonContext.Default.ProblemResponse, statusCode: status);

    private static IResult Guarded(Func<IResult> action)
    {
        try { return action(); }
        catch (SettingsValidationException error) { return Problem(400, error.Message); }
        catch (SettingsConflictException error) { return Problem(409, error.Message); }
        catch (ConfigurationException error) { return Problem(409, error.Message + " No settings were changed."); }
        catch (IOException) { return Problem(409, "Could not read or write config.json. Check that the config folder is mounted and writable; no settings were changed."); }
        catch (UnauthorizedAccessException) { return Problem(409, "config.json is not writable. Check the config folder permissions; no settings were changed."); }
    }

    private static async Task<T?> Read<T>(HttpContext context, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (context.Request.ContentLength is > 1024 * 1024)
            return null;
        try { return await JsonSerializer.DeserializeAsync(context.Request.Body, type, context.RequestAborted).ConfigureAwait(false); }
        catch (JsonException) { return null; }
    }
}
