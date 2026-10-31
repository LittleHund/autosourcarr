using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Repair;

public sealed record PublicSourceRequest(Uri Url, int MaximumBytes, TimeSpan Timeout, string? Accept = null);
public sealed record PublicSourceResponse(byte[] Body, Uri ResolvedUrl);
public interface IPublicSourceFetcher
{
    Task<PublicSourceResponse> FetchAsync(PublicSourceRequest request, CancellationToken cancellationToken = default);
}
public sealed record SourceIndexProject(string Owner, string Repository, string Readme, string Index);

/// <summary>Only declared same-publisher source repositories may nominate candidate code.</summary>
public sealed class SourceDiscovery(IPublicSourceFetcher fetcher)
{
    public const int CapabilityVersion = 2;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static SourceIndexProject? IndexProject(string value)
    {
        var file = GithubFile(value);
        if (file is null || file.Value.Files[^1] is not ("index.pb" or "index.json" or "index.min.json")) return null;
        var (owner, repository, branch, files) = file.Value;
        var root = "https://raw.githubusercontent.com/" + new Uri(repository).AbsolutePath.Trim('/') + "/" + branch + "/";
        // Explicit JSON paths remain explicit; protobuf publishers expose the
        // reviewed sibling JSON. There is no stale-min retry after a bad response.
        if (files[^1] == "index.pb") files[^1] = "index.json";
        return new(owner, repository, root + "README.md", root + string.Join('/', files));
    }
    public async Task<JsonObject?> DeclaredSourceAsync(string indexUrl, CancellationToken cancellationToken = default)
    {
        if (SourceOrigin.KnownIndex(indexUrl)) return new() { ["repository"] = SourceOrigin.KeiyoushiSource, ["ecosystem"] = "keiyoushi", ["declaration"] = "https://github.com/keiyoushi/extensions" };
        var project = IndexProject(indexUrl); if (project is null) return null;
        var response = await FetchAsync(project.Readme, 128 * 1024, cancellationToken).ConfigureAwait(false);
        if (!SamePublisher(project, response.ResolvedUrl)) return null;
        var text = StrictUtf8.GetString(response.Body); var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in Regex.Split(text, @"(?m)^#{1,6}\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var newline = section.IndexOf('\n'); var title = (newline < 0 ? section : section[..newline]).Trim();
            if (!Regex.IsMatch(title, @"\A(?:source(?:\s+code)?|source\s+repository)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) continue;
            var content = newline < 0 ? "" : section[(newline + 1)..];
            foreach (Match match in Regex.Matches(content, @"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                var plan = SourceOrigin.RepositoryPlan(match.Value);
                if (plan.Path.Split('/')[0].Equals(project.Owner, StringComparison.OrdinalIgnoreCase)) candidates.Add(plan.Repository);
            }
        }
        return candidates.Count == 1 ? new JsonObject { ["repository"] = candidates.Single(), ["ecosystem"] = "tachiyomi", ["declaration"] = project.Readme } : null;
    }
    public async Task<JsonObject> DiscoverAsync(JsonObject extension, IEnumerable<string> indexUrls, CancellationToken cancellationToken = default)
    {
        var package = RawCodeRules.Package(RawCodeRules.Text(extension, "pkgName")); var version = RawCodeRules.Version(RawCodeRules.Text(extension, "versionName"));
        var declared = SourceOrigin.Optional(extension, "storeIndexUrl"); var found = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase); var errors = new JsonArray();
        var urls = new[] { declared }.Concat(indexUrls).Where(url => !string.IsNullOrEmpty(url)).Distinct(StringComparer.Ordinal).Take(5).ToArray();
        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var project = IndexProject(url); if (project is null) continue;
                if (url != declared)
                {
                    var response = await FetchAsync(project.Index, 3 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                    if (!SamePublisher(project, response.ResolvedUrl) || !AdvertisesPackage(response.Body, package)) continue;
                }
                var source = await DeclaredSourceAsync(url, cancellationToken).ConfigureAwait(false);
                if (source is not null) { source["index_url"] = url; found[RawCodeRules.Text(source, "repository")] = source; }
            }
            catch (Exception error) when (error is InvalidDataException or IOException or JsonException or HttpRequestException or DecoderFallbackException or TimeoutException or ArgumentException)
            { errors.Add(error.GetType().Name); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (found.Count != 1) return new JsonObject { ["state"] = "needs-input", ["reason"] = found.Count > 1 ? "Multiple repositories advertise this package. Choose its original code repository." :
            "No unambiguous code declaration was found for this installed package. Select an upstream project below, provide its ZIP, or choose Unavailable.", ["checked"] = urls.Length, ["errors"] = errors };
        var selected = found.Values.Single();
        return new JsonObject { ["package"] = package, ["version"] = version, ["module"] = SourceOrigin.ModuleHint(package), ["state"] = "known",
            ["repository"] = selected["repository"]!.DeepClone(), ["ecosystem"] = selected["ecosystem"]!.DeepClone(), ["revision"] = "", ["index_url"] = selected["index_url"]!.DeepClone(), ["basis"] = "repository-candidate" };
    }
    private async Task<PublicSourceResponse> FetchAsync(string url, int maximum, CancellationToken cancellationToken)
    {
        var response = await fetcher.FetchAsync(new(new Uri(url), maximum, TimeSpan.FromSeconds(8)), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.Body.Length == 0 || response.Body.Length > maximum) throw new InvalidDataException("Source declaration exceeds its bounded response limit");
        return response;
    }
    private static bool AdvertisesPackage(byte[] body, string package)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        var rows = document.RootElement; var field = "pkg";
        if (rows.ValueKind == JsonValueKind.Object)
        {
            // The current Index oneof has an inline list or an external URL.
            // External lists are unsupported and never create another request.
            if (rows.TryGetProperty("extensionListUrl", out _) || !rows.TryGetProperty("extensionList", out var list)
                || list.ValueKind != JsonValueKind.Object || !list.TryGetProperty("extensions", out rows)) return false;
            field = "packageName";
        }
        if (rows.ValueKind != JsonValueKind.Array) return false;
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(field, out var name)
                && name.ValueKind == JsonValueKind.String && name.GetString() == package) return true;
        return false;
    }
    private static bool SamePublisher(SourceIndexProject project, Uri resolved)
    {
        var file = GithubFile(resolved.OriginalString);
        return file is not null && file.Value.Repository.Equals(project.Repository, StringComparison.OrdinalIgnoreCase);
    }
    private static (string Owner, string Repository, string Branch, string[] Files)? GithubFile(string value)
    {
        RawCodeRules.PublicReference(value);
        var uri = new Uri(value); var parts = RawCodeRules.OriginalUrlPath(value).Trim('/').Split('/'); int branchAt;
        if (uri.IdnHost == "github.com" && parts.Length >= 5 && parts[2] == "raw")
        {
            branchAt = parts[3] == "refs" && parts[4] == "heads" ? 5 : 3;
        }
        else if (uri.IdnHost == "raw.githubusercontent.com" && parts.Length >= 4)
        {
            // GitHub's Raw link preserves the ref selector on this final host;
            // refs/heads identifies the following branch, not a branch named refs.
            branchAt = parts.Length >= 6 && parts[2] == "refs" && parts[3] == "heads" ? 4 : 2;
        }
        else return null;
        if (parts.Length <= branchAt + 1) return null;
        var plan = SourceOrigin.RepositoryPlan("https://github.com/" + parts[0] + "/" + parts[1]);
        if (parts.Skip(branchAt).Any(part => part is "." or ".." || !RawCodeRules.Match(part, "[A-Za-z0-9_.-]+", 512))) return null;
        return (plan.Path.Split('/')[0], plan.Repository, parts[branchAt], parts[(branchAt + 1)..]);
    }
}
