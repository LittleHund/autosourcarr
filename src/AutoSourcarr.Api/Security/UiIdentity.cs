using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Unicode;
using Microsoft.AspNetCore.Http;

namespace AutoSourcarr.Api.Security;

/// <summary>Preference profile identity only; proxy headers never grant admin access.</summary>
public sealed record UiIdentity(string Key, string User, string Header, bool Authelia, string Email,
    IReadOnlyList<string> Groups, IReadOnlyList<string> IdentityHeaders, bool ProxyDetected, IReadOnlyList<string> ProxyHeaders,
    string CustomHeader, string Host, bool DirectAccess)
{
    private static readonly (string Name, string Label)[] FallbackHeaders = [
        ("x-forwarded-user", "X-Forwarded-User"), ("x-auth-request-user", "X-Auth-Request-User"), ("x-remote-user", "X-Remote-User"),
        ("x-forwarded-username", "X-Forwarded-Username"), ("x-auth-request-username", "X-Auth-Request-Username"),
        ("x-forwarded-preferred-username", "X-Forwarded-Preferred-Username"), ("x-auth-request-preferred-username", "X-Auth-Request-Preferred-Username"),
        ("x-webauth-user", "X-Webauth-User"), ("x-user", "X-User"), ("x-username", "X-Username")];

    public static UiIdentity Resolve(HttpRequest request, AutoSourcarrSettings settings)
    {
        var custom = settings.AutheliaUserHeader.Trim();
        var primary = new List<(string Name, string Label)> { ("remote-user", "Remote-User") };
        if (custom.Length > 0 && !custom.Equals("remote-user", StringComparison.OrdinalIgnoreCase)) primary.Insert(0, (custom.ToLowerInvariant(), custom));
        var candidates = primary.Concat(FallbackHeaders).DistinctBy(header => header.Name, StringComparer.OrdinalIgnoreCase);
        var email = Value(request, "remote-email");
        var groups = request.Headers["remote-groups"].ToString().Trim().Split(',').Select(group => group.Trim()).Where(group => group.Length > 0).Take(64).ToArray();
        var present = new List<string>();
        foreach (var (name, label) in new[] { ("remote-user", "Remote-User"), ("remote-email", "Remote-Email"), ("remote-groups", "Remote-Groups") })
            if (request.Headers[name].ToString().Trim().Length > 0) present.Add(label);
        var proxy = request.Headers.Keys.Select(key => key.ToLowerInvariant()).Where(key => key.StartsWith("x-forwarded-", StringComparison.Ordinal)
            || key.StartsWith("x-auth-request-", StringComparison.Ordinal) || key.StartsWith("remote-", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(32).ToArray();
        var host = UnicodeCaseFold.Truncate(request.Headers.Host.ToString(), 256);
        foreach (var (name, label) in candidates)
        {
            if (request.Headers[name].ToString().Trim().Length == 0) continue;
            var value = Value(request, name);
            return new("authelia:" + UnicodeCaseFold.Fold(value), value, label, true, email, groups,
                present.Append(label).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), proxy.Length > 0, proxy, custom, host, false);
        }
        foreach (var (name, label) in new[] { ("remote-email", "Remote-Email"), ("x-forwarded-email", "X-Forwarded-Email"), ("x-auth-request-email", "X-Auth-Request-Email") })
        {
            if (request.Headers[name].ToString().Trim().Length == 0) continue;
            var value = Value(request, name);
            return new("authelia-email:" + UnicodeCaseFold.Fold(value), value, label, true, value, groups,
                present.Append(label).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), true, proxy, custom, host, false);
        }
        return new("local:default", "Local user", "", false, "", [], present, proxy.Length > 0, proxy, custom, host, proxy.Length == 0);
    }

    public JsonObject PreferenceResponse(JsonObject values) => new()
    {
        ["user"] = User, ["identity_header"] = Header, ["authelia"] = Authelia,
        ["identity_diagnostics"] = new JsonObject { ["proxy_detected"] = ProxyDetected, ["proxy_headers"] = Array(ProxyHeaders),
            ["identity_headers_present"] = Array(IdentityHeaders), ["custom_header"] = CustomHeader, ["remote_email"] = Email,
            ["remote_groups"] = Array(Groups), ["host"] = Host, ["direct_access"] = DirectAccess },
        ["values"] = values.DeepClone()
    };
    public JsonObject Context() => new() { ["key"] = Key, ["user"] = User, ["header"] = Header, ["authelia"] = Authelia, ["email"] = Email,
        ["groups"] = Array(Groups), ["identity_headers_present"] = Array(IdentityHeaders), ["proxy_detected"] = ProxyDetected,
        ["proxy_headers"] = Array(ProxyHeaders), ["custom_header"] = CustomHeader, ["host"] = Host, ["direct_access"] = DirectAccess };
    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static string Value(HttpRequest request, string key) => UnicodeCaseFold.Truncate(request.Headers[key].ToString().Trim().Split(',', 2)[0].Trim(), 256);
}
