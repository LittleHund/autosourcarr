using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Repair;

public sealed record SourceRepositoryPlan(string Repository, string Host, string Path, string ResolveUrl);

/// <summary>Resolve original ecosystem declarations, never a similarly named replacement.</summary>
public static class SourceOrigin
{
    public const string KeiyoushiSource = "https://github.com/keiyoushi/extensions-source", KeiyoushiIndex = "https://github.com/keiyoushi/extensions/raw/repo/index.pb";
    public static SourceRepositoryPlan RepositoryPlan(string value)
    {
        RawCodeRules.PublicReference(value); var uri = new Uri(value); var host = uri.IdnHost.ToLowerInvariant();
        if (host is not ("github.com" or "gitlab.com")) throw new InvalidDataException("Provide the original project ZIP for this repository host; no unrelated repository was guessed");
        var parts = RawCodeRules.OriginalUrlPath(value).Trim('/').Split('/');
        if (parts.Any(part => part is "." or ".." || !RawCodeRules.Match(part, "[A-Za-z0-9_.-]+", 512))) throw new InvalidDataException("Enter the original source repository root, not a file or ambiguous path");
        if (parts[^1].EndsWith(".git", StringComparison.Ordinal)) parts[^1] = parts[^1][..^4];
        if (parts[^1].Length == 0 || parts[^1] is "." or ".." || host == "github.com" && parts.Length != 2 || parts.Length > 6) throw new InvalidDataException("Enter the original source repository root");
        var path = string.Join('/', parts); var repo = "https://" + host + "/" + path;
        return new(repo, host, path, host == "github.com" ? "https://api.github.com/repos/" + path + "/commits/HEAD" : "https://gitlab.com/api/v4/projects/" + Uri.EscapeDataString(path) + "/repository/commits/HEAD");
    }
    public static string RepositoryArchive(SourceRepositoryPlan plan, string revision)
    {
        RawCodeRules.Commit(revision); var checkedPlan = RepositoryPlan(plan.Repository);
        return checkedPlan.Host == "github.com" ? "https://codeload.github.com/" + checkedPlan.Path + "/zip/" + revision : checkedPlan.Repository + "/-/archive/" + revision + "/" + checkedPlan.Path.Split('/')[^1] + "-" + revision + ".zip";
    }
    public static string ModuleHint(string package)
    {
        RawCodeRules.Package(package); const string prefix = "eu.kanade.tachiyomi.extension.";
        var suffix = package.StartsWith(prefix, StringComparison.Ordinal) ? package[prefix.Length..] : "";
        return RawCodeRules.Match(suffix, @"[a-z0-9]+\.[a-z0-9_]+", 200) ? "src/" + suffix.Replace('.', '/') : ".";
    }
    public static bool KnownIndex(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(c => c < 33) || value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Port != 443) return false;
        var path = RawCodeRules.OriginalUrlPath(value).ToLowerInvariant().Trim('/').Split('/');
        if (path[^1] is not ("index.pb" or "index.json" or "index.min.json")) return false;
        if (uri.IdnHost == "raw.githubusercontent.com") return
            path.Length == 4 && path[0] == "keiyoushi" && path[1] == "extensions" && path[2] == "repo" ||
            path.Length == 6 && path[0] == "keiyoushi" && path[1] == "extensions" && path[2] == "refs" && path[3] == "heads" && path[4] == "repo";
        return uri.IdnHost == "github.com" && path.Length >= 2 && path[0] == "keiyoushi" && path[1] == "extensions" &&
            (path.Length == 5 && path[2] == "raw" && path[3] == "repo" || path.Length == 7 && path[2] == "raw" && path[3] == "refs" && path[4] == "heads" && path[5] == "repo");
    }
    public static JsonObject Resolve(JsonObject extension, bool? manual = null, JsonObject? supplied = null)
    {
        var package = RawCodeRules.Package(RawCodeRules.Text(extension, "pkgName")); var version = RawCodeRules.Version(RawCodeRules.Text(extension, "versionName"));
        var result = new JsonObject { ["package"] = package, ["version"] = version, ["module"] = ModuleHint(package), ["ecosystem"] = "tachiyomi" };
        if (supplied is not null)
        {
            if (supplied.Any(pair => pair.Key is not ("repository" or "module" or "revision" or "ecosystem"))) throw new InvalidDataException("Unsupported source-origin fields");
            var plan = RepositoryPlan(RawCodeRules.Text(supplied, "repository")); var revision = Optional(supplied, "revision");
            if (revision.Length != 0) RawCodeRules.Commit(revision);
            var ecosystem = supplied.ContainsKey("ecosystem") ? RawCodeRules.Text(supplied, "ecosystem") : "tachiyomi";
            if (ecosystem is not ("tachiyomi" or "keiyoushi" or "mihon" or "suwayomi")) throw new InvalidDataException("Unsupported source ecosystem");
            result["state"] = "known"; result["repository"] = plan.Repository; result["module"] = supplied.ContainsKey("module") ? RawCodeRules.Module(RawCodeRules.Text(supplied, "module")) : ModuleHint(package);
            result["revision"] = revision; result["ecosystem"] = ecosystem; result["basis"] = "user-provided"; return result;
        }
        if (manual == true || manual is null && Optional(extension, "orphan_reason") == "manual-upload")
        {
            result["state"] = "needs-input"; result["reason"] = "This source was installed locally. Provide its original raw project or choose Unavailable to reconstruct the actual installed code."; return result;
        }
        if (KnownIndex(Optional(extension, "storeIndexUrl")))
        {
            result["state"] = "known"; result["repository"] = KeiyoushiSource; result["revision"] = ""; result["ecosystem"] = "keiyoushi";
            result["index_url"] = KeiyoushiIndex; result["basis"] = "known-repository"; return result;
        }
        result["state"] = "needs-input"; result["reason"] = "The original code repository is not declared by this source. Provide its project ZIP/repository or choose Unavailable; no similar source was substituted."; return result;
    }
    public static string Optional(JsonObject value, string key) => value[key] is null ? "" : RawCodeRules.Text(value, key);
}
