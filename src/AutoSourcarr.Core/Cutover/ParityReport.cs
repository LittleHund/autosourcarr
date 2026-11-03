using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Cutover;

/// <summary>
/// The parity report of one 0.4.x → 0.5.0 migration (<c>parity-report.md</c> + <c>.json</c>). It holds key names, counts,
/// ids and your manga titles; never a secret value, cookie, token or password (secrets are compared by presence only).
/// </summary>
public sealed class ParityReport
{
    public const int ListLimit = 200;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    public required string Input { get; init; }
    public required string ToVersion { get; init; }
    public string FromVersion { get; init; } = "0.4.x";
    public required string CreatedAt { get; init; }
    public required List<ParityKindResult> Decisions { get; init; }
    public required List<(string File, string Table, long Original, long Migrated)> Counts { get; init; }
    public required List<long> UnresolvedEntries { get; init; }
    public required int OriginalEntries { get; init; }
    public required List<SettingParityItem> Settings { get; init; }
    public required List<UpdateNoticeLine> Notices { get; init; }
    public required FileParityResult Files { get; init; }
    public required MergeRecheckResult Merges { get; init; }
    public JsonObject Run { get; init; } = [];
    public List<string> Warnings { get; init; } = [];

    public int LostSubscriptions => Decisions.Where(result => result.Kind.Group == ParityGroup.Subscription).Sum(result => result.Lost);
    public int LostDecisions => Decisions.Where(result => result.Kind.Group != ParityGroup.Subscription).Sum(result => result.Lost);
    public int LostSettings => Settings.Count(item => item.Lost);
    public bool Ok => LostSubscriptions == 0 && LostDecisions == 0 && LostSettings == 0;

    public string Verdict => Ok
        ? "PARITY OK: 0 lost manual decisions, 0 lost subscriptions"
        : string.Create(CultureInfo.InvariantCulture, $"PARITY FAILED: {LostDecisions} lost manual decisions, {LostSubscriptions} lost subscriptions, {LostSettings} lost settings")
          + " (" + string.Join("; ", Decisions.Where(result => result.Lost > 0).Select(result => result.Kind.Name + ": " + result.Lost.ToString(CultureInfo.InvariantCulture))
              .Concat(Settings.Where(item => item.Lost).Select(item => "setting " + item.Key))) + ")";

    public JsonObject ToJson() => new()
    {
        ["verdict"] = Verdict, ["ok"] = Ok, ["lost_manual_decisions"] = LostDecisions, ["lost_subscriptions"] = LostSubscriptions, ["lost_settings"] = LostSettings,
        ["input"] = Input, ["from_version"] = FromVersion, ["to_version"] = ToVersion, ["created_at"] = CreatedAt, ["run"] = Run.DeepClone(),
        ["warnings"] = new JsonArray(Warnings.Select(text => (JsonNode)text).ToArray()),
        ["decisions"] = new JsonArray(Decisions.Select(result => (JsonNode)new JsonObject
        {
            ["name"] = result.Kind.Name, ["group"] = result.Kind.Group.ToString().ToLowerInvariant(), ["table"] = result.Kind.Table,
            ["compared"] = result.Compared, ["original"] = result.Original, ["migrated"] = result.Migrated,
            ["missing"] = new JsonArray(result.Missing.Select(item => (JsonNode)item.ToJson()).ToArray()),
            ["changed"] = new JsonArray(result.Changed.Select(item => (JsonNode)item.ToJson()).ToArray()),
        }).ToArray()),
        ["entries"] = new JsonObject { ["original"] = OriginalEntries, ["unresolved"] = new JsonArray(UnresolvedEntries.Select(id => (JsonNode)id).ToArray()) },
        ["row_counts"] = new JsonArray(Counts.Select(row => (JsonNode)new JsonObject { ["file"] = row.File, ["table"] = row.Table, ["original"] = row.Original, ["migrated"] = row.Migrated }).ToArray()),
        ["settings"] = new JsonArray(Settings.Select(item => (JsonNode)item.ToJson()).ToArray()),
        ["update_notice"] = new JsonArray(Notices.Select(item => (JsonNode)item.ToJson()).ToArray()),
        ["files"] = Files.ToJson(),
        ["imp9_merge_recheck"] = Merges.ToJson(),
        ["reader_progress"] = "not stored by 0.4.x (Mihon keeps reading progress); nothing to compare",
    };

