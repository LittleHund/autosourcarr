using System.Text.Json.Serialization;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Api.Security;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AutoSourcarr.Api.Endpoints;

/// <summary>One effective setting. Secret values are never returned; <c>is_set</c> says whether one exists.</summary>
public sealed record SettingItem(string Key, string? Value, bool Secret, bool IsSet);

public sealed record SettingsOverview(IReadOnlyList<SettingItem> Items, bool ConfigFileExists);

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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SettingsOverview))]
[JsonSerializable(typeof(ConversionSummary))]
public sealed partial class ApiJsonContext : JsonSerializerContext;

/// <summary>What the typed /api/v1 endpoints need; tests and the OpenAPI generator pass stand-ins.</summary>
public sealed class ApiV1Services(Func<AutoSourcarrSettings> settings, string configurationRoot, ConversionReport? conversion)
{
    public Func<AutoSourcarrSettings> Settings { get; } = settings;
    public string ConfigurationRoot { get; } = configurationRoot;
    public ConversionReport? Conversion { get; } = conversion;

    public SettingsOverview Overview()
    {
        var values = SettingsLoader.ToEnvironment(Settings(), includeSecrets: true);
        var items = AutoSourcarrSettings.Definitions
            .Select(definition =>
            {
                var present = values.TryGetValue(definition.Key, out var value);
                return definition.Secret
                    ? new SettingItem(definition.Key, null, true, present && value!.Length > 0)
                    : new SettingItem(definition.Key, present ? value : null, false, present);
            })
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        return new(items, new ConfigStore(ConfigurationRoot).Exists);
    }

    public ConversionSummary ConversionSummary() => Conversion is { } report
        ? new(true, report.ConvertedAt, report.BackupDirectory, report.SettingKeys, report.SourceHostKeys, report.ExtraKeys,
            report.EncryptedKeys, report.InvalidKeys, report.KeysWithoutValue)
        : new(false, null, null, [], [], [], [], [], []);
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

        group.MapGet("/settings", (Func<HttpContext, IResult>)(_ =>
                Results.Json(services.Overview(), ApiJsonContext.Default.SettingsOverview)))
            .WithName("getSettings")
            .WithSummary("Effective settings; secret values are hidden.")
            .Produces<SettingsOverview>(StatusCodes.Status200OK);

        group.MapGet("/settings/conversion", (Func<HttpContext, IResult>)(_ =>
                Results.Json(services.ConversionSummary(), ApiJsonContext.Default.ConversionSummary)))
            .WithName("getSettingsConversion")
            .WithSummary("What this start converted from autosourcarr.env to config.json.")
            .Produces<ConversionSummary>(StatusCodes.Status200OK);

        group.MapGet("/openapi.json", (Func<HttpContext, IResult>)(context =>
                Results.Text(OpenApiDocumentBuilder.Build(context.RequestServices.GetRequiredService<EndpointDataSource>()).ToJsonString(),
                    "application/json; charset=utf-8")))
            .ExcludeFromDescription();
        return group;
    }
}
