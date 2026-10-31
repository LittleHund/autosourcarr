using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using AutoSourcarr.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace AutoSourcarr.Api.Http;

/// <summary>
/// Builds the OpenAPI 3.1 document for the typed /api/v1 contract from the registered endpoints.
/// Only endpoints marked with <see cref="ApiContractMetadata"/> are described; schemas come from the
/// same source-generated JSON contracts the endpoints serialize with, so the document cannot drift.
/// </summary>
public static class OpenApiDocumentBuilder
{
    public const string Title = "AutoSourcarr API";
    public const string ContractVersion = "1";

    public static JsonObject Build(EndpointDataSource source) => Build(source.Endpoints);

    public static JsonObject Build(IEnumerable<Endpoint> endpoints)
    {
        var paths = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var schemas = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var metadata = endpoint.Metadata;
            if (metadata.GetMetadata<ApiContractMetadata>() is null
                || metadata.GetMetadata<IExcludeFromDescriptionMetadata>() is { ExcludeFromDescription: true })
                continue;
            var path = Path(endpoint.RoutePattern);
            var item = paths.TryGetValue(path, out var existing) ? existing : paths[path] = new JsonObject();
            foreach (var method in metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                item[method.ToLowerInvariant()] = Operation(endpoint, method, schemas);
        }

        var pathsNode = new JsonObject();
        foreach (var (path, item) in paths)
            pathsNode[path] = Sorted(item);
        var schemasNode = new JsonObject();
        foreach (var (name, schema) in schemas)
            schemasNode[name] = schema;
        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject { ["title"] = Title, ["version"] = ContractVersion },
            ["paths"] = pathsNode,
            ["components"] = new JsonObject
            {
                ["schemas"] = schemasNode,
                ["securitySchemes"] = new JsonObject
                {
                    ["adminToken"] = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = "X-Admin-Token" }
                }
            },
            ["security"] = new JsonArray(new JsonObject { ["adminToken"] = new JsonArray() })
        };
    }

    private static JsonObject Operation(RouteEndpoint endpoint, string method, IDictionary<string, JsonNode> schemas)
    {
        var metadata = endpoint.Metadata;
        var operation = new JsonObject
        {
            ["operationId"] = metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? DerivedId(method, endpoint.RoutePattern)
        };
        if (metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary is { Length: > 0 } summary)
            operation["summary"] = summary;

        var parameters = new JsonArray();
        foreach (var parameter in endpoint.RoutePattern.Parameters)
            parameters.Add(new JsonObject
            {
                ["name"] = parameter.Name,
                ["in"] = "path",
                ["required"] = true,
                ["schema"] = new JsonObject { ["type"] = "string" }
            });
        if (parameters.Count > 0)
            operation["parameters"] = parameters;

        if (metadata.GetMetadata<IAcceptsMetadata>() is { RequestType: { } requestType } accepts)
        {
            var content = new JsonObject();
            foreach (var contentType in accepts.ContentTypes.DefaultIfEmpty("application/json"))
                content[contentType] = new JsonObject { ["schema"] = new JsonObject { ["$ref"] = Schema(requestType, schemas) } };
            operation["requestBody"] = new JsonObject { ["required"] = !accepts.IsOptional, ["content"] = content };
        }

        var responses = new JsonObject();
        foreach (var produced in metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().OrderBy(m => m.StatusCode))
        {
            var response = new JsonObject { ["description"] = produced.StatusCode is >= 200 and < 300 ? "Success" : "Error" };
            if (produced.Type is { } type && type != typeof(void))
            {
                var reference = Schema(type, schemas);
                var content = new JsonObject();
                foreach (var contentType in produced.ContentTypes.DefaultIfEmpty("application/json"))
                    content[contentType] = new JsonObject { ["schema"] = new JsonObject { ["$ref"] = reference } };
                response["content"] = content;
            }
            responses[produced.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)] = response;
        }
        responses["401"] = new JsonObject { ["description"] = "Invalid admin token" };
        operation["responses"] = responses;
        return operation;
    }

    private static string Schema(Type type, IDictionary<string, JsonNode> schemas)
    {
        if (!schemas.ContainsKey(type.Name))
        {
            var options = ApiJsonContext.Default.Options;
            schemas[type.Name] = JsonSchemaExporter.GetJsonSchemaAsNode(options, type,
                new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        }
        return "#/components/schemas/" + type.Name;
    }

    private static string Path(RoutePattern pattern) => "/" + string.Join('/', pattern.PathSegments.Select(segment =>
        string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternParameterPart parameter => "{" + parameter.Name + "}",
            RoutePatternSeparatorPart separator => separator.Content,
            _ => ""
        }))));

    private static string DerivedId(string method, RoutePattern pattern)
    {
        var words = Path(pattern).Split('/', '-', '{', '}', '.').Where(word => word.Length > 0 && word is not ("api" or "v1"));
        return method.ToLowerInvariant() + string.Concat(words.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static JsonObject Sorted(JsonObject item)
    {
        var sorted = new JsonObject();
        foreach (var (key, value) in item.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray())
        {
            item.Remove(key);
            sorted[key] = value;
        }
        return sorted;
    }
}