    public string ToMarkdown()
    {
        var md = new StringBuilder();
        void Line(string text = "") => md.Append(text).Append('\n');
        static string Cell(string? text) => (text ?? "").Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        Line("# AutoSourcarr " + FromVersion + " → " + ToVersion + " parity report");
        Line();
        Line("**" + Verdict + "**");
        Line();
        Line("Created " + CreatedAt + " from " + Input + ". This report stays on your PC: it lists your manga titles and setting names, never a secret value.");
        Line();
        if (Warnings.Count > 0)
        {
            Line("## Warnings"); Line();
            foreach (var warning in Warnings) Line("- " + warning);
            Line();
        }
        Line("## Manual decisions and subscriptions"); Line();
        Line("| Kind | Group | 0.4.x | 0.5.0 | Missing | Changed |");
        Line("| --- | --- | ---: | ---: | ---: | ---: |");
        foreach (var result in Decisions)
            Line(result.Compared
                ? string.Create(CultureInfo.InvariantCulture, $"| {Cell(result.Kind.Name)} | {result.Kind.Group} | {result.Original} | {result.Migrated} | {result.Missing.Count} | {result.Changed.Count} |")
                : $"| {Cell(result.Kind.Name)} | {result.Kind.Group} | not in this 0.4.x | | | |");
        Line();
        Line("Entry ids are compared through the merge aliases (canonical ids). Reader progress: not stored by 0.4.x (Mihon keeps it), nothing to compare.");
        Line();
        foreach (var result in Decisions.Where(result => result.Lost > 0))
        {
            Line("### " + result.Kind.Name + " — lost or changed"); Line();
            Line("| Key | Work | 0.4.x | 0.5.0 |"); Line("| --- | --- | --- | --- |");
            foreach (var item in result.Missing.Concat(result.Changed).Take(ListLimit))
                Line($"| {Cell(item.Key)} | {Cell(item.Title)} | {Cell(item.Before)} | {Cell(item.After ?? "(missing)")} |");
            if (result.Lost > ListLimit) Line(string.Create(CultureInfo.InvariantCulture, $"| … {result.Lost - ListLimit} more in parity-report.json | | | |"));
            Line();
        }
        Line("## Entries"); Line();
        Line(string.Create(CultureInfo.InvariantCulture, $"{OriginalEntries} entries in 0.4.x; {UnresolvedEntries.Count} 0.4.x entry ids no longer lead to an entry after the migration")
            + (UnresolvedEntries.Count == 0 ? "." : ": " + string.Join(", ", UnresolvedEntries.Take(ListLimit).Select(id => id.ToString(CultureInfo.InvariantCulture))) + "."));
        Line();
        Line("## Settings (autosourcarr.env / source-host.env → config.json)"); Line();
        Line("| File | Setting | Status | 0.4.x | 0.5.0 |"); Line("| --- | --- | --- | --- | --- |");
        foreach (var item in Settings)
            Line($"| {Cell(item.File)} | {Cell(item.Key)} | {Cell(item.Status)} | {Cell(item.Secret ? "(secret)" : item.Before)} | {Cell(item.Secret ? "(secret)" : item.After)} |");
        Line();
        Line("## Update notice (ARC-7): what the update changed, dropped or needs"); Line();
        foreach (var notice in Notices) Line("- **" + Cell(notice.Key) + "** (" + notice.Kind + "): " + notice.Message);
        Line();
        Line("## IMP-9: merge log and recheck of earlier merges (dry run, nothing applied)"); Line();
        Line("- Decision log (`decision_log`) present: " + (Merges.DecisionLogPresent ? "yes" : "NO") + string.Create(CultureInfo.InvariantCulture, $", {Merges.DecisionLogRows} rows."));
        Line(string.Create(CultureInfo.InvariantCulture, $"- Earlier merges: {Merges.Merges} ({Merges.ManualMerges} manual — your decisions, always kept; {Merges.AutomaticMerges} automatic)."));
        Line(string.Create(CultureInfo.InvariantCulture, $"- Automatic merges against the current rules: {Merges.Hold} would still hold, {Merges.WouldNotHold} would not, {Merges.NotRechecked} have no stored merge snapshot (not rechecked)."));
        foreach (var item in Merges.Failing.Take(ListLimit))
            Line(string.Create(CultureInfo.InvariantCulture, $"  - entry {item.Duplicate} → {item.Canonical} {Cell(item.Title)}: {item.Reason}"));
        Line();
        Line("## Files, keys and accounts (names only)"); Line();
        Line(string.Create(CultureInfo.InvariantCulture, $"{Files.Same} files unchanged, {Files.Changed.Count} changed, {Files.Missing.Count} missing, {Files.Added} new (config.json, config.key, backups, …). Databases are compared by rows above."));
        foreach (var item in Files.Keys) Line("- key file " + Cell(item.Path) + ": " + item.Status);
        foreach (var item in Files.Missing.Take(ListLimit)) Line("- missing: " + Cell(item.Path));
        foreach (var item in Files.Changed.Take(ListLimit)) Line("- changed: " + Cell(item.Path));
        Line("- Accounts: 0.4.x had none (Authelia only); 0.5.0 keeps them in config/accounts/accounts.json.");
        Line();
        Line("## Row counts (every table in both files)"); Line();
        Line("| File | Table | 0.4.x | 0.5.0 |"); Line("| --- | --- | ---: | ---: |");
        foreach (var row in Counts) Line(string.Create(CultureInfo.InvariantCulture, $"| {row.File} | {row.Table} | {row.Original} | {row.Migrated} |"));
        Line();
        Line("## Migration run"); Line();
        Line("```json"); Line(Run.ToJsonString(Indented)); Line("```");
        Line();
        Line("**" + Verdict + "**");
        return md.ToString();
    }
}
