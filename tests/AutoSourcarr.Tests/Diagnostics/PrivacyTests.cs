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
