using System.Text.RegularExpressions;

namespace AutoSourcarr.Api.Http;

public static class RequestPolicy
{
    private static readonly HashSet<string> Polls = ["/api/admin/imports", "/api/admin/matrix", "/api/admin/source-health", "/api/admin/source-maintenance", "/api/admin/source-maintenance/control", "/api/v1/source-maintenance"];
    private static readonly HashSet<string> NonInteractive = ["/api/v1/live", "/api/v1/health", "/api/admin/tasks", "/api/admin/errors", "/api/v1/tasks", "/api/v1/errors", "/api/v1/jobs", "/api/v1/events", "/api/admin/duplicates", "/api/admin/duplicate-candidates", "/api/admin/entries/summaries", "/api/admin/matrix/chapter-stats"];
    private static readonly HashSet<string> NoMutationTask = ["/api/admin/entries/summaries", "/api/admin/duplicate-candidates", "/api/admin/matrix/chapter-stats", "/api/admin/duplicates/automatic",
        "/api/admin/imports/clear-history", "/api/admin/imports/reset-rejections", "/api/admin/imports/reset-manual-autoresolve", "/api/admin/ui-preferences", "/api/admin/maintenance/refresh-metadata",
        "/api/admin/maintenance/discover-additional", "/api/admin/subbed/search-all", "/api/admin/subbed/repair-unmatched", "/api/admin/maintenance/full-reset", "/api/admin/maintenance/consolidate", "/api/admin/manual-actions"];
    private static readonly HashSet<string> ResetControls = ["/api/admin/maintenance/reset-database", "/api/admin/maintenance/full-reset", "/api/admin/maintenance/reset-all-manual-actions", "/api/admin/storage/database/move", "/api/v1/storage/database/move"];
    private static readonly HashSet<string> Monitoring = ["/api/v1/live", "/api/v1/health", "/api/admin/tasks", "/api/admin/errors", "/api/v1/tasks", "/api/v1/errors", "/api/v1/jobs", "/api/v1/events"];

    public static bool Interactive(string method, string path) => !(method == "GET" && (Polls.Contains(path) || Match(path, @"^/api/admin/imports/[^/]+/items(?:/[0-9]+)?$"))
        || Starts(path, "/static/", "/api/v1/covers/", "/api/v1/source-covers/", "/api/v1/source-icon/", "/api/v1/source-extension-icon/") || NonInteractive.Contains(path));

    public static bool CatalogAdmission(string method, string path)
    {
        if (!path.StartsWith("/api/", StringComparison.Ordinal)) return false;
        var control = method == "POST" && (ResetControls.Contains(path) || Starts(path, "/api/admin/instance/", "/api/admin/source-orphans/", "/api/admin/source-maintenance/", "/api/admin/source-extensions/", "/api/admin/source-health/",
            "/api/v1/source-orphans/", "/api/v1/source-maintenance/"))
            || method == "PUT" && (path == "/api/admin/source-health/daily" || Match(path, @"^/api/admin/sources/[^/]+/preferences$"));
        control |= method is "POST" or "PUT" or "DELETE" && (Starts(path, "/api/admin/library/", "/api/admin/profiles/", "/api/admin/downloads", "/api/admin/review-workbench/",
            "/api/admin/metadata-fields/", "/api/admin/serving-upgrades/", "/api/admin/policy-simulator/", "/api/admin/engine/compatibility", "/api/v1/engine/")
            || path.StartsWith("/api/admin/candidates/", StringComparison.Ordinal) && path.EndsWith("/quality", StringComparison.Ordinal)
            || (path.StartsWith("/api/admin/tasks/", StringComparison.Ordinal) || path.StartsWith("/api/v1/tasks/", StringComparison.Ordinal)) && path.EndsWith("/retry-safe", StringComparison.Ordinal));
        return !control && !(method is "GET" or "HEAD" && Monitoring.Contains(path));
    }

