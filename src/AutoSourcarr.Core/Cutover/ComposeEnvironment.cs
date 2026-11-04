namespace AutoSourcarr.Core.Cutover;

/// <summary>One Compose service: its plain environment values, the values it cannot know, and its env_file entries.</summary>
public sealed record ComposeService(string Name, Dictionary<string, string> Environment, List<string> Unknown, List<string> EnvFiles);

/// <summary>
/// Reads the <c>environment:</c> block of each service in a Compose file (both <c>KEY: value</c> and <c>- KEY=value</c>
/// forms, quoted values, <c>$$</c> as a literal <c>$</c>). Deliberately small: anchors, merge keys, flow or block styles,
/// values from the host and interpolation are not resolved. They are recorded per service as unknown, so the tool refuses
/// the service it would use instead of comparing a value Compose would not use, while other services do not matter.
/// </summary>
public static class ComposeEnvironment
{
    public static Dictionary<string, ComposeService> Read(string path)
    {
        var services = new Dictionary<string, ComposeService>(StringComparer.Ordinal);
        int? servicesIndent = null, serviceIndent = null, environmentIndent = null, envFileIndent = null;
        ComposeService? service = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Replace("\t", "  ", StringComparison.Ordinal);
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var indent = line.Length - line.TrimStart().Length;
            if (servicesIndent is null) { if (trimmed == "services:") servicesIndent = indent; continue; }
            if (indent <= servicesIndent) { servicesIndent = trimmed == "services:" ? indent : null; service = null; serviceIndent = null; environmentIndent = null; envFileIndent = null; continue; }
            if (serviceIndent is null || indent <= serviceIndent)
            {
                if (trimmed.EndsWith(':'))
                {
                    serviceIndent = indent;
                    var name = trimmed[..^1].Trim().Trim('"', '\'');
                    service = services[name] = new ComposeService(name, new Dictionary<string, string>(StringComparer.Ordinal), [], []);
                }
                environmentIndent = null; envFileIndent = null;
                continue;
            }
            if (service is null) continue;
            if (environmentIndent is null && envFileIndent is null || indent <= (environmentIndent ?? envFileIndent))
            {
                environmentIndent = null; envFileIndent = null;
                if (trimmed == "environment:") environmentIndent = indent;
                else if (trimmed.StartsWith("environment:", StringComparison.Ordinal))
                    service.Unknown.Add("environment: written inline (" + trimmed + "); write one KEY: value per line");
                else if (trimmed == "env_file:") envFileIndent = indent;
                else if (trimmed.StartsWith("env_file:", StringComparison.Ordinal)) service.EnvFiles.Add(Unquote(trimmed["env_file:".Length..].Trim()));
                else if (trimmed.StartsWith("<<:", StringComparison.Ordinal)) service.Unknown.Add("a merge key (" + trimmed + ") may bring environment values this tool does not resolve");
                continue;
            }
            if (envFileIndent is not null)
            {
                if (trimmed.StartsWith("- ", StringComparison.Ordinal)) service.EnvFiles.Add(Unquote(trimmed[2..].Trim()));
                continue;
            }
            string key, value;
            bool quoted;
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var item = trimmed[2..].Trim();
                quoted = IsQuoted(item); item = Unquote(item);
                var equals = item.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0) { service.Unknown.Add(item + " takes its value from the host environment"); continue; }
                key = item[..equals].Trim(); value = item[(equals + 1)..];
            }
            else
            {
                var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0) continue;
                key = trimmed[..colon].Trim(); var text = StripComment(trimmed[(colon + 1)..].Trim());
                quoted = IsQuoted(text); value = Unquote(text);
            }
            if (key == "<<") { service.Unknown.Add("a merge key may bring environment values this tool does not resolve"); continue; }
            if (!quoted && value.Length == 0) { service.Unknown.Add(key + " has no value (write KEY: \"\" for an empty one)"); continue; }
            if (!quoted && value.Length > 0 && value[0] is '*' or '&' or '|' or '>' or '[' or '{')
            { service.Unknown.Add(key + " uses YAML syntax this tool does not resolve (" + value + ")"); continue; }
            var literal = value.Replace("$$", "\0", StringComparison.Ordinal);
            if (literal.Contains('$', StringComparison.Ordinal)) { service.Unknown.Add(key + " uses variable interpolation (" + value + "), which this tool does not resolve"); continue; }
            service.Environment[key] = literal.Replace("\0", "$", StringComparison.Ordinal);
        }
        return services;
    }

    private static bool IsQuoted(string value) => value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'');

    private static string StripComment(string value) => value.StartsWith('"') || value.StartsWith('\'') ? value
        : (value.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0 ? value[..hash].TrimEnd() : value);

    private static string Unquote(string value) => IsQuoted(value) ? value[1..^1] : value;
}
