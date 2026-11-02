using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Repair;

/// <summary>One user verdict on an installed repair.</summary>
/// <param name="Verdict"><c>fixed</c> (the user confirmed it works) or <c>not-working</c> (the user reported it still fails).</param>
public sealed record RepairConfirmation(string Package, string TaskId, string InstalledSha256, string Verdict, DateTimeOffset At);

/// <summary>
/// Hard rule 6: "Fixed" means the user confirmed the installed repair works. Installed, compiled and sampled-healthy (the
/// content check passed) are weaker states and are never stored here. One verdict per package, bound to the repair task
/// and the exact installed jar (sha256): a later repair, a rollback or any other jar is not covered by an older "Fixed".
/// Kept in &lt;source-maintenance&gt;/confirmations/repair-confirmations.json so it survives restarts.
/// </summary>
public sealed class RepairConfirmations
{
    public const string Fixed = "fixed";
    public const string NotWorking = "not-working";
    private const string Document = "repair-confirmations.json";
    private const int MaximumBytes = 512 * 1024;
    private const int MaximumPackages = 2048;
    private readonly PrivateDocumentStore _store;
    private readonly Func<DateTimeOffset> _clock;

    public RepairConfirmations(string maintenanceRoot, Func<DateTimeOffset>? clock = null)
    {
        _store = new PrivateDocumentStore(Path.Combine(maintenanceRoot, "confirmations"));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Records the user's verdict for the installed jar of a repair task (replaces the package's earlier verdict).</summary>
    public RepairConfirmation Record(string package, string taskId, string installedSha256, string verdict)
    {
        if (verdict is not (Fixed or NotWorking)) throw new ArgumentException("Verdict must be fixed or not-working.", nameof(verdict));
        if (installedSha256.Length != 64 || !installedSha256.All(Uri.IsHexDigit)) throw new ArgumentException("installed_sha256 must be a sha256.", nameof(installedSha256));
        if (package.Length is 0 or > 200 || taskId.Length is 0 or > 64) throw new ArgumentException("Invalid repair identity.");
        var entry = new RepairConfirmation(package, taskId, installedSha256.ToLowerInvariant(), verdict, _clock());
        _store.Locked(Document, document =>
        {
            var all = Decode(document.Read(MaximumBytes));
            all[package] = entry;
            while (all.Count > MaximumPackages) all.Remove(all.MinBy(pair => pair.Value.At).Key);
            document.Write(Encode(all));
            return 0;
        });
        return entry;
    }

    /// <summary>Forgets the package's verdict (a rollback installs another jar).</summary>
    public void Clear(string package) => _store.Locked(Document, document =>
    {
        var all = Decode(document.Read(MaximumBytes));
        if (all.Remove(package)) document.Write(Encode(all));
        return 0;
    });

    /// <summary>All verdicts by package (a damaged file reads as none: nothing is ever shown as Fixed by mistake).</summary>
    public IReadOnlyDictionary<string, RepairConfirmation> All() => _store.Locked(Document, document => (IReadOnlyDictionary<string, RepairConfirmation>)Decode(document.Read(MaximumBytes)));

    /// <summary>The verdict that applies to exactly this task and installed jar, else null.</summary>
    public static RepairConfirmation? For(IReadOnlyDictionary<string, RepairConfirmation> all, string package, string taskId, string installedSha256) =>
        all.TryGetValue(package, out var entry) && entry.TaskId == taskId && installedSha256.Length > 0
            && string.Equals(entry.InstalledSha256, installedSha256, StringComparison.OrdinalIgnoreCase) ? entry : null;

    private static Dictionary<string, RepairConfirmation> Decode(byte[]? bytes)
    {
        var result = new Dictionary<string, RepairConfirmation>(StringComparer.Ordinal);
        if (bytes is null || bytes.Length == 0) return result;
        try
        {
            if (JsonNode.Parse(bytes)?["packages"] is not JsonObject packages) return result;
            foreach (var (package, node) in packages)
            {
                if (node is not JsonObject row) continue;
                var task = row["task_id"]?.GetValue<string>() ?? ""; var sha = row["installed_sha256"]?.GetValue<string>() ?? "";
                var verdict = row["verdict"]?.GetValue<string>() ?? "";
                if (task.Length == 0 || sha.Length != 64 || verdict is not (Fixed or NotWorking)) continue;
                if (!DateTimeOffset.TryParse(row["at"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) continue;
                result[package] = new(package, task, sha, verdict, at);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { result.Clear(); }
        return result;
    }

    private static byte[] Encode(Dictionary<string, RepairConfirmation> all)
    {
        var packages = new JsonObject();
        foreach (var (package, entry) in all.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            packages[package] = new JsonObject
            {
                ["task_id"] = entry.TaskId, ["installed_sha256"] = entry.InstalledSha256, ["verdict"] = entry.Verdict,
                ["at"] = entry.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            };
        return Encoding.UTF8.GetBytes(new JsonObject { ["schema"] = 1, ["packages"] = packages }.ToJsonString());
    }
}
