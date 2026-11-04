namespace AutoSourcarr.Core.Cutover;

/// <summary>
/// Reads the <c>environment:</c> block of each service in a Compose file (both <c>KEY: value</c> and <c>- KEY=value</c>
/// forms). Deliberately small: no anchors, merges, interpolation or env_file. A value it cannot know (from the host, or
/// interpolated) is refused rather than guessed, so the comparison never runs on a value Compose would not use.
/// </summary>
public static class ComposeEnvironment
{
    public static Dictionary<string, Dictionary<string, string>> Read(string path)
    {
        var services = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        int? servicesIndent = null, serviceIndent = null, environmentIndent = null;
        string? service = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Replace("\t", "  ", StringComparison.Ordinal);
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var indent = line.Length - line.TrimStart().Length;
            if (servicesIndent is null) { if (trimmed == "services:") servicesIndent = indent; continue; }
            if (indent <= servicesIndent) { servicesIndent = trimmed == "services:" ? indent : null; service = null; serviceIndent = null; environmentIndent = null; continue; }
            if (serviceIndent is null || indent <= serviceIndent)
            {
                if (trimmed.EndsWith(':'))
                {
                    serviceIndent = indent; service = trimmed[..^1].Trim().Trim('"', '\'');
                    services[service] = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                environmentIndent = null;
                continue;
            }
            if (environmentIndent is null || indent <= environmentIndent)
            {
                environmentIndent = trimmed == "environment:" ? indent : null;
                continue;
            }
            if (service is null) continue;
            string key, value;
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var item = Unquote(trimmed[2..].Trim());
                var equals = item.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0)
                    // "- KEY" takes the value from the host shell: unknown here, so it cannot be compared.
                    throw new InvalidDataException("service " + service + ": " + item + " has no value (it comes from the host environment); write KEY=value");
                key = item[..equals].Trim(); value = item[(equals + 1)..];
            }
            else
            {
                var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0) continue;
                key = trimmed[..colon].Trim(); value = Unquote(StripComment(trimmed[(colon + 1)..].Trim()));
            }
            if (value.Length == 0 && !trimmed.StartsWith("- ", StringComparison.Ordinal))
                throw new InvalidDataException("service " + service + ": " + key + " has no value; write KEY: value");
            if (value.Contains("${", StringComparison.Ordinal) || value.StartsWith('$'))
                throw new InvalidDataException("service " + service + ": " + key + " uses variable interpolation (" + value + "), which this tool does not resolve; write the value itself");
            services[service][key] = value;
        }
        return services;
    }

    private static string StripComment(string value) => value.StartsWith('"') || value.StartsWith('\'') ? value
        : (value.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0 ? value[..hash].TrimEnd() : value);

    private static string Unquote(string value) => value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'') ? value[1..^1] : value;
}
