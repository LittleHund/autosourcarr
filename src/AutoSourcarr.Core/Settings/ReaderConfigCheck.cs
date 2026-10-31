using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Settings;

public sealed record ReaderConfigIssue(string Key, int Code, string Message);

public sealed record ReaderConfigResult(bool Ready, int ExitCode, IReadOnlyList<ReaderConfigIssue> Errors, IReadOnlyList<ReaderConfigIssue> Warnings);

/// <summary>
/// Reader address and key checks shown by the setup wizard (port of extension/reader_build_config.py).
/// Same codes and messages, so the wizard and the reader build agree. Values are read, never returned.
/// </summary>
public static partial class ReaderConfigCheck
{
    public const string Token = "AUTOSOURCARR_READER_API_TOKEN";
    public const string Internal = "AUTOSOURCARR_INTERNAL_URL";
    public const string Public = "AUTOSOURCARR_PUBLIC_URL";
    public static IReadOnlyList<string> Keys { get; } = [Internal, Public, Token];

    public static ReaderConfigResult Evaluate(IReadOnlyDictionary<string, string?> values, bool allowInsecure = false, bool allowDocker = false)
    {
        var errors = new List<ReaderConfigIssue>();
        var warnings = new List<ReaderConfigIssue>();
        var token = values.GetValueOrDefault(Token) ?? "";
        if (token.Length == 0)
            errors.Add(new(Token, 12, "Set a Reader API key before building reader extensions."));
        else if (!TokenPattern().IsMatch(token))
            errors.Add(new(Token, 13, "Reader API key must contain 32–256 letters, digits, dots, underscores, tildes or hyphens."));

        var internalUrl = values.GetValueOrDefault(Internal) is { Length: > 0 } configured ? configured : "http://autosourcarr:8282";
        var publicUrl = values.GetValueOrDefault(Public) is { Length: > 0 } outside ? outside : internalUrl;
        var parsed = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var (key, value) in new[] { (Internal, internalUrl), (Public, publicUrl) })
        {
            if (Parse(value) is { } url) parsed[key] = url;
            else errors.Add(new(key, 14, "Use a literal HTTP(S) reader base address with a valid host/port and no credentials, interpolation, query, fragment, quotes or whitespace."));
        }
        if (parsed.TryGetValue(Public, out var reader))
        {
            if (reader.Scheme != "https" && !allowInsecure)
                errors.Add(new(Public, 14, "Android reader address must use HTTPS to protect its Reader API key."));
            else if (DockerOnly(reader.Host) && !allowDocker)
                errors.Add(new(Public, 11, "Android reader address uses a Docker-only service name. Set the public HTTPS address of this instance."));
        }
        if (parsed.TryGetValue(Internal, out var inside) && DockerOnly(inside.Host) && !inside.IsDefaultPort && inside.Port != 8282)
            warnings.Add(new(Internal, 0, "The Suwayomi reader address uses a Docker service with a port other than 8282. Use the container listener port unless you deliberately changed it, then rebuild and reinstall the JAR."));
        return new(errors.Count == 0, errors.Count == 0 ? 0 : errors[0].Code, errors, warnings);
    }

    private static bool DockerOnly(string host) => host.ToLowerInvariant() is "autosourcarr" or "private-autosourcarr";

    private static Uri? Parse(string value)
    {
        if (value.Length > 4096 || !(value.StartsWith("http://", StringComparison.Ordinal) || value.StartsWith("https://", StringComparison.Ordinal))
            || value.Any(c => char.IsWhiteSpace(c) || c < ' ' || "\\\"$`@?#".Contains(c, StringComparison.Ordinal)))
            return null;
        return Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Host.Length > 0 && url.UserInfo.Length == 0
            && url.Query.Length == 0 && url.Fragment.Length == 0 && url.Port is >= 1 and <= 65535 ? url : null;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._~-]{32,256}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
