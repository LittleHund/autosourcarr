using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Repair;

public sealed class AddressBlocked(string message) : IOException(message);
public sealed class AddressNeedsUpdate(string message) : IOException(message);

/// <summary>Stage corresponding address evidence. Redirects never establish ownership or a validated repair.</summary>
public sealed class AddressPolicy(JsonObject job, Func<IEnumerable<JsonObject>, CancellationToken, Task<JsonArray>> probe)
{
    public async Task<JsonArray> CheckAsync(IEnumerable<JsonObject> sources, IEnumerable<JsonObject>? expected, bool official = false, CancellationToken cancellationToken = default)
    {
        var previous = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in expected ?? []) previous[WebsiteEvidence.SourceId(row)] = row;
        var pairs = new List<(JsonObject Source, string Old, string New)>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested(); var id = WebsiteEvidence.SourceId(source); var old = previous.TryGetValue(id, out var row) ? WebsiteEvidence.Text(row, "baseUrl") : "";
            string current;
            try { current = WebsiteAddress.PublicUrl(WebsiteEvidence.Text(source, "baseUrl")); }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { throw new AddressBlocked("Candidate has no supported public website address"); }
            if (old.Length == 0)
            { if (WebsiteEvidence.Flag(job, "recovery")) continue; throw new AddressBlocked("Existing website address is unavailable; no address migration was inferred"); }
            try { old = WebsiteAddress.PublicUrl(old); }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { throw new AddressBlocked("Existing website address cannot be verified safely"); }
            var redirected = Redirect(source, old);
            if (redirected is not null && WebsiteAddress.Origin(current) == WebsiteAddress.Origin(old))
                throw new AddressNeedsUpdate("Source still declares " + old + " but its checked HTTPS website redirects to " + WebsiteEvidence.Text(redirected, "new_url") + ". Update the corresponding base address and API paths before validation.");
            if (WebsiteAddress.Origin(old) != WebsiteAddress.Origin(current)) pairs.Add((source, old, current));
        }
        if (official && pairs.Count != 0)
        {
            if (!KnownPatterns.Hex40().IsMatch(WebsiteEvidence.Text(job, "source_revision"))) throw new AddressBlocked("Changed official addresses require the pinned source revision");
            var observations = await probe(pairs.Select(pair => pair.Source), cancellationToken).ConfigureAwait(false);
            job["website_checks"] = new JsonArray(WebsiteEvidence.Rows(job, "website_checks").Concat(observations.OfType<JsonObject>()).TakeLast(16).Select(value => value.DeepClone()).ToArray());
            foreach (var (source, old, current) in pairs)
            {
                var observation = observations.OfType<JsonObject>().FirstOrDefault(row => WebsiteEvidence.HasSource(row, WebsiteEvidence.SourceId(source)) && WebsiteEvidence.Text(row, "state") == "working");
                if (observation is null) throw new AddressBlocked("The maintained source declares a new website, but its public availability is inconclusive or requires verification");
                var final = WebsiteAddress.PublicUrl(WebsiteEvidence.Text(observation, "final_url")); Remember(WebsiteEvidence.SourceId(source), old, final, "maintained-source-revision");
                if (WebsiteAddress.Origin(final) != WebsiteAddress.Origin(current)) throw new AddressNeedsUpdate("Maintained source address " + current + " redirects to " + final + ". Match its current website and API addresses before validation.");
            }
        }
        var changes = new JsonArray();
        foreach (var (source, old, current) in pairs)
        {
            var target = WebsiteEvidence.Rows(job, "address_targets").FirstOrDefault(row => WebsiteEvidence.Text(row, "source_id") == WebsiteEvidence.SourceId(source) && WebsiteAddress.Origin(WebsiteEvidence.Text(row, "new_url")) == WebsiteAddress.Origin(current));
            if (target is null) throw new AddressBlocked("Changed website does not match the checked corresponding redirect or pinned maintained source; no unrelated replacement was accepted");
            var change = (JsonObject)target.DeepClone(); change["previous_url"] = old; change["new_url"] = current; changes.Add(change);
        }
        if (changes.Count != 0 && WebsiteEvidence.Flag(job, "raw_first")) job["automatic_address_trust"] = changes.OfType<JsonObject>().All(row => WebsiteEvidence.Text(row, "trust") == "corroborated" && WebsiteEvidence.Text(row, "basis") == "original-maintainer-code");
        return changes;
    }
    private JsonObject Remember(string id, string previous, string target, string basis)
    {
        target = WebsiteAddress.PublicUrl(target); if (!target.StartsWith("https://", StringComparison.Ordinal)) throw new AddressBlocked("A changed website address must use HTTPS");
        var rows = job["address_targets"] as JsonArray;
        if (rows is null) { if (job["address_targets"] is not null) throw new AddressBlocked("Malformed address target evidence"); rows = []; job["address_targets"] = rows; }
        var item = new JsonObject { ["source_id"] = id, ["previous_url"] = previous, ["new_url"] = target, ["basis"] = basis };
        if (basis == "maintained-source-revision") item["source_revision"] = job["source_revision"]?.DeepClone();
        if (rows.Count < 200 && !rows.Any(row => JsonNode.DeepEquals(row, item))) rows.Add(item.DeepClone());
        return item;
    }
    private JsonObject? Redirect(JsonObject source, string previous)
    {
        foreach (var row in WebsiteEvidence.Rows(job, "website_checks"))
        {
            if (WebsiteEvidence.Text(row, "state") != "working" || !WebsiteEvidence.HasSource(row, WebsiteEvidence.SourceId(source))) continue;
            try
            {
                var chain = new[] { WebsiteAddress.PublicUrl(WebsiteEvidence.Text(row, "requested_url")) }.Concat(row["redirects"] is JsonArray redirects ? redirects.Select(node => WebsiteAddress.PublicUrl(node?.GetValue<string>() ?? "")) : []).ToArray();
                var final = WebsiteAddress.PublicUrl(WebsiteEvidence.Text(row, "final_url")); var secure = chain.All(url => url.StartsWith("https://", StringComparison.Ordinal));
                var upgrade = final.StartsWith("https://", StringComparison.Ordinal) && chain.All(url => new Uri(url).IdnHost == new Uri(previous).IdnHost);
                if (WebsiteAddress.Origin(chain[0]) == WebsiteAddress.Origin(previous) && (secure || upgrade) && WebsiteAddress.Origin(final) != WebsiteAddress.Origin(previous))
                    return Remember(WebsiteEvidence.SourceId(source), previous, final, secure ? "observed-https-redirect" : "same-host-https-upgrade");
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException) { }
        }
        return null;
    }
    public static string PromptEvidence(JsonObject job, int limit = 16000)
    {
        if (limit < 128 || limit > 128 * 1024) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new JsonObject { ["website_checks"] = new JsonArray(), ["address_targets"] = new JsonArray(), ["omitted"] = 0, ["address_research"] = job["address_research"]?.DeepClone() ?? new JsonObject() };
        if (Encoding.UTF8.GetByteCount(WebsiteEvidence.Json(result, spaces: true)) > limit) throw new InvalidDataException("Address research exceeds the prompt evidence limit");
        var omitted = 0;
        foreach (var key in new[] { "website_checks", "address_targets" })
            foreach (var row in WebsiteEvidence.Rows(job, key))
            {
                var rows = result[key]!.AsArray(); rows.Add(row.DeepClone());
                if (Encoding.UTF8.GetByteCount(WebsiteEvidence.Json(result, spaces: true)) > limit) { rows.RemoveAt(rows.Count - 1); result["omitted"] = ++omitted; }
            }
        var text = WebsiteEvidence.Json(result);
        if (Encoding.UTF8.GetByteCount(text) > limit) throw new InvalidDataException("Address evidence exceeds the prompt limit");
        return text;
    }
}
