using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Repair.Migration;

/// <summary>What a domain repair needs from the source host (the SuwayomiClient source-host API in production).</summary>
public interface IDomainRepairHost
{
    /// <summary>The host's extension row (pkgName, versionName, isInstalled, loadError, source.nodes with id/name/lang/baseUrl), or null.</summary>
    Task<JsonObject?> ExtensionAsync(string packageName, CancellationToken cancellationToken);
    Task<byte[]> ReadJarAsync(string packageName, CancellationToken cancellationToken);
    /// <summary>Installs a jar for the installed package and answers the new extension row.</summary>
    Task<JsonObject> InstallJarAsync(string packageName, byte[] jar, CancellationToken cancellationToken);
    IContentCheckSource Content { get; }
}

public sealed class SuwayomiDomainRepairHost(SuwayomiClient client) : IDomainRepairHost
{
    public IContentCheckSource Content { get; } = new SuwayomiContentCheckSource(client);
    public Task<JsonObject?> ExtensionAsync(string packageName, CancellationToken cancellationToken) => client.HostExtensionAsync(packageName, cancellationToken);
    public Task<byte[]> ReadJarAsync(string packageName, CancellationToken cancellationToken) => client.ReadExtensionJarAsync(packageName, cancellationToken);
    public Task<JsonObject> InstallJarAsync(string packageName, byte[] jar, CancellationToken cancellationToken) => client.InstallExtensionJarAsync(packageName, jar, cancellationToken);
}

