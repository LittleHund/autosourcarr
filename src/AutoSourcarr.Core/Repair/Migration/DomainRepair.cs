using System.Collections.Concurrent;
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
/// package's sources use several, a search text for the content check, an http-to-https upgrade, and the content check's
/// overall deadline (default <see cref="ContentCheck.DefaultDeadline"/>, kept shorter than the repair job's stall timeout).</summary>
public sealed record DomainRepairOptions(string? NewHost = null, string? OldHost = null, string? Query = null, bool UpgradeToHttps = false,
    TimeSpan? ContentDeadline = null);

/// <summary>
/// Phase 6e domain repair of one installed extension:
/// (0) SRC-8: when a configured repository now offers a maintained (newer) version of the package, the repair is
///     refused (reason official_replacement) and the person is told to update from the repository instead;
/// (a) read the installed jar from the host (last working code) and keep it under
///     source-maintenance/last-working/&lt;pkg&gt;/&lt;sha256&gt;.jar with a JSON record;
/// (b) find the new address (explicit, or the old base URL's observed cross-site redirect; parked-domain targets are
///     refused, see <see cref="ParkingHost"/>);
/// (c) rewrite the old host in the compiled string constants and run the structure check (<see cref="DomainMigration"/>);
/// (d) install the repaired jar through the host; (e) run the content check on one of the package's sources (bounded
///     by <see cref="DomainRepairOptions.ContentDeadline"/>, progress reported);
/// (f) when the check fails, install the kept jar again (rollback).
/// Every run ends in a report (state repaired, refused, failed, rolled_back or rollback_failed) that is saved as the
/// package's last report under source-maintenance/domain-repairs/&lt;pkg&gt;.json. Only host changes and host reads (jar
/// read, install, restore) hold the shared source admin lock; the redirect lookup and the content check do not. One
/// repair or rollback per package runs at a time (a second one is refused, reason busy). A cancellation after the
/// repaired jar was installed (cancel route or job watchdog) restores the kept jar without a token, saves the report
/// as rolled_back (reason cancelled; rollback_failed when the restore failed) and then rethrows the cancellation.
/// </summary>
public sealed class DomainRepair
{
    public const int KeptPerPackage = 5;
    private static readonly Regex Package = new(@"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*){1,20}\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly Regex Digest = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    /// <summary>
    /// Well-known domain parking and for-sale landing services. A moved site never redirects there; an expired domain
    /// does. Matched on the final host and every redirect hop (the host or any subdomain of it).
    /// </summary>
    private static readonly string[] ParkingServices =
    [
        "sedoparking.com", "sedo.com", "bodis.com", "parkingcrew.net", "parkingcrew.com", "above.com", "dan.com", "afternic.com",
        "hugedomains.com", "undeveloped.com", "domainmarket.com", "parklogic.com", "voodoo.com", "fabulous.com", "skenzo.com",
        "dsparking.com", "parkingpage.namecheap.com", "domainnamesales.com", "buydomains.com", "sav.com", "porkbun.com",
        "godaddy.com", "domaincontrol.com", "uniregistry.com", "epik.com", "squadhelp.com", "atom.com", "brandbucket.com",
        "domainlore.com", "namebright.com", "smartname.com", "trafficz.com", "domainsponsor.com", "searchvity.com",
    ];
    private readonly string _root;
    private readonly IDomainRepairHost _host;
    private readonly SemaphoreSlim _adminLock;
    private readonly Func<string, CancellationToken, Task<JsonObject>>? _observe;
    private readonly Func<string, CancellationToken, Task<string?>>? _officialReplacement;
    private readonly TimeProvider _time;
    // REP-1: package -> "versionName|versionCode" whose installed jar is already kept as last working; a repair or
    // rollback of the package forgets it, because those keep the version but change the jar.
    private readonly ConcurrentDictionary<string, string> _keptVersions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _keeping = new(StringComparer.Ordinal);
    // The per-package gate: a repair or rollback of a package runs alone, and no last-working keep reads the package
    // while a repaired jar that has not passed its content check yet is installed.
    private readonly ConcurrentDictionary<string, byte> _changing = new(StringComparer.Ordinal);

    /// <param name="maintenanceRoot">The app's &lt;data&gt;/source-maintenance folder.</param>
    /// <param name="observe">Observes one public URL and answers WebsiteProbe.OneAsync's JSON (state, final_url, redirects); null disables discovery.</param>
    /// <param name="officialReplacement">SRC-8: answers why a configured repository now offers a maintained version of the
    /// package (the repair is then refused), or null when it does not; null skips the check.</param>
    public DomainRepair(string maintenanceRoot, IDomainRepairHost host, SemaphoreSlim adminLock,
        Func<string, CancellationToken, Task<JsonObject>>? observe = null, TimeProvider? time = null,
        Func<string, CancellationToken, Task<string?>>? officialReplacement = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maintenanceRoot);
        _root = Path.GetFullPath(maintenanceRoot);
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _adminLock = adminLock ?? throw new ArgumentNullException(nameof(adminLock));
        _observe = observe;
        _officialReplacement = officialReplacement;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Production wiring: the source host client and redirect observation through the public fetcher.</summary>
    public DomainRepair(string maintenanceRoot, SuwayomiClient client, SemaphoreSlim adminLock, IPublicSourceFetcher? fetcher,
        Func<string, CancellationToken, Task<string?>>? officialReplacement = null)
        : this(maintenanceRoot, new SuwayomiDomainRepairHost(client), adminLock,
            fetcher is null ? null : new Func<string, CancellationToken, Task<JsonObject>>((url, token) => ObserveAsync(fetcher, url, token)),
            officialReplacement: officialReplacement)
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

    /// <summary>
    /// Look-alike guard for observed redirects: the parking or for-sale service the observation ended on or passed
    /// through, or null. Explicit new hosts given by the admin are not checked.
    /// </summary>
    public static string? ParkingHost(JsonObject observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var hosts = new List<string> { SourceResponseContract.Text(observation["final_url"]) };
        if (observation["redirects"] is JsonArray redirects) hosts.AddRange(redirects.Select(item => SourceResponseContract.Text(item)));
        foreach (var url in hosts)
        {
            var host = HostOf(url);
            if (host.Length == 0) continue;
            var parked = ParkingServices.FirstOrDefault(service => host == service || host.EndsWith("." + service, StringComparison.Ordinal));
            if (parked is not null) return host;
        }
        return null;
    }

    /// <summary>True when both hosts share their name label (the label before the public suffix, e.g. "mangasite" in
    /// mangasite.com and www.mangasite.net). Informational: the content check decides.</summary>
    public static bool SharesNameLabel(string first, string second)
    {
        static string Label(string host)
        {
            var labels = host.Split('.');
            if (labels.Length < 2) return host;
            // Two-label public suffixes such as co.uk or com.br: the name is the label before them.
            var index = labels.Length >= 3 && labels[^2].Length <= 3 && labels[^1].Length == 2 ? labels.Length - 3 : labels.Length - 2;
            return labels[index];
        }
        return first.Length > 0 && second.Length > 0 && Label(first) == Label(second);
    }

    public string LastWorkingDirectory(string packageName) => Path.Combine(_root, "last-working", RequirePackage(packageName));
    public string ReportPath(string packageName) => Path.Combine(_root, "domain-repairs", RequirePackage(packageName) + ".json");

    public static bool IsPackageName(string? value) => value is not null && value.Length <= 200 && Package.IsMatch(value);

    private static string RequirePackage(string packageName) =>
        IsPackageName(packageName) ? packageName : throw new DomainRepairException(400, "invalid_package", "A plain extension package name is required.");

    /// <summary>True while a repair or rollback of the package runs.</summary>
    public bool IsChanging(string packageName) => _changing.ContainsKey(packageName);

    /// <summary>The last saved report of a package, or null.</summary>
    public JsonObject? LastReport(string packageName)
    {
        var path = ReportPath(packageName);
        var bytes = ReadBounded(path, 4 * 1024 * 1024);
        if (bytes is null) return null;
        try { return JsonNode.Parse(bytes) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private async Task<T> UnderAdminLockAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        await _adminLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally { _adminLock.Release(); }
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
        if (!_changing.TryAdd(packageName, 0))
        {
            // Not saved: the running repair owns the package's last report.
            report["state"] = "refused"; report["reason"] = "busy"; report["finished_at"] = Now();
            report["message"] = "A repair or rollback of " + packageName + " is already running.";
            return report;
        }
        void Say(string text)
        {
            report["message"] = text;
            try { progress?.Invoke(text); }
            catch (Exception) { }
        }
        JsonObject Finish(string state, string message)
        {
            report["state"] = state; report["message"] = message; report["finished_at"] = Now();
            SaveReport(packageName, report);
            return report;
        }

        _keptVersions.TryRemove(packageName, out _);
        // Set once the repaired jar is installed: from then on a cancellation restores the kept jar first.
        byte[]? keptJar = null; var keptSha = ""; var installed = false;
        Task<JsonObject> Restore(byte[] kept, string reason)
        {
            installed = false;
            return RestoreAsync(packageName, kept, keptSha, report, reason, Finish);
        }
        try
        {
            // (0) SRC-8: a maintained version in a configured repository replaces the repair.
            if (_officialReplacement is not null)
            {
                Say("Checking the configured repositories for a maintained version");
                string? official;
                try { official = await _officialReplacement(packageName, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    official = null;
                    report["official_replacement_check"] = "unavailable: " + error.Message;
                }
                if (official is not null)
                {
                    report["reason"] = "official_replacement"; report["official_replacement"] = official;
                    return Finish("refused", "A maintained version is available from a configured repository (" + official
                        + "). Update or install it from the repository in Sources instead of repairing.");
                }
            }

            // (a) the last working code (host reads under the admin lock)
            Say("Reading the installed extension");
            var (extension, jar) = await UnderAdminLockAsync<(JsonObject? Row, byte[]? Jar)>(async () =>
            {
                var row = await _host.ExtensionAsync(packageName, cancellationToken).ConfigureAwait(false);
                if (row is null || !(SourceResponseContract.Boolean(row["isInstalled"], out var present) && present)
                    || SourceResponseContract.Text(row["loadError"]).Length > 0)
                    return (row, (byte[]?)null);
                return (row, await _host.ReadJarAsync(packageName, cancellationToken).ConfigureAwait(false));
            }, cancellationToken).ConfigureAwait(false);
            if (extension is null || !(SourceResponseContract.Boolean(extension["isInstalled"], out var isInstalled) && isInstalled))
                return Finish("refused", packageName + " is not installed on the source host.");
            var loadError = SourceResponseContract.Text(extension["loadError"]);
            if (loadError.Length > 0) return Finish("refused", "The installed extension does not load (" + loadError + "); a domain migration cannot repair that.");
            if (jar is null) return Finish("failed", "The installed jar could not be read.");
            var sources = (extension["source"] as JsonObject)?["nodes"] is JsonArray nodes ? nodes.OfType<JsonObject>().ToList() : [];
            report["version"] = SourceResponseContract.Text(extension["versionName"]);
            report["sources_before"] = Sources(sources);
            var kept = Keep(packageName, jar, extension, "before-domain-repair");
            report["kept"] = kept;
            keptSha = SourceResponseContract.Text(kept["sha256"]);

            // (b) old and new address (no lock: public requests)
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
                if (ParkingHost(observation) is { } parked)
                {
                    report["reason"] = "parked_domain";
                    return Finish("refused", "The old address redirects to a domain parking or for-sale page (" + parked + "), not to a moved site; give the new address if you know it.");
                }
                newHost = finalHost;
                if (final.StartsWith("https://", StringComparison.Ordinal) && oldBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) upgrade = true;
                report["address_basis"] = "observed-redirect";
                // A different name is allowed (sites rename when they move) but only the content check can accept it.
                report["shares_name_label"] = SharesNameLabel(oldHost, newHost);
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

            // (d) install (admin lock)
            Say("Installing the repaired extension");
            keptJar = jar;
            JsonObject repaired;
            try
            {
                repaired = await UnderAdminLockAsync(async () =>
                {
                    // From the first byte sent the host may have the repaired jar: a cancellation restores.
                    installed = true;
                    return await _host.InstallJarAsync(packageName, migration.Jar, cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                report["install_error"] = error.Message;
                return await Restore(jar, "The repaired extension could not be installed: " + error.Message).ConfigureAwait(false);
            }
            var after = (repaired["source"] as JsonObject)?["nodes"] is JsonArray afterNodes ? afterNodes.OfType<JsonObject>().ToList() : [];
            report["sources_after"] = Sources(after);
            var repairedError = SourceResponseContract.Text(repaired["loadError"]);
            if (repairedError.Length > 0)
                return await Restore(jar, "The repaired extension does not load: " + repairedError).ConfigureAwait(false);

            // (e) content check on a source that used the old host (no lock: source traffic, bounded)
            var checkedIds = sources.Where(row => HostOf(SourceResponseContract.Text(row["baseUrl"])) == oldHost)
                .Select(row => SourceResponseContract.Identity(row["id"])).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
            var target = after.FirstOrDefault(row => checkedIds.Contains(SourceResponseContract.Identity(row["id"]) ?? "")
                    && SourceResponseContract.Text(row["lang"]) == "en")
                ?? after.FirstOrDefault(row => checkedIds.Contains(SourceResponseContract.Identity(row["id"]) ?? ""))
                ?? after.FirstOrDefault();
            if (target is null)
                return await Restore(jar, "The repaired extension has no sources to check.").ConfigureAwait(false);
            var sourceId = SourceResponseContract.Identity(target["id"]) ?? "";
            Say("Checking content of " + SourceResponseContract.Text(target["name"]));
            var check = await ContentCheck.RunAsync(_host.Content, sourceId, SourceResponseContract.Text(target["name"]), options.Query,
                cancellationToken: cancellationToken, deadline: options.ContentDeadline, progress: Say).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            report["content_check"] = check.ToJson();
            if (check.Ok)
            {
                report["installed_sha256"] = migration.Report["output_sha256"]?.DeepClone();
                installed = false;
                return Finish("repaired", "Repaired: " + oldHost + " now reads " + newHost + "; the content check passed ("
                    + check.Pages.ToString(CultureInfo.InvariantCulture) + " pages, first image " + check.ImageBytes.ToString(CultureInfo.InvariantCulture) + " bytes).");
            }

            // (f) rollback
            return await Restore(jar, "The content check failed at " + check.Step + ": " + check.Error).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (installed && keptJar is not null)
        {
            // Never leave an unverified repaired jar installed: restore without a token, save, then report the cancel.
            report["reason"] = "cancelled";
            await Restore(keptJar!, "The repair was cancelled after the repaired extension was installed.").ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException && error is not DomainRepairException)
        {
            report["error"] = error.GetType().Name;
            if (installed && keptJar is not null)
                return await Restore(keptJar!, "The repair failed after the repaired extension was installed: " + error.Message).ConfigureAwait(false);
            return Finish("failed", error.Message);
        }
        finally
        {
            _keptVersions.TryRemove(packageName, out _);
            _changing.TryRemove(packageName, out _);
        }
    }

    /// <summary>Installs the most recently kept jar of a package again (the last working code before a repair).</summary>
    public async Task<JsonObject> RollbackAsync(string packageName, CancellationToken cancellationToken = default)
    {
        RequirePackage(packageName);
        var (sha, record, jar) = LatestKept(packageName) ?? throw new DomainRepairException(404, "nothing_kept", "No working jar of " + packageName + " has been kept.");
        var report = new JsonObject { ["kind"] = "domain-repair-rollback", ["package"] = packageName, ["started_at"] = Now(), ["kept"] = record.DeepClone() };
        if (!_changing.TryAdd(packageName, 0))
            throw new DomainRepairException(409, "busy", "A repair or rollback of " + packageName + " is already running.");
        try
        {
            await _adminLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _changing.TryRemove(packageName, out _);
            throw;
        }
        _keptVersions.TryRemove(packageName, out _);
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
        finally
        {
            _adminLock.Release();
            _changing.TryRemove(packageName, out _);
        }
        report["finished_at"] = Now();
        SaveReport(packageName, report);
        return report;
    }

    /// <summary>
    /// REP-1: keeps the installed jar of a package as its latest confirmed working version, after the package passed a
    /// source health check (SRC-7). At most once per jar digest: a jar already kept is not written again, and while the
    /// installed version is unchanged since the last keep the jar is not even read. Answers the kept record, or null when
    /// nothing was kept (not installed, does not load, or already kept for this version).
    /// </summary>
    public async Task<JsonObject?> KeepLastWorkingAsync(string packageName, CancellationToken cancellationToken = default)
    {
        RequirePackage(packageName);
        // A repair in progress may have an unverified jar installed; that is never kept as working.
        if (_changing.ContainsKey(packageName)) return null;
        await _adminLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_changing.ContainsKey(packageName)) return null;
            var extension = await _host.ExtensionAsync(packageName, cancellationToken).ConfigureAwait(false);
            if (extension is null || !(SourceResponseContract.Boolean(extension["isInstalled"], out var installed) && installed)) return null;
            if (SourceResponseContract.Text(extension["loadError"]).Length > 0) return null;
            var version = SourceResponseContract.Text(extension["versionName"]) + "|" + SourceResponseContract.Text(extension["versionCodeLong"]);
            if (_keptVersions.TryGetValue(packageName, out var known) && known == version) return null;
            var jar = await _host.ReadJarAsync(packageName, cancellationToken).ConfigureAwait(false);
            var sha = DomainMigration.Sha(jar);
            var directory = LastWorkingDirectory(packageName);
            JsonObject? record = null;
            if (File.Exists(Path.Combine(directory, sha + ".jar")))
                record = Records(directory).Where(item => item.Sha == sha).Select(item => item.Record).FirstOrDefault();
            var kept = record is null;
            record ??= Keep(packageName, jar, extension, "passed-health-check");
            _keptVersions[packageName] = version;
            return kept ? record : null;
        }
        finally { _adminLock.Release(); }
    }

    /// <summary>
    /// Fire-and-forget form of <see cref="KeepLastWorkingAsync"/> for the health check's completion: one keep per package
    /// at a time, three minutes at most, never throws. Errors other than "no source host here" go to
    /// <paramref name="recordError"/>.
    /// </summary>
    public void QueueKeepLastWorking(string packageName, Action<Exception>? recordError = null)
    {
        if (!IsPackageName(packageName) || !_keeping.TryAdd(packageName, 0)) return;
        _ = Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await KeepLastWorkingAsync(packageName, deadline.Token).ConfigureAwait(false); }
            catch (SourceEngineUnavailableException) { }
            catch (Exception error)
            {
                try { recordError?.Invoke(error); }
                catch (Exception) { }
            }
            finally { _keeping.TryRemove(packageName, out _); }
        });
    }

    /// <summary>Installs the kept jar again under the admin lock. Never cancelled: it runs after a cancel too.</summary>
    private async Task<JsonObject> RestoreAsync(string packageName, byte[] kept, string keptSha, JsonObject report, string reason,
        Func<string, string, JsonObject> finish)
    {
        report["failure"] = reason;
        try
        {
            // The host's own failure path already reloads the previous jar; installing the kept bytes again makes the
            // outcome explicit and also covers a repaired jar that loaded but does not work.
            var answer = await UnderAdminLockAsync(() => _host.InstallJarAsync(packageName, kept, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
            var loadError = SourceResponseContract.Text(answer["loadError"]);
            report["rollback"] = new JsonObject { ["sha256"] = keptSha, ["load_error"] = loadError.Length == 0 ? null : loadError };
            if (loadError.Length > 0) return finish("rollback_failed", reason + " The kept jar was installed again but does not load: " + loadError);
            return finish("rolled_back", reason + " The last working jar is installed again.");
        }
        catch (Exception error)
        {
            report["rollback"] = new JsonObject { ["sha256"] = keptSha, ["error"] = error.Message };
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