    public static string? MutationTaskName(string method, string path)
    {
        method = method.ToUpperInvariant();
        if (method is not ("POST" or "PUT" or "DELETE") || !path.StartsWith("/api/admin/", StringComparison.Ordinal)) return null;
        if (Starts(path, "/api/admin/tasks/", "/api/admin/source-browser", "/api/admin/source-maintenance", "/api/admin/source-orphans", "/api/admin/source-health", "/api/admin/setup", "/api/admin/attention", "/api/admin/errors")
            || NoMutationTask.Contains(path) || path.Contains("/check-chapters", StringComparison.Ordinal) || path.EndsWith("/redo-automation", StringComparison.Ordinal)) return null;
        var direct = path switch
        {
            "/api/admin/maintenance/reset-covers" => "Reset covers",
            "/api/admin/maintenance/reset-database" => "Reset database",
            "/api/admin/maintenance/reset-manual-groups" => "Reset manual groups",
            "/api/admin/maintenance/reset-manual-matches" => "Reset manual matches",
            "/api/admin/maintenance/refresh-feeds" => "Refresh browse feeds",
            "/api/admin/duplicates/review" => "Review duplicate",
            "/api/admin/sources/sync" => "Sync sources",
            "/api/admin/source-extensions/upload" => "Install source APK",
            _ => null
        };
        if (direct is not null) return direct;
        if (path.EndsWith("/refresh-covers", StringComparison.Ordinal)) return "Refresh covers";
        if (path.EndsWith("/unmatch-duplicates", StringComparison.Ordinal)) return "Unmatch duplicates";
        if (path.EndsWith("/unbind-auto-matches", StringComparison.Ordinal)) return "Unbind automatic matches";
        if (path.StartsWith("/api/admin/manual-groups/", StringComparison.Ordinal))
        { if (method == "DELETE") return "Remove manual group member"; if (method == "POST") return "Add manual group member"; }
        if (method == "PUT" && Match(path, @"^/api/admin/sources/[^/]+/preferences$")) return "Save source settings";
        if (method == "PUT" && path.StartsWith("/api/admin/sources/", StringComparison.Ordinal)) return "Update source";
        if (method == "PUT" && path.StartsWith("/api/admin/settings/env/", StringComparison.Ordinal)) return "Save settings";
        if (path.StartsWith("/api/admin/candidates/", StringComparison.Ordinal) && path.EndsWith("/quality", StringComparison.Ordinal)) return "Analyze chapter quality";
        if (path.StartsWith("/api/admin/imports/", StringComparison.Ordinal))
        {
            if (path.EndsWith("/retry", StringComparison.Ordinal) || path.EndsWith("/reject", StringComparison.Ordinal) || path.Contains("/match/", StringComparison.Ordinal)) return null;
            if (path.EndsWith("/skip", StringComparison.Ordinal)) return "Skip backup import";
        }
        if (path.StartsWith("/api/admin/entries/", StringComparison.Ordinal))
        {
            if (method == "DELETE" && path.Contains("/variants/", StringComparison.Ordinal)) return "Remove additional match";
            if (path.EndsWith("/promote", StringComparison.Ordinal)) return "Use as primary";
            if (path.EndsWith("/refresh-metadata", StringComparison.Ordinal)) return "Refresh metadata";
            if (path.EndsWith("/refresh", StringComparison.Ordinal)) return "Refresh metadata + chapters";
            if (path.EndsWith("/discover-additional", StringComparison.Ordinal)) return "Find missing + extra source records";
            if (path.EndsWith("/manual-match", StringComparison.Ordinal)) return "Update match";
            if (method == "DELETE" && path.Contains("/matches/", StringComparison.Ordinal)) return "Clear match";
            if (path.EndsWith("/preferred-title", StringComparison.Ordinal)) return "Change main title";
            if (path.EndsWith("/aliases", StringComparison.Ordinal)) return "Add alternate title";
            if (method == "DELETE" && path.Count(character => character == '/') == 4) return "Delete entry";
        }
        return $"Admin action: {method} {path["/api/admin/".Length..]}";
    }
    private static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant);
    private static bool Starts(string path, params string[] prefixes) => prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
}
