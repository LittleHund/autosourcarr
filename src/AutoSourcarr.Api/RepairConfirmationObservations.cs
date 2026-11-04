using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Repair.Migration;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Api;

/// <summary>
/// Sources → Health (audit A10): the user's persisted repair verdicts (hard rule 6) as the repair histories the health
/// snapshot reads: per package <c>{working_at, current: {status, source_ids}}</c>. A verdict applies only while the jar
/// it was given for is still the installed repair (the package's last report) and is the jar the source host has
/// installed now (an update or upload replaces it without a repair report); otherwise the status is "unconfirmed".
/// Statuses follow 0.4.5: confirmed (the user said Fixed), reported-not-working, unconfirmed. Unreadable verdicts are
/// reported through <paramref name="recordError"/> rather than silently shown as none.
/// </summary>
internal sealed class RepairConfirmationObservations(RepairConfirmations confirmations, DomainRepair? domain, Action<string>? recordError = null) : IRepairHealthObservations
{
    public async Task<(JsonArray Jobs, JsonObject Operation, JsonObject Histories)> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var histories = new JsonObject();
        IReadOnlyDictionary<string, RepairConfirmation> all;
        try { all = confirmations.All(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            recordError?.Invoke("The saved repair verdicts cannot be read: " + error.Message);
            all = new Dictionary<string, RepairConfirmation>();
        }
        foreach (var (package, verdict) in all)
        {
            JsonObject? report = null;
            try { report = domain?.LastReport(package); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or DomainRepairException) { report = null; }
            var installed = report?["installed_sha256"] is JsonValue sha && sha.TryGetValue<string>(out var digest) ? digest : "";
            var current = string.Equals(installed, verdict.InstalledSha256, StringComparison.OrdinalIgnoreCase);
            if (current && domain is not null)
            {
                // The host's jar now: a verdict for code that is no longer installed is not current (hard rule 6).
                // A local timeout or deferral is no answer: the report's verdict stands (a health read never fails on it).
                try { current = string.Equals(await domain.InstalledShaAsync(package, cancellationToken).ConfigureAwait(false), verdict.InstalledSha256, StringComparison.OrdinalIgnoreCase); }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && SourceResultTracker.IsLocal(error)) { }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested) { _ = error; current = false; }
            }
            var fixedNow = current && verdict.Verdict == RepairConfirmations.Fixed;
            var ids = new JsonArray((report?["sources_after"] as JsonArray ?? []).OfType<JsonObject>().Select(row => row["id"]?.DeepClone()).ToArray());
            histories[package] = new JsonObject
            {
                ["working_at"] = fixedNow ? verdict.At.ToUnixTimeMilliseconds() / 1000.0 : null,
                ["current"] = new JsonObject
                {
                    ["status"] = !current ? "unconfirmed" : fixedNow ? "confirmed" : "reported-not-working",
                    ["source_ids"] = ids,
                },
            };
        }
        return (new JsonArray(), new JsonObject(), histories);
    }
}