/// <summary>A request the repair cannot run at all (bad package name, no kept jar); <see cref="Status"/> is the HTTP status.</summary>
public sealed class DomainRepairException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Domain repair inputs: an explicit new host (else the old site's redirect is observed), the old host when the
/// package's sources use several, a search text for the content check, and an http-to-https upgrade.</summary>
public sealed record DomainRepairOptions(string? NewHost = null, string? OldHost = null, string? Query = null, bool UpgradeToHttps = false);

/// <summary>
/// Phase 6e domain repair of one installed extension:
/// (a) read the installed jar from the host (last working code) and keep it under
///     source-maintenance/last-working/&lt;pkg&gt;/&lt;sha256&gt;.jar with a JSON record;
/// (b) find the new address (explicit, or the old base URL's observed cross-site redirect);
/// (c) rewrite the old host in the compiled string constants and run the structure check (<see cref="DomainMigration"/>);
/// (d) install the repaired jar through the host; (e) run the content check on one of the package's sources;
/// (f) when the check fails, install the kept jar again (rollback).
/// Every run ends in a report (state repaired, refused, failed, rolled_back or rollback_failed) that is saved as the
/// package's last report under source-maintenance/domain-repairs/&lt;pkg&gt;.json. Host changes run under the shared
/// source admin lock.
/// </summary>
public sealed class DomainRepair
{
    public const int KeptPerPackage = 5;
    private static readonly Regex Package = new(@"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*){1,20}\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly Regex Digest = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    private readonly string _root;
    private readonly IDomainRepairHost _host;
    private readonly SemaphoreSlim _adminLock;
    private readonly Func<string, CancellationToken, Task<JsonObject>>? _observe;
    private readonly TimeProvider _time;

    /// <param name="maintenanceRoot">The app's &lt;data&gt;/source-maintenance folder.</param>
    /// <param name="observe">Observes one public URL and answers WebsiteProbe.OneAsync's JSON (state, final_url, redirects); null disables discovery.</param>
    public DomainRepair(string maintenanceRoot, IDomainRepairHost host, SemaphoreSlim adminLock,
        Func<string, CancellationToken, Task<JsonObject>>? observe = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maintenanceRoot);
        _root = Path.GetFullPath(maintenanceRoot);
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _adminLock = adminLock ?? throw new ArgumentNullException(nameof(adminLock));
        _observe = observe;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Production wiring: the source host client and redirect observation through the public fetcher.</summary>
    public DomainRepair(string maintenanceRoot, SuwayomiClient client, SemaphoreSlim adminLock, IPublicSourceFetcher? fetcher)
        : this(maintenanceRoot, new SuwayomiDomainRepairHost(client), adminLock, fetcher is null ? null : new Func<string, CancellationToken, Task<JsonObject>>((url, token) => ObserveAsync(fetcher, url, token)))
    {
    }

    /// <summary>One bounded observation of a public address through <see cref="WebsiteProbe"/> on <see cref="FetcherObservationTransport"/>.</summary>
    public static async Task<JsonObject> ObserveAsync(IPublicSourceFetcher fetcher, string url, CancellationToken cancellationToken)
    {
        using var probe = new WebsiteProbe(new FetcherObservationTransport(fetcher), pace: TimeSpan.FromMilliseconds(500), deadline: TimeSpan.FromSeconds(60));
        var result = await probe.OneAsync(url, cancellationToken: cancellationToken).ConfigureAwait(false);
        result.Remove("_api_targets");
        return result;
    }

    public string LastWorkingDirectory(string packageName) => Path.Combine(_root, "last-working", RequirePackage(packageName));
    public string ReportPath(string packageName) => Path.Combine(_root, "domain-repairs", RequirePackage(packageName) + ".json");

    public static bool IsPackageName(string? value) => value is not null && value.Length <= 200 && Package.IsMatch(value);

    private static string RequirePackage(string packageName) =>
        IsPackageName(packageName) ? packageName : throw new DomainRepairException(400, "invalid_package", "A plain extension package name is required.");

    /// <summary>The last saved report of a package, or null.</summary>
    public JsonObject? LastReport(string packageName)
    {
        var path = ReportPath(packageName);
        var bytes = ReadBounded(path, 4 * 1024 * 1024);
        if (bytes is null) return null;
        try { return JsonNode.Parse(bytes) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Runs the whole repair (a–f). Expected failures end in the report; only a bad package name or cancellation throw.</summary>
    public async Task<JsonObject> RunAsync(string packageName, DomainRepairOptions? options = null, Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequirePackage(packageName);
        options ??= new();
        var report = new JsonObject
        {
            ["kind"] = "domain-repair", ["package"] = packageName, ["started_at"] = Now(), ["state"] = "running",
            ["requested_new_host"] = options.NewHost, ["requested_old_host"] = options.OldHost,
        };
        void Say(string text) { report["message"] = text; progress?.Invoke(text); }
        JsonObject Finish(string state, string message)
        {
            report["state"] = state; report["message"] = message; report["finished_at"] = Now();
            SaveReport(packageName, report);
            return report;
        }

        await _adminLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // (a) the last working code
            Say("Reading the installed extension");
            var extension = await _host.ExtensionAsync(packageName, cancellationToken).ConfigureAwait(false);
            if (extension is null || !(SourceResponseContract.Boolean(extension["isInstalled"], out var installed) && installed))
                return Finish("refused", packageName + " is not installed on the source host.");
            var loadError = SourceResponseContract.Text(extension["loadError"]);
            if (loadError.Length > 0) return Finish("refused", "The installed extension does not load (" + loadError + "); a domain migration cannot repair that.");
            var sources = (extension["source"] as JsonObject)?["nodes"] is JsonArray nodes ? nodes.OfType<JsonObject>().ToList() : [];
            report["version"] = SourceResponseContract.Text(extension["versionName"]);
            report["sources_before"] = Sources(sources);
            var jar = await _host.ReadJarAsync(packageName, cancellationToken).ConfigureAwait(false);
            var kept = Keep(packageName, jar, extension, "before-domain-repair");
            report["kept"] = kept;
            var keptSha = SourceResponseContract.Text(kept["sha256"]);

            // (b) old and new address
            string oldHost;
            try { oldHost = OldHost(sources, options.OldHost); }
            catch (DomainMigrationException error) { report["reason"] = error.Code; return Finish("refused", error.Message); }
            report["old_host"] = oldHost;
            var oldBase = sources.Select(row => SourceResponseContract.Text(row["baseUrl"]))
                .FirstOrDefault(url => HostOf(url) == oldHost) ?? "https://" + oldHost;
            string newHost; var upgrade = options.UpgradeToHttps;
            if (!string.IsNullOrWhiteSpace(options.NewHost))
            {
                try { newHost = DomainMigration.NormalizeHost(options.NewHost); }
                catch (DomainMigrationException error) { report["reason"] = error.Code; return Finish("refused", error.Message); }
                if (options.NewHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && oldBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) upgrade = true;
                report["address_basis"] = "explicit";
            }
            else if (_observe is null) return Finish("refused", "No new address was given and website observation is not available here.");
            else
            {
                Say("Looking for the new address of " + oldHost);
                var observation = await _observe(oldBase, cancellationToken).ConfigureAwait(false);
                report["observation"] = observation.DeepClone();
                var final = SourceResponseContract.Text(observation["final_url"]);
                var finalHost = HostOf(final);
                if (SourceResponseContract.Text(observation["state"]) != "working" || finalHost.Length == 0)
                { report["reason"] = "new_host_unknown"; return Finish("refused", "The old address does not lead to a working site; give the new address."); }
                if (DomainMigration.SameSite(finalHost, oldHost))
                { report["reason"] = "new_host_unknown"; return Finish("refused", "The old address still answers on the same site (" + finalHost + "); give the new address if it moved."); }
                newHost = finalHost;
                if (final.StartsWith("https://", StringComparison.Ordinal) && oldBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) upgrade = true;
                report["address_basis"] = "observed-redirect";
            }
            report["new_host"] = newHost;

            // (c) rewrite and structure check
            Say("Rewriting " + oldHost + " to " + newHost);
            DomainMigrationResult migration;
            try { migration = DomainMigration.Migrate(jar, oldHost, newHost, upgrade ? "https" : null); }
            catch (DomainMigrationException error)
            {
                report["reason"] = error.Code;
                return Finish(error.Code == "structure_check" ? "failed" : "refused", error.Message);
            }
            report["migration"] = migration.Report;

            // (d) install
            Say("Installing the repaired extension");
            JsonObject repaired;
            try { repaired = await _host.InstallJarAsync(packageName, migration.Jar, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                report["install_error"] = error.Message;
                return await RestoreAsync(packageName, jar, keptSha, report, "The repaired extension could not be installed: " + error.Message, Finish, cancellationToken).ConfigureAwait(false);
            }
            var after = (repaired["source"] as JsonObject)?["nodes"] is JsonArray afterNodes ? afterNodes.OfType<JsonObject>().ToList() : [];
            report["sources_after"] = Sources(after);
            var repairedError = SourceResponseContract.Text(repaired["loadError"]);
            if (repairedError.Length > 0)
                return await RestoreAsync(packageName, jar, keptSha, report, "The repaired extension does not load: " + repairedError, Finish, cancellationToken).ConfigureAwait(false);

            // (e) content check on a source that used the old host
            var checkedIds = sources.Where(row => HostOf(SourceResponseContract.Text(row["baseUrl"])) == oldHost)
                .Select(row => SourceResponseContract.Identity(row["id"])).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
            var target = after.FirstOrDefault(row => checkedIds.Contains(SourceResponseContract.Identity(row["id"]) ?? "")
                    && SourceResponseContract.Text(row["lang"]) == "en")
                ?? after.FirstOrDefault(row => checkedIds.Contains(SourceResponseContract.Identity(row["id"]) ?? ""))
                ?? after.FirstOrDefault();
            if (target is null)
                return await RestoreAsync(packageName, jar, keptSha, report, "The repaired extension has no sources to check.", Finish, cancellationToken).ConfigureAwait(false);
            var sourceId = SourceResponseContract.Identity(target["id"]) ?? "";
            Say("Checking content of " + SourceResponseContract.Text(target["name"]));
            var check = await ContentCheck.RunAsync(_host.Content, sourceId, SourceResponseContract.Text(target["name"]), options.Query,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            report["content_check"] = check.ToJson();
            if (check.Ok)
            {
                report["installed_sha256"] = migration.Report["output_sha256"]?.DeepClone();
                return Finish("repaired", "Repaired: " + oldHost + " now reads " + newHost + "; the content check passed ("
                    + check.Pages.ToString(CultureInfo.InvariantCulture) + " pages, first image " + check.ImageBytes.ToString(CultureInfo.InvariantCulture) + " bytes).");
            }

            // (f) rollback
            return await RestoreAsync(packageName, jar, keptSha, report, "The content check failed at " + check.Step + ": " + check.Error, Finish, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException && error is not DomainRepairException)
        {
            report["error"] = error.GetType().Name;
            return Finish("failed", error.Message);
        }
        finally { _adminLock.Release(); }
    }

    /// <summary>Installs the most recently kept jar of a package again (the last working code before a repair).</summary>
    public async Task<JsonObject> RollbackAsync(string packageName, CancellationToken cancellationToken = default)
    {
        RequirePackage(packageName);
        var (sha, record, jar) = LatestKept(packageName) ?? throw new DomainRepairException(404, "nothing_kept", "No working jar of " + packageName + " has been kept.");
        var report = new JsonObject { ["kind"] = "domain-repair-rollback", ["package"] = packageName, ["started_at"] = Now(), ["kept"] = record.DeepClone() };
        await _adminLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var answer = await _host.InstallJarAsync(packageName, jar, cancellationToken).ConfigureAwait(false);
            report["sources_after"] = Sources((answer["source"] as JsonObject)?["nodes"] is JsonArray nodes ? nodes.OfType<JsonObject>().ToList() : []);
            var loadError = SourceResponseContract.Text(answer["loadError"]);
            report["state"] = loadError.Length == 0 ? "rolled_back" : "rollback_failed";
            report["message"] = loadError.Length == 0 ? "The kept jar " + sha[..12] + " is installed again." : "The kept jar does not load: " + loadError;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            report["state"] = "rollback_failed"; report["message"] = "The kept jar could not be installed: " + error.Message;
        }
        finally { _adminLock.Release(); }
        report["finished_at"] = Now();
        SaveReport(packageName, report);
        return report;
    }

    private async Task<JsonObject> RestoreAsync(string packageName, byte[] kept, string keptSha, JsonObject report, string reason,
        Func<string, string, JsonObject> finish, CancellationToken cancellationToken)
    {
        report["failure"] = reason;
        try
        {
            // The host's own failure path already reloads the previous jar; installing the kept bytes again makes the
            // outcome explicit and also covers a repaired jar that loaded but does not work.
            var answer = await _host.InstallJarAsync(packageName, kept, CancellationToken.None).ConfigureAwait(false);
            var loadError = SourceResponseContract.Text(answer["loadError"]);
            report["rollback"] = new JsonObject { ["sha256"] = keptSha, ["load_error"] = loadError.Length == 0 ? null : loadError };
            if (loadError.Length > 0) return finish("rollback_failed", reason + " The kept jar was installed again but does not load: " + loadError);
            return finish("rolled_back", reason + " The last working jar is installed again.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            report["rollback"] = new JsonObject { ["sha256"] = keptSha, ["error"] = error.Message };
            cancellationToken.ThrowIfCancellationRequested();
            return finish("rollback_failed", reason + " Reinstalling the kept jar failed: " + error.Message + ". Use rollback again.");
        }
    }

    // ------------------------------------------------------------------ kept jars

    private JsonObject Keep(string packageName, byte[] jar, JsonObject extension, string reason)
    {
        var directory = LastWorkingDirectory(packageName);
        var sha = DomainMigration.Sha(jar);
        var jarPath = Path.Combine(directory, sha + ".jar");
        if (!File.Exists(jarPath)) ConfigFiles.WriteAtomic(jarPath, jar);
        var record = new JsonObject
        {
            ["package"] = packageName, ["sha256"] = sha, ["bytes"] = jar.Length, ["version_name"] = SourceResponseContract.Text(extension["versionName"]),
            ["kept_at"] = Now(), ["reason"] = reason,
            ["base_urls"] = new JsonArray(((extension["source"] as JsonObject)?["nodes"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(row => SourceResponseContract.Text(row["baseUrl"])).Where(url => url.Length > 0).Distinct(StringComparer.Ordinal)
                .Select(url => (JsonNode?)JsonValue.Create(url)).ToArray()),
        };
        ConfigFiles.WriteAtomic(Path.Combine(directory, sha + ".json"), Encoding.UTF8.GetBytes(record.ToJsonString(Indented)));
        Prune(directory, sha);
        return record;
    }

    private static void Prune(string directory, string current)
    {
        var records = Records(directory).OrderByDescending(item => item.KeptAt).ToList();
        foreach (var (sha, _, _) in records.Skip(KeptPerPackage))
        {
            if (sha == current) continue;
            TryDelete(Path.Combine(directory, sha + ".jar"));
            TryDelete(Path.Combine(directory, sha + ".json"));
        }
    }

    private (string Sha, JsonObject Record, byte[] Jar)? LatestKept(string packageName)
    {
        var directory = LastWorkingDirectory(packageName);
        foreach (var (sha, _, record) in Records(directory).OrderByDescending(item => item.KeptAt))
        {
            var jar = ReadBounded(Path.Combine(directory, sha + ".jar"), SuwayomiClient.MaximumExtensionJar);
            if (jar is null || DomainMigration.Sha(jar) != sha) continue;
            return (sha, record, jar);
        }
        return null;
    }

    private static IEnumerable<(string Sha, DateTimeOffset KeptAt, JsonObject Record)> Records(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var sha = Path.GetFileNameWithoutExtension(path);
            if (!Digest.IsMatch(sha)) continue;
            var bytes = ReadBounded(path, 1024 * 1024);
            if (bytes is null) continue;
            JsonObject? record;
            try { record = JsonNode.Parse(bytes) as JsonObject; }
            catch (JsonException) { continue; }
            if (record is null || !DateTimeOffset.TryParse(SourceResponseContract.Text(record["kept_at"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var keptAt)) continue;
            yield return (sha, keptAt, record);
        }
    }

    // ------------------------------------------------------------------ helpers

    private void SaveReport(string packageName, JsonObject report) =>
        ConfigFiles.WriteAtomic(ReportPath(packageName), Encoding.UTF8.GetBytes(report.ToJsonString(Indented)));

    private string Now() => _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static string OldHost(List<JsonObject> sources, string? requested)
    {
        var hosts = sources.Select(row => HostOf(SourceResponseContract.Text(row["baseUrl"]))).Where(host => host.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var host = DomainMigration.NormalizeHost(requested);
            if (hosts.Count > 0 && !hosts.Contains(host)) throw new DomainMigrationException("old_host_unused", "No source of this package uses " + host + ".");
            return host;
        }
        return hosts.Count switch
        {
            1 => hosts[0],
            0 => throw new DomainMigrationException("old_host_unknown", "The package's sources report no website address; give the old host."),
            _ => throw new DomainMigrationException("old_host_ambiguous", "The package's sources use several hosts (" + string.Join(", ", hosts) + "); give the old host."),
        };
    }

    private static string HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "";
        try { return DomainMigration.NormalizeHost(uri.IdnHost); }
        catch (DomainMigrationException) { return ""; }
    }

    private static JsonArray Sources(List<JsonObject> rows) => new(rows.Select(row => (JsonNode?)new JsonObject
    {
        ["id"] = SourceResponseContract.Identity(row["id"]), ["name"] = SourceResponseContract.Text(row["name"]),
        ["lang"] = SourceResponseContract.Text(row["lang"]), ["base_url"] = SourceResponseContract.Text(row["baseUrl"]),
    }).ToArray());

    private static byte[]? ReadBounded(string path, int maximum)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null) return null;
        if (info.Length > maximum) throw new InvalidDataException(Path.GetFileName(path) + " is larger than expected.");
        return File.ReadAllBytes(path);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
