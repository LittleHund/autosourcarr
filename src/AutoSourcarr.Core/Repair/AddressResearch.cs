using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Core.Repair;

/// <summary>Original-maintainer and public-search observations through the isolated website client.
/// Search results never establish replacement ownership, source validation or a working repair.</summary>
public sealed class AddressResearch(IWebsiteObservationClient probe)
{
    private static readonly Regex Revision = new(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant);
    public async Task<JsonObject> ResearchAsync(JsonObject inputs, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(WebsiteEvidence.Json(inputs)) > 128 * 1024) throw new InvalidDataException("Research input exceeds its limit");
        var info = inputs["raw_meta"] as JsonObject ?? throw new InvalidDataException("Missing source research provenance");
        var original = info["origin"] as JsonObject ?? new JsonObject(); var repository = WebsiteEvidence.Text(original, "repository");
        var evidence = new JsonArray(); var targets = new JsonArray(); var leads = new JsonArray(); var head = ""; SourceRepositoryPlan? plan = null;
        try
        {
            if (repository.Length != 0)
            {
                plan = SourceOrigin.RepositoryPlan(repository);
                var response = await probe.ResponseAsync(plan.ResolveUrl, plan.Host == "github.com" ? "application/vnd.github.sha" : null, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == 200 && response.Readable)
                {
                    head = Encoding.ASCII.GetString(response.Body.Span).Trim();
                    if (!Revision.IsMatch(head))
                    {
                        var json = JsonNode.Parse(response.Body.Span, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject;
                        head = json is null ? "" : WebsiteEvidence.Text(json, plan.Host == "github.com" ? "sha" : "id");
                    }
                }
                if (!Revision.IsMatch(head)) head = "";
            }
        }
        catch (Exception error) when (ObservationFailure(error)) { head = ""; }
        if (head.Length != 0 && plan is not null)
            foreach (var row in WebsiteEvidence.Rows(inputs, "files").Take(6))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Invalid paths fail closed before any source-file request; no workspace path is opened.
                var name = RawCodeRules.RelativeName(WebsiteEvidence.Text(row, "path"));
                if (!new[] { ".kt", ".java", ".gradle.kts", ".gradle" }.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal))) continue;
                var url = plan.Host == "github.com" ? "https://raw.githubusercontent.com/" + plan.Path + "/" + head + "/" + name : plan.Repository + "/-/raw/" + head + "/" + name;
                try
                {
                    var response = await probe.ResponseAsync(url, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode != 200 || !response.Readable) continue;
                    var candidates = AddressResearchEvidence.BaseAddresses(Encoding.UTF8.GetString(response.Body.Span));
                    if (candidates.Count != 1) continue;
                    var current = candidates[0];
                    foreach (var source in WebsiteEvidence.Rows(inputs, "sources").Take(200))
                    {
                        string previous;
                        try { previous = WebsiteAddress.PublicUrl(WebsiteEvidence.Text(source, "baseUrl")); }
                        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { continue; }
                        if (row["addresses"] is not JsonArray addresses || !addresses.Any(node => node is JsonValue value && value.TryGetValue<string>(out var text) && text == previous) || WebsiteAddress.Origin(previous) == WebsiteAddress.Origin(current)) continue;
                        var observed = await probe.OneAsync(current, cancellationToken: cancellationToken).ConfigureAwait(false); observed.Remove("_api_targets");
                        var observedFinal = WebsiteEvidence.Text(observed, "final_url"); if (observedFinal.Length == 0) observedFinal = current;
                        if (WebsiteEvidence.Text(observed, "state") != "working" || WebsiteAddress.Origin(observedFinal) != WebsiteAddress.Origin(current)) continue;
                        var redirect = WebsiteEvidence.Rows(inputs, "website_checks").FirstOrDefault(value => WebsiteEvidence.HasSource(value, WebsiteEvidence.SourceId(source)) &&
                            WebsiteEvidence.Text(value, "state") == "working" && WebsiteEvidence.Text(value, "requested_url").StartsWith("https://", StringComparison.Ordinal) &&
                            WebsiteEvidence.Text(value, "final_url") == current && (value["redirects"] is not JsonArray redirects || redirects.All(node => node is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.StartsWith("https://", StringComparison.Ordinal))));
                        var recognized = (WebsiteEvidence.Text(info, "kind") is "upstream" or "reconstructed" or "repaired") &&
                            WebsiteEvidence.Text(original, "repository") == SourceOrigin.KeiyoushiSource && (WebsiteEvidence.Text(original, "basis") is "known-repository" or "binary-reconstruction");
                        var trust = redirect is not null && recognized ? "corroborated" : "review-required";
                        var target = new JsonObject { ["source_id"] = WebsiteEvidence.SourceId(source), ["previous_url"] = previous, ["new_url"] = current,
                            ["basis"] = "original-maintainer-code", ["source_revision"] = head, ["evidence_url"] = url, ["trust"] = trust };
                        if (!targets.OfType<JsonObject>().Any(value => WebsiteEvidence.Text(value, "source_id") == WebsiteEvidence.SourceId(source) && WebsiteEvidence.Text(value, "new_url") == current)) targets.Add(target);
                        evidence.Add(new JsonObject { ["state"] = "observed", ["url"] = url, ["revision"] = head, ["new_url"] = current, ["trust"] = trust });
                    }
                }
                catch (Exception error) when (ObservationFailure(error)) { }
            }
        foreach (var row in WebsiteEvidence.Rows(inputs, "website_checks").Take(2))
        {
            if (WebsiteEvidence.Text(row, "state") is not ("not_found" or "inconclusive")) continue;
            try
            {
                var previous = WebsiteAddress.PublicUrl(WebsiteEvidence.Text(row, "requested_url")); var host = new Uri(previous).IdnHost.Trim('[', ']');
                var package = WebsiteEvidence.Text(inputs, "package"); var suffix = package.Split('.')[^1];
                var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(host + " " + suffix + " official website moved");
                var response = await probe.ResponseAsync(url, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == 200 && response.Readable)
                    foreach (var value in WebsiteMarkup.SearchLinks(Encoding.UTF8.GetString(response.Body.Span)).Take(4))
                        leads.Add(new JsonObject { ["url"] = value, ["basis"] = "public-search-lead", ["trust"] = "unverified" });
                evidence.Add(new JsonObject { ["state"] = response.StatusCode == 200 ? "searched" : "unavailable", ["provider"] = "DuckDuckGo", ["for_origin"] = WebsiteAddress.Origin(previous), ["http_status"] = response.StatusCode });
            }
            catch (Exception error) when (ObservationFailure(error)) { }
        }
        return new() { ["targets"] = new JsonArray(targets.Take(16).Select(node => node?.DeepClone()).ToArray()),
            ["evidence"] = new JsonArray(evidence.Take(16).Select(node => node?.DeepClone()).ToArray()), ["leads"] = new JsonArray(leads.Take(8).Select(node => node?.DeepClone()).ToArray()), ["note"] = AddressResearchEvidence.Note };
    }
    private static bool ObservationFailure(Exception error) => error is InvalidDataException or IOException or ArgumentException or JsonException or TimeoutException or HttpRequestException;
}
