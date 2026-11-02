using System.Text.Json.Nodes;
using AutoSourcarr.Core.Privacy;

namespace AutoSourcarr.Tests;

public static class PrivacyTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("privacy: known secrets are masked in plain and encoded logs", () =>
        {
            var privacy = new SupportPrivacy();
            const string secret = "private-保持/a'b$&";
            privacy.Register(new Dictionary<string, string?> { ["apiKey"] = secret });
            var escaped = Uri.EscapeDataString(secret);
            var text = privacy.Text("failure " + secret + " encoded=" + escaped);
            Check.True(!text.Contains(secret, StringComparison.Ordinal));
            Check.True(!text.Contains(escaped, StringComparison.Ordinal));
            Check.True(text.Contains("[redacted]", StringComparison.Ordinal));
        });
        suite.Add("privacy: credentials, user paths, addresses and query values are hidden", () =>
        {
            var privacy = new SupportPrivacy();
            var text = privacy.Text("""
                Authorization: Bearer secret-access-value
                uri https://private.example/api/graphql?token=secret-query&search=private-title
                userEmail=private.person@example.invalid
                address 192.168.10.77 and /mnt/private-user/project/worker.log
                server F:\private-user\project\worker.log
                --storepass signing-password
                """);
            foreach (var value in new[] { "secret-access-value", "private.example", "secret-query", "private-title", "private.person", "192.168.10.77", "private-user", "signing-password" })
                Check.True(!text.Contains(value, StringComparison.Ordinal), "Private sentinel was retained");
            Check.True(text.Contains("/api/graphql", StringComparison.Ordinal));
            Check.True(text.Contains("worker.log", StringComparison.Ordinal));
        });
        suite.Add("privacy: PERF-9 instance folders are named, bare cookies and signing keys are hidden, other paths keep their words", () =>
        {
            var privacy = new SupportPrivacy();
            var root = OperatingSystem.IsWindows() ? @"D:\nas-share\autosourcarr" : "/srv/nas-share/autosourcarr";
            privacy.RegisterPath("data", root); privacy.RegisterPath("ignored", "relative/folder"); privacy.RegisterPath("ignored", "/");
            var file = Path.Combine(root, "catalog.sqlite");
            var text = privacy.Text("open " + file + " failed; folder " + root + "; other " + root + "-old/x; cf_clearance=cookie-sentinel __cf_bm=bm-sentinel PHPSESSID=php-sentinel");
            Check.True(!text.Contains("nas-share", StringComparison.Ordinal), "the registered folder is hidden: " + text);
            Check.True(text.Contains("[data]", StringComparison.Ordinal) && text.Contains("catalog.sqlite", StringComparison.Ordinal), text);
            foreach (var value in new[] { "cookie-sentinel", "bm-sentinel", "php-sentinel" }) Check.True(!text.Contains(value, StringComparison.Ordinal), value + " retained");
            var scrubbed = privacy.Scrub(new JsonObject { ["signing_key"] = "key-sentinel", ["keystore"] = "store-sentinel", ["key"] = "COVER_CACHE_MAX_MB" })!.ToJsonString();
            Check.True(!scrubbed.Contains("key-sentinel", StringComparison.Ordinal) && !scrubbed.Contains("store-sentinel", StringComparison.Ordinal), scrubbed);
            Check.True(scrubbed.Contains("COVER_CACHE_MAX_MB", StringComparison.Ordinal), "setting names stay readable");
            // A registered "/data" never changes "/database" or an API path.
            var docker = new SupportPrivacy(); docker.RegisterPath("data", OperatingSystem.IsWindows() ? @"C:\data" : "/data");
            var api = docker.Text("GET /api/v1/storage/database and /api/v1/metadata-providers");
            Check.True(api.Contains("/api/v1/storage/database", StringComparison.Ordinal) && api.Contains("/api/v1/metadata-providers", StringComparison.Ordinal), api);
        });
        suite.Add("privacy: field allowlists and registry overflow fail closed", () =>
        {
            var privacy = new SupportPrivacy();
            var scrubbed = privacy.Scrub(new JsonObject
            {
                ["apiKey"] = "hidden-key", ["preferred_title"] = "private-title", ["status"] = "blocked",
                ["nested"] = new JsonObject { ["workspaceId"] = "hidden-workspace", ["exit_code"] = 70 }
            })!;
            var text = scrubbed.ToJsonString();
            Check.True(!text.Contains("hidden-", StringComparison.Ordinal) && !text.Contains("private-title", StringComparison.Ordinal));
            Check.Equal("blocked", scrubbed["status"]!.GetValue<string>());
            Check.Equal(70, scrubbed["nested"]!["exit_code"]!.GetValue<int>());
            privacy.Register(new Dictionary<string, string?> { ["token"] = new('x', 16385) });
            Check.Equal("[diagnostic omitted: secret registry capacity]", privacy.Text("even an unrelated message"));
        });
    }
}
