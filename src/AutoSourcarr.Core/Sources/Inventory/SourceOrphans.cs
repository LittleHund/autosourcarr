using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Core.Sources.Inventory;

/// <summary>An orphan request the API answers with this HTTP status and message (0.4.x HTTPException).</summary>
public sealed class SourceOrphanException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Port of source_orphans.py: bounded discovery and explicit installation of original Keiyoushi archives, plus the
/// manual orphan labels of installed sources. No source website is visited during discovery. A pinned repository
/// manifest (and release digest where present) is the authority; forks/mirrors are never substituted. An installed
/// archive remains unverified until a repair validates it. State lives in &lt;maintenance&gt;/orphans.json, the same
/// file <see cref="InstalledSourceAnnotations"/> reads; without it the bundled seed list is the starting point.
/// </summary>
public sealed class SourceOrphans
{
    public const string Repository = "https://github.com/keiyoushi/extensions";
    public const string Raw = "https://raw.githubusercontent.com/keiyoushi/extensions/";
    public const string Api = "https://api.github.com/repos/keiyoushi/extensions/";
    public const int MaximumIndex = 8 * 1024 * 1024;
    public const int MaximumPackage = 32 * 1024 * 1024;
    public const int MaximumState = 16 * 1024 * 1024;
    public const string Notice = "Original Keiyoushi snapshots are sampled over two years; this is not the complete history. "
        + "Missing from today’s catalog does not prove a website is retired. "
        + "Archived installation does not prove the source works. Repair checks metadata, chapters and page images; "
        + "CAPTCHA, login and rate limits require attention rather than repeated code repairs.";
    public static IReadOnlyList<int> SnapshotDays { get; } = [0, 7, 30, 60, 90, 180, 365, 730];
    private static readonly HashSet<string> GithubHosts = new(StringComparer.OrdinalIgnoreCase)
        { "api.github.com", "raw.githubusercontent.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" };
    private static readonly Regex Pkg = new(@"\Aeu\.kanade\.tachiyomi\.extension\.[a-z0-9]+\.[a-z0-9_]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex Commit = new(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex SourceId = new(@"\A-?[0-9]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex RawPath = new(@"\A/keiyoushi/extensions/(?:repo|[0-9a-f]{40})/((?:apk|jar)/[A-Za-z0-9_.+\-]+\.(?:apk|jar))\z", RegexOptions.CultureInvariant);
    private static readonly Regex ReleasePath = new(@"\A/keiyoushi/extensions/releases/download/[A-Za-z0-9_.\-]+/[A-Za-z0-9_.+\-]+\.(?:apk|jar)\z", RegexOptions.CultureInvariant);
    private static readonly Regex GithubRawPath = new(@"\A/keiyoushi/extensions/raw/(?:repo|[0-9a-f]{40})/((?:apk|jar)/[A-Za-z0-9_.+\-]+\.(?:apk|jar))\z", RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _root;
    private readonly SuwayomiClient _client;
    private readonly IPublicSourceFetcher _fetcher;
    private readonly Func<byte[]?> _seed;
    private readonly TimeProvider _clock;
    private readonly Func<string, string> _scrub;
    private readonly object _gate = new();
    private bool _loaded;
    private JsonObject _records = new();
    private HashSet<string> _current = new(StringComparer.Ordinal);
    private JsonObject _labels = new();
    private JsonObject _refresh = DefaultRefresh();
    private JsonObject _operation = Idle();
    private List<JsonObject> _inventory = [];
    private bool _inventoryKnown;
    private long? _inventoryAt;
    private Task? _inventoryTask;
    private long _inventoryRevision;
    private bool _discovering;
    private bool _installing;

    /// <param name="maintenanceRoot">settings data directory/source-maintenance (the 0.4.x folder).</param>
    /// <param name="client">The source engine: inventory reads and the archive installation.</param>
    /// <param name="fetcher">Public-DNS-pinned HTTPS reads of the original repository (GitHub hosts only).</param>
    /// <param name="seed">The bundled seed list (source_orphans_seed.json), used while orphans.json does not exist.</param>
    /// <param name="scrub">Removes secrets from messages that are saved or shown (SupportPrivacy.Text).</param>
    public SourceOrphans(string maintenanceRoot, SuwayomiClient client, IPublicSourceFetcher fetcher, Func<byte[]?>? seed = null,
        TimeProvider? clock = null, Func<string, string>? scrub = null)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(fetcher);
        _root = Path.GetFullPath(maintenanceRoot); _client = client; _fetcher = fetcher;
        _seed = seed ?? (() => null); _clock = clock ?? TimeProvider.System; _scrub = scrub ?? (text => text);
    }

    public string StatePath => Path.Combine(_root, "orphans.json");

    // ---------------------------------------------------------------- state

    /// <summary>Validates the bundled seed list: records (by package), current packages, optional labels/refresh/operation.</summary>
    public static JsonObject ParseSeed(byte[] bytes)
    {
        JsonObject root;
        try { root = JsonNode.Parse(StrictUtf8.GetString(bytes), documentOptions: new() { MaxDepth = 64 }) as JsonObject ?? throw new InvalidDataException("The orphan seed list is not an object"); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException) { throw new InvalidDataException("The orphan seed list is not valid UTF-8 JSON", error); }
        Validate(root);
        return root;
    }

    private static void Validate(JsonObject root)
    {
        if (root["records"] is not null and not JsonObject || root["labels"] is not null and not JsonObject || root["current"] is not null and not JsonArray
            || root["refresh"] is not null and not JsonObject || root["operation"] is not null and not JsonObject)
            throw new InvalidDataException("Orphan source state is damaged");
        foreach (var (package, row) in root["records"] as JsonObject ?? new JsonObject())
            if (row is not JsonObject record || !SourceInventoryFiles.IsApkPackage(package) || Text(record["pkgName"]) != package)
                throw new InvalidDataException("Orphan source record is damaged");
        foreach (var (package, row) in root["labels"] as JsonObject ?? new JsonObject())
            if (row is not JsonObject label || !SourceInventoryFiles.IsApkPackage(package) || label["orphaned"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Orphan source label is damaged");
        foreach (var item in root["current"] as JsonArray ?? [])
            if (item?.GetValueKind() != JsonValueKind.String) throw new InvalidDataException("Orphan current package list is damaged");
    }

    /// <summary>Loads orphans.json (or the seed) once. Callers hold _gate.</summary>
    private void EnsureLoaded()
    {
        if (_loaded) return;
        JsonObject? saved = null;
        if (File.Exists(StatePath) || new FileInfo(StatePath).LinkTarget is not null)
        {
            saved = SourceInventoryFiles.ReadObject(StatePath, MaximumState);
            Validate(saved);
        }
        else if (_seed() is { } seed) saved = ParseSeed(seed);
        _records = new(); _current = new(StringComparer.Ordinal); _labels = new(); _refresh = DefaultRefresh(); _operation = Idle();
        if (saved is not null)
        {
            _records = (JsonObject?)saved["records"]?.DeepClone() ?? new();
            foreach (var item in saved["current"] as JsonArray ?? []) _current.Add(item!.GetValue<string>());
            _labels = (JsonObject?)saved["labels"]?.DeepClone() ?? new();
            foreach (var (key, value) in saved["refresh"] as JsonObject ?? new JsonObject()) _refresh[key] = value?.DeepClone();
            if (Text(_refresh["state"]) == "running") { _refresh["state"] = "interrupted"; _refresh["message"] = "Previous discovery stopped; retained results remain available"; }
            _operation = (JsonObject?)saved["operation"]?.DeepClone() ?? Idle();
            if (Text(_operation["state"]) is "queued" or "running" or "installing")
            { _operation["state"] = "interrupted"; _operation["message"] = "Previous source operation was interrupted. Refresh installed status before trying again."; }
        }
        RecoverUploads();
        _loaded = true;
    }

    /// <summary>Older successful local uploads already have durable receipts: recover their classification only.</summary>
    private void RecoverUploads()
    {
        var uploads = new DirectoryInfo(Path.Combine(_root, "uploads"));
        if (!uploads.Exists || uploads.LinkTarget is not null || (uploads.Attributes & FileAttributes.ReparsePoint) != 0) return;
        var seen = 0;
        foreach (var directory in uploads.EnumerateDirectories())
        {
            if (++seen > 512) break;
            try
            {
                if (directory.LinkTarget is not null || (directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var path = Path.Combine(directory.FullName, "receipt.json"); var info = new FileInfo(path);
                if (!info.Exists || info.LinkTarget is not null || info.Length > 65536) continue;
                var receipt = SourceInventoryFiles.ReadObject(path, 65536); var package = Text(receipt["pkgName"]);
                if (Text(receipt["state"]) == "installed" && SourceInventoryFiles.IsApkPackage(package) && !_labels.ContainsKey(package))
                    _labels[package] = new JsonObject { ["orphaned"] = true, ["reason"] = "manual-upload", ["name"] = package, ["marked_at"] = receipt["completed_at"]?.DeepClone() };
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
    }

    private void Save()
    {
        var value = new JsonObject
        {
            ["records"] = _records.DeepClone(),
            ["current"] = new JsonArray(_current.Order(StringComparer.Ordinal).Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            ["labels"] = _labels.DeepClone(), ["refresh"] = _refresh.DeepClone(), ["operation"] = _operation.DeepClone()
        };
        AtomicSourceFiles.Write(StatePath, Encoding.UTF8.GetBytes(value.ToJsonString()), MaximumState);
    }

    /// <summary>Reads (and on first use creates the in-memory copy of) the state; throws when orphans.json is damaged.</summary>
    public void Load() { lock (_gate) EnsureLoaded(); }

    public bool HasRecord(string package) { lock (_gate) { EnsureLoaded(); return _records.ContainsKey(package); } }

    public JsonObject Records() { lock (_gate) { EnsureLoaded(); return (JsonObject)_records.DeepClone(); } }

    public JsonObject Labels() { lock (_gate) { EnsureLoaded(); return (JsonObject)_labels.DeepClone(); } }

    // ---------------------------------------------------------------- classification

    /// <summary>
    /// Classifies the local inventory only: never fetches repositories or alters provenance. Adds is_orphaned,
    /// orphan_reason, can_mark_orphan, can_unmark_orphan and repository_missing. <paramref name="configured"/> is the
    /// configured repository index list; null means unknown (unknown configuration is not proof of absence).
    /// </summary>
    public IReadOnlyList<JsonObject> Annotate(IEnumerable<JsonObject> extensions, IReadOnlyCollection<string>? configured)
    {
        lock (_gate) { EnsureLoaded(); return extensions.Select(item => AnnotateOne(item, configured)).ToList(); }
    }

    private JsonObject AnnotateOne(JsonObject item, IReadOnlyCollection<string>? configured)
    {
        var row = (JsonObject)item.DeepClone();
        var package = Text(row["pkgName"]); var installed = Truthy(row["isInstalled"]);
        var label = _labels[package] as JsonObject;
        var store = Text(row["storeIndexUrl"]);
        var localOnly = installed && row.ContainsKey("storeIndexUrl") && row.ContainsKey("apkUrl") && !Truthy(row["storeIndexUrl"]) && !Truthy(row["apkUrl"]);
        var missing = IsTrue(row["isObsolete"]) || localOnly || configured is not null && store.Length > 0 && !configured.Contains(store);
        var archived = _records.ContainsKey(package) && !_current.Contains(package);
        var automatic = localOnly || installed && Truthy(row["isObsolete"]) || archived;
        var orphaned = label is not null && label.ContainsKey("orphaned") ? IsTrue(label["orphaned"]) : automatic;
        // An uninstalled package advertised by a configured repository keeps the ordinary Install path.
        var listed = Truthy(row["apkUrl"]) && !Truthy(row["isObsolete"]) && store.Length > 0 && (configured is null || configured.Contains(store));
        if (!installed && listed) orphaned = false;
        var reason = IsTrue(label?["orphaned"]) ? Text(label!["reason"]) : localOnly ? "manual-upload" : archived ? "archived" : "catalog-missing";
        var valid = SourceInventoryFiles.IsApkPackage(package);
        row["is_orphaned"] = orphaned;
        row["orphan_reason"] = orphaned ? reason : "";
        row["can_mark_orphan"] = installed && !orphaned && missing && valid;
        row["can_unmark_orphan"] = installed && orphaned && valid;
        row["repository_missing"] = missing;
        return row;
    }

    /// <summary>
    /// SRC-8 "official replacement before repair": why a configured repository now offers a maintained version of an
    /// installed package, or null. Uses the same listing rule as <see cref="Annotate"/> (an apkUrl, not obsolete, from a
    /// configured repository index; unknown configuration counts as configured) and needs a newer version there
    /// (hasUpdate): a repository that lists the very version that broke offers nothing to replace it with, so the
    /// repair may go ahead. A package that is not installed has nothing to repair and answers null.
    /// </summary>
    public static string? MaintainedVersion(IEnumerable<JsonObject> rows, string package, IReadOnlyCollection<string>? configured)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            if (Text(row["pkgName"]) != package || !Truthy(row["isInstalled"])) continue;
            var store = Text(row["storeIndexUrl"]);
            var listed = Truthy(row["apkUrl"]) && !Truthy(row["isObsolete"]) && store.Length > 0 && (configured is null || configured.Contains(store));
            if (!listed || !IsTrue(row["hasUpdate"])) continue;
            return "a newer version than the installed " + (Truthy(row["versionName"]) ? Str(row["versionName"]) : "one") + " is listed by " + store;
        }
        return null;
    }

    /// <summary>A full inventory read (installed and available extensions) becomes the cached list.</summary>
    public void AcceptInventory(IEnumerable<JsonObject> rows)
    {
        lock (_gate)
        {
            _inventoryRevision++; _inventory = rows.Select(row => (JsonObject)row.DeepClone()).ToList();
            _inventoryKnown = true; _inventoryAt = _clock.GetTimestamp();
        }
    }

    /// <summary>One changed package; a one-package result is not a complete inventory, so the next snapshot re-reads.</summary>
    public void UpdateInventoryPackage(string package, JsonObject? extension)
    {
        lock (_gate)
        {
            _inventoryRevision++;
            _inventory = _inventory.Where(row => Text(row["pkgName"]) != package).ToList();
            if (extension is not null) _inventory.Add((JsonObject)extension.DeepClone());
            _inventoryAt = null;
        }
    }

    /// <summary>Reads the engine inventory (5 seconds at most) into the cache; a failed read marks it unknown.</summary>
    public async Task RefreshInventoryAsync(CancellationToken cancellationToken = default)
    {
        long revision; lock (_gate) revision = _inventoryRevision;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var rows = await ReadInventoryAsync(deadline.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (revision != _inventoryRevision) return;
                _inventoryRevision++; _inventory = rows.ToList(); _inventoryKnown = true;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_gate) if (revision == _inventoryRevision) _inventoryKnown = false;
        }
        finally { lock (_gate) _inventoryAt = _clock.GetTimestamp(); }
    }

    /// <summary>Every extension the engine lists (installed and available), in the inventory query's fields.</summary>
    public async Task<IReadOnlyList<JsonObject>> ReadInventoryAsync(CancellationToken cancellationToken = default)
    {
        var data = await _client.GraphQlAsync(EngineInstalledSources.InventoryQuery, null, cancellationToken).ConfigureAwait(false);
        return SourceResponseContract.ConnectionNodes(data["extensions"]);
    }

    /// <summary>
    /// The orphan page: every orphan once in items (sorted by name), and SRC-3's two headers in groups, Installed and
    /// Not installed, each with its own orphan list. Starts a background inventory read when the cache is older than 20 s.
    /// <paramref name="repairAvailable"/> is whether the in-app domain repair runs here: then every installed orphan can
    /// be repaired (can_repair); a not-installed one must be installed first. worker_configured keeps its name for the
    /// UI and carries the same truth (the in-app repair is the worker), repair_available says it plainly.
    /// </summary>
    public JsonObject Snapshot(bool repairAvailable, IReadOnlyCollection<string>? configured, JsonObject? maintenanceOperation = null)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if ((_inventoryAt is null || _clock.GetElapsedTime(_inventoryAt.Value) > TimeSpan.FromSeconds(20)) && (_inventoryTask is null || _inventoryTask.IsCompleted))
            {
                _inventoryAt = _clock.GetTimestamp();
                _inventoryTask = Task.Run(() => RefreshInventoryAsync());
            }
            var annotated = _inventory.Select(item => AnnotateOne(item, configured)).ToList();
            var installed = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var live in annotated.Where(entry => Truthy(entry["isInstalled"]))) installed[Text(live["pkgName"])] = live;
            var available = annotated.Where(entry => !Truthy(entry["isInstalled"]) && !IsTrue(entry["is_orphaned"])).Select(entry => Text(entry["pkgName"])).ToHashSet(StringComparer.Ordinal);
            // Local/manual labels do not manufacture an original archive, commit or working backup.
            var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var (package, node) in _labels)
                if (node is JsonObject label && IsTrue(label["orphaned"]))
                {
                    var row = new JsonObject { ["pkgName"] = package };
                    foreach (var (key, value) in label) row[key] = value?.DeepClone();
                    row["origin"] = "Local package"; row["message"] = "Not installed. Upload its APK again to reinstall.";
                    rows[package] = row;
                }
            foreach (var (package, node) in _records) if (node is JsonObject record) rows[package] = (JsonObject)record.DeepClone();
            var operationPackage = Text(maintenanceOperation?["extension"] is JsonObject extension ? extension["pkgName"] : null);
            var items = new List<JsonObject>();
            foreach (var (package, row) in rows)
            {
                var isInstalled = installed.ContainsKey(package);
                var label = _labels[package] as JsonObject;
                if (available.Contains(package)) continue;
                if (label?["orphaned"]?.GetValueKind() == JsonValueKind.False) continue;
                if (isInstalled && !IsTrue(installed[package]["is_orphaned"])) continue;
                if (!isInstalled && _current.Contains(package) && !IsTrue(label?["orphaned"])) continue;
                var state = row.ContainsKey("state") ? Text(row["state"]) : "available";
                if (isInstalled && state == "available") state = "installed-unverified";
                if (!isInstalled) state = Truthy(row["archive_url"]) && row["archive_available"]?.GetValueKind() != JsonValueKind.False ? "available" : "unavailable";
                if (isInstalled && Text(maintenanceOperation?["state"]) == "installed" && operationPackage == package) state = "repaired";
                var live = installed.GetValueOrDefault(package) ?? new JsonObject();
                var message = row.ContainsKey("message") ? row["message"]?.DeepClone() : JsonValue.Create("Original archived package; functionality has not been verified.");
                if (isInstalled && Text(live["orphan_reason"]) is "manual" or "manual-upload")
                    message = JsonValue.Create((Text(live["orphan_reason"]) == "manual-upload" ? "Manually added source. " : "Marked as orphaned. ") + "Its installed package and source settings are unchanged.");
                var item = new JsonObject();
                foreach (var (key, value) in row) if (key != "sources") item[key] = value?.DeepClone();
                foreach (var key in new[] { "name", "lang", "versionName", "hasUpdate", "isObsolete", "orphan_reason", "can_unmark_orphan" })
                    if (live.ContainsKey(key)) item[key] = live[key]?.DeepClone();
                item["installed"] = isInstalled;
                item["state"] = state;
                item["can_install"] = Truthy(row["archive_url"]) && row["archive_available"]?.GetValueKind() != JsonValueKind.False && !isInstalled && _inventoryKnown;
                item["can_repair"] = repairAvailable && isInstalled && AutoSourcarr.Core.Repair.Migration.DomainRepair.IsPackageName(package);
                item["message"] = message;
                items.Add(item);
            }
            // Installed orphans remain visible even if older than the sampled history.
            foreach (var (package, item) in installed)
            {
                if (rows.ContainsKey(package) || !IsTrue(item["is_orphaned"])) continue;
                var manual = Text(item["orphan_reason"]) == "manual-upload";
                items.Add(new JsonObject
                {
                    ["pkgName"] = package, ["name"] = item.ContainsKey("name") ? item["name"]?.DeepClone() : package,
                    ["lang"] = item.ContainsKey("lang") ? item["lang"]?.DeepClone() : "", ["versionName"] = item.ContainsKey("versionName") ? item["versionName"]?.DeepClone() : "",
                    ["orphan_reason"] = item["orphan_reason"]?.DeepClone(), ["can_unmark_orphan"] = item["can_unmark_orphan"]?.DeepClone(),
                    ["hasUpdate"] = item.ContainsKey("hasUpdate") ? item["hasUpdate"]?.DeepClone() : false,
                    ["isObsolete"] = item.ContainsKey("isObsolete") ? item["isObsolete"]?.DeepClone() : false,
                    ["installed"] = true, ["state"] = "installed-unverified", ["origin"] = "Installed package; archive provenance not yet located",
                    ["can_install"] = false, ["can_repair"] = repairAvailable && AutoSourcarr.Core.Repair.Migration.DomainRepair.IsPackageName(package),
                    ["message"] = manual ? "Manually added source; no repository download is recorded."
                        : "Missing from the configured catalog. Refresh archives to locate the original history."
                });
            }
            items.Sort((left, right) => string.Compare(SortName(left), SortName(right), StringComparison.Ordinal));
            JsonObject Group(string key, string title, bool wanted)
            {
                var members = items.Where(entry => IsTrue(entry["installed"]) == wanted).Select(entry => (JsonNode?)entry.DeepClone()).ToArray();
                return new JsonObject { ["key"] = key, ["title"] = title, ["count"] = members.Length, ["items"] = new JsonArray(members) };
            }
            return new JsonObject
            {
                ["items"] = new JsonArray(items.Select(entry => (JsonNode?)entry).ToArray()),
                ["groups"] = new JsonArray(Group("installed", "Installed", true), Group("not-installed", "Not installed", false)),
                ["refresh"] = _refresh.DeepClone(), ["operation"] = _operation.DeepClone(), ["worker_configured"] = repairAvailable,
                ["repair_available"] = repairAvailable,
                ["inventory_known"] = _inventoryKnown, ["inventory_refreshing"] = _inventoryTask is { IsCompleted: false }, ["notice"] = Notice
            };
        }
    }

    private static string SortName(JsonObject item) => (item["name"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : "").ToUpperInvariant().ToLowerInvariant();

    /// <summary>Mark (or return to Installed) one installed source. Holds the shared source admin lock like 0.4.x.</summary>
    public async Task<JsonObject> SetOrphanedAsync(string package, bool orphaned, SemaphoreSlim adminLock, IReadOnlyCollection<string>? configured, CancellationToken cancellationToken = default)
    {
        if (!SourceInventoryFiles.IsApkPackage(package)) throw new SourceOrphanException(400, "Unsupported extension package name");
        await adminLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<JsonObject> rows;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                rows = await ReadInventoryAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            { throw new SourceOrphanException(503, "Source inventory is unavailable. No orphan label was changed."); }
            var extension = rows.FirstOrDefault(row => Text(row["pkgName"]) == package && Truthy(row["isInstalled"]))
                ?? throw new SourceOrphanException(404, "This source extension is not installed");
            lock (_gate)
            {
                EnsureLoaded();
                var annotated = AnnotateOne(extension, configured);
                if (orphaned && !(IsTrue(annotated["can_mark_orphan"]) || IsTrue(annotated["is_orphaned"])))
                    throw new SourceOrphanException(409, "This source is not known to be missing from the configured repositories. Refresh the catalog before marking it as orphaned.");
                var previous = _labels[package]?.DeepClone();
                _labels[package] = new JsonObject
                {
                    ["orphaned"] = orphaned, ["reason"] = "manual", ["name"] = Truthy(extension["name"]) ? Str(extension["name"]) : package,
                    ["lang"] = Str(extension["lang"]), ["versionName"] = Str(extension["versionName"]), ["marked_at"] = Now()
                };
                try { Save(); }
                catch
                {
                    if (previous is null) _labels.Remove(package); else _labels[package] = previous;
                    throw;
                }
                _inventoryRevision++; _inventory = rows.Select(row => (JsonObject)row.DeepClone()).ToList(); _inventoryKnown = true; _inventoryAt = _clock.GetTimestamp();
                return new JsonObject
                {
                    ["extension"] = AnnotateOne(extension, configured),
                    ["message"] = orphaned ? "Source marked as orphaned." : "Source returned to Installed."
                };
            }
        }
        finally { adminLock.Release(); }
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>
    /// Admits one discovery: 202 with the queued state, the running state when one already runs, or 429 within a
    /// minute of the previous request. The caller then runs <see cref="DiscoverAsync"/> in the background.
    /// </summary>
    public (bool Start, JsonObject Answer) BeginDiscovery()
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_discovering) return (false, new JsonObject { ["queued"] = true, ["refresh"] = _refresh.DeepClone() });
            if (_refresh["requested_at"] is JsonValue requested && requested.TryGetValue<double>(out var at) && Now() - at < 60)
                throw new SourceOrphanException(429, "Wait one minute between repository history refreshes");
            _refresh["state"] = "running"; _refresh["requested_at"] = Now(); _refresh["message"] = "Discovery queued";
            _discovering = true;
            return (true, new JsonObject { ["queued"] = true, ["refresh"] = _refresh.DeepClone() });
        }
    }

    /// <summary>Releases an admitted discovery that never started (the queue refused it).</summary>
    public void AbandonDiscovery(string message)
    {
        lock (_gate) { _discovering = false; _refresh["state"] = "failed"; _refresh["message"] = message; }
    }

    /// <summary>
    /// Reads sampled original snapshots (bounded to 4 minutes) and records the packages that left the catalog.
    /// Never throws for repository errors: the result is the saved refresh state (complete, partial or failed).
    /// </summary>
    public async Task<JsonObject> DiscoverAsync(Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, JsonObject>(StringComparer.Ordinal); HashSet<string>? active = null;
        var seen = new HashSet<string>(StringComparer.Ordinal); var failures = 0;
        try
        {
            lock (_gate)
            {
                EnsureLoaded(); _discovering = true;
                _refresh["state"] = "running"; _refresh["message"] = "Reading original Keiyoushi snapshots"; _refresh["snapshots"] = 0; _refresh["requests"] = 0;
                Save();
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(240));
            var token = deadline.Token;
            async Task<byte[]?> RequestAsync(string url, bool optional = false)
            {
                lock (_gate) _refresh["requests"] = Integer(_refresh["requests"]) + 1;
                return await FetchAsync(url, MaximumIndex, optional, token).ConfigureAwait(false);
            }
            foreach (var days in SnapshotDays)
            {
                var stamp = (_clock.GetUtcNow() - TimeSpan.FromDays(days)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                var commits = ParseArray((await RequestAsync(Api + "commits?sha=repo&per_page=1&until=" + stamp).ConfigureAwait(false))!);
                if (commits.Count == 0) continue;
                var commit = Text(Get(commits[0], "sha"));
                if (!Commit.IsMatch(commit)) throw new InvalidDataException("GitHub returned an invalid commit");
                var date = Text(Get(Get(Get(commits[0], "commit"), "committer"), "date")); if (date.Length == 0) date = stamp;
                if (!seen.Add(commit)) continue;
                var raw = await RequestAsync(Raw + commit + "/index.pb", optional: true).ConfigureAwait(false);
                var modern = raw is not null;
                raw ??= await RequestAsync(Raw + commit + "/index.min.json").ConfigureAwait(false);
                var assets = new JsonObject();
                if (modern && await RequestAsync(Raw + commit + "/release-assets.json", optional: true).ConfigureAwait(false) is { } rawAssets)
                    assets = ParseNode(rawAssets) as JsonObject ?? new JsonObject();
                Dictionary<string, JsonObject> rows;
                try { rows = ParseIndex(raw!, commit, assets); }
                catch (Exception error) when (error is InvalidDataException or JsonException)
                {
                    if (days == 0) throw;
                    failures++; continue;
                }
                string message;
                lock (_gate)
                {
                    var snapshots = Integer(_refresh["snapshots"]) + 1;
                    _refresh["snapshots"] = snapshots; _refresh["oldest_snapshot"] = date;
                    if (snapshots == 1) _refresh["newest_snapshot"] = date;
                    if (active is null) active = rows.Keys.ToHashSet(StringComparer.Ordinal);
                    else
                        foreach (var (package, row) in rows)
                            if (!active.Contains(package) && !found.ContainsKey(package))
                            {
                                var copy = (JsonObject)row.DeepClone(); copy["snapshot_date"] = date; copy["sampled_cutoff"] = stamp; copy["state"] = "available";
                                found[package] = copy;
                            }
                    message = $"Read {snapshots} original snapshots; found {found.Count} missing packages";
                    _refresh["message"] = message;
                }
                progress?.Invoke(message);
                await Task.Delay(TimeSpan.FromMilliseconds(150), token).ConfigureAwait(false);
            }
            if (active is null) throw new InvalidDataException("No current official catalog could be read");
            lock (_gate)
            {
                MergeFound(found); _current = active;
                _refresh["state"] = "complete"; _refresh["checked_at"] = Now();
                _refresh["message"] = $"Read {Integer(_refresh["snapshots"])} snapshots; {found.Count} archived packages found"
                    + (failures > 0 ? $"; {failures} incomplete snapshots skipped" : "");
                Save();
                return (JsonObject)_refresh.DeepClone();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                _refresh["state"] = "interrupted"; _refresh["message"] = "Discovery stopped; previously saved results retained";
                TrySave();
            }
            throw;
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                // Successfully read historical snapshots remain useful if a later rate limit interrupts this bounded scan.
                if (active is not null) { _current = active; MergeFound(found); }
                var detail = error is OperationCanceledException ? "Discovery reached its four-minute limit" : _scrub(error.Message);
                _refresh["state"] = found.Count > 0 ? "partial" : "failed";
                _refresh["message"] = "Archive discovery paused: " + detail;
                TrySave();
                return (JsonObject)_refresh.DeepClone();
            }
        }
        finally { lock (_gate) _discovering = false; }
    }

    private void MergeFound(Dictionary<string, JsonObject> found)
    {
        foreach (var (package, discovered) in found)
        {
            var existing = _records[package] as JsonObject ?? new JsonObject();
            if (Truthy(existing["installed_at"])) continue;
            var row = (JsonObject)discovered.DeepClone();
            if (Text(existing["archive_url"]) == Text(row["archive_url"]) && Text(existing["sha256"]) == Text(row["sha256"]))
                foreach (var key in new[] { "archive_available", "archive_verified_at", "message", "repair_job_id" })
                    if (existing.ContainsKey(key)) row[key] = existing[key]?.DeepClone();
            _records[package] = row;
        }
    }

    private async Task CheckCurrentAsync(string package, CancellationToken token)
    {
        var commits = ParseArray((await FetchAsync(Api + "commits?sha=repo&per_page=1", MaximumIndex, false, token).ConfigureAwait(false))!);
        var head = commits.Count > 0 ? Text(Get(commits[0], "sha")) : "";
        if (!Commit.IsMatch(head)) throw new InvalidDataException("Current original catalog could not be confirmed");
        var index = await FetchAsync(Raw + head + "/index.pb", MaximumIndex, true, token).ConfigureAwait(false)
            ?? await FetchAsync(Raw + head + "/index.min.json", MaximumIndex, false, token).ConfigureAwait(false);
        var active = ParseIndex(index!, head);
        lock (_gate) { _current = active.Keys.ToHashSet(StringComparer.Ordinal); Save(); }
        if (active.ContainsKey(package)) throw new InvalidDataException("The original maintained package is available again; use Manage sources instead of its archive");
    }

    // ---------------------------------------------------------------- installation

    /// <summary>Admits one archive installation (404 unknown archive, 409 while another runs) and marks it queued.</summary>
    public void BeginInstall(string package)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_records.ContainsKey(package)) throw new SourceOrphanException(404, "Archived source not found; refresh original history first");
            if (_installing) throw new SourceOrphanException(409, "An archive installation is running");
            _installing = true;
            _operation = new JsonObject { ["state"] = "queued", ["pkgName"] = package, ["message"] = "Archive installation queued" };
        }
    }

    /// <summary>Releases an admitted installation that never started (the queue refused it).</summary>
    public void AbandonInstall(string message)
    {
        lock (_gate) { _installing = false; _operation["state"] = "failed"; _operation["message"] = message; }
    }

    /// <summary>
    /// Downloads the original archive, checks its release digest and package identity, keeps a copy under
    /// archives/&lt;sha256&gt;/ and installs it into the source engine under the shared admin lock. Never replaces an
    /// installed source and never replays an uncertain installation. Returns the saved operation state.
    /// </summary>
    public async Task<JsonObject> InstallAsync(string package, SemaphoreSlim adminLock, CancellationToken cancellationToken = default)
    {
        try
        {
            await adminLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_gate) EnsureLoaded();
                var before = await ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
                if (before.Any(row => Text(row["pkgName"]) == package && Truthy(row["isInstalled"])))
                    throw new InvalidDataException("Source is already installed; an archive will not overwrite it");
                JsonObject row; string url;
                lock (_gate)
                {
                    row = (JsonObject?)_records[package]?.DeepClone() ?? throw new InvalidDataException("Archived source not found; refresh original history first");
                    if (_current.Contains(package)) throw new InvalidDataException("A maintained package is available; install it through Manage sources");
                    url = PackageUrl(Text(row["archive_url"]), Text(row["commit"]));
                    if (url.Length == 0 || url != Text(row["archive_url"])) throw new InvalidDataException("No verified original download is available");
                    _operation = new JsonObject { ["state"] = "installing", ["pkgName"] = package, ["message"] = "Downloading the original archived package" };
                    Save();
                }
                var suffix = url.EndsWith(".jar", StringComparison.Ordinal) ? ".jar" : ".apk";
                if (_client.SourceHost && suffix == ".jar")
                    throw new InvalidDataException("The source host installs APK archives only, and this original archive is a JAR. Rebuild it with Build and check archive once source repairs are available.");
                byte[] data;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(90));
                    await CheckCurrentAsync(package, deadline.Token).ConfigureAwait(false);
                    data = (await FetchAsync(url, MaximumPackage, false, deadline.Token, TimeSpan.FromSeconds(60)).ConfigureAwait(false))!;
                }
                var digest = SourceInventoryFiles.Digest(data);
                var expected = Text(row["sha256"]);
                if (expected.Length > 0 && digest != expected) throw new InvalidDataException("Archived download does not match its original release SHA256");
                ValidatePackage(data, package);
                AtomicSourceFiles.Write(Path.Combine(_root, "archives", digest, "original" + suffix), data, MaximumPackage, immutable: true);
                // Retain recovery provenance before mutation; an uncertain response is never replayed automatically.
                lock (_gate)
                {
                    _operation["message"] = "Installing original archive; functionality is not yet verified";
                    if (_records[package] is JsonObject record) { record["download_sha256"] = digest; record["archive_saved"] = true; }
                    Save();
                }
                await _client.InstallExtensionFileAsync(package + suffix, data, suffix == ".apk", cancellationToken).ConfigureAwait(false);
                var after = await ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
                var current = after.FirstOrDefault(item => Text(item["pkgName"]) == package && Truthy(item["isInstalled"]))
                    ?? throw new InvalidDataException("Source engine did not confirm installation; refresh installed sources before retrying");
                var expectedIds = Ids(row["sources"]);
                var actualIds = Ids((current["source"] as JsonObject)?["nodes"]);
                var matches = expectedIds.Count == 0 || expectedIds.SequenceEqual(actualIds, StringComparer.Ordinal);
                var message = matches ? "Archived source installed. Metadata, chapters and pages still need functional verification."
                    : "Archived source installed, but source IDs differ from its historical index. Review before using or repairing.";
                lock (_gate)
                {
                    if (_records[package] is JsonObject record)
                    { record["state"] = "installed-unverified"; record["installed_at"] = Now(); record["message"] = message; record["identity_matches"] = matches; }
                    _operation["state"] = "installed"; _operation["message"] = message;
                    Save();
                    _inventoryRevision++; _inventory = after.Select(item => (JsonObject)item.DeepClone()).ToList(); _inventoryKnown = true; _inventoryAt = _clock.GetTimestamp();
                    return (JsonObject)_operation.DeepClone();
                }
            }
            finally { adminLock.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                _operation["state"] = "interrupted";
                _operation["message"] = "Archive installation stopped. Refresh installed sources before trying again.";
                TrySave();
            }
            throw;
        }
        catch (Exception error)
        {
            var message = _scrub(error is OperationCanceledException ? "Archive download reached its time limit" : error.Message);
            if (message.Length == 0) message = error.GetType().Name;
            lock (_gate)
            {
                if (message.Contains("Original repository returned HTTP 404", StringComparison.Ordinal) && _records[package] is JsonObject record)
                {
                    record["archive_available"] = false;
                    record["message"] = "Original archived binary was removed. Use Build and check archive to recover the original source code with the configured repair worker.";
                }
                _operation["state"] = "failed"; _operation["pkgName"] = package; _operation["message"] = message;
                TrySave();
                return (JsonObject)_operation.DeepClone();
            }
        }
        finally { lock (_gate) _installing = false; }
    }

    private static List<string> Ids(JsonNode? sources) => (sources as JsonArray ?? [])
        .OfType<JsonObject>().Select(source => SourceResponseContract.Identity(source["id"]) ?? "").Order(StringComparer.Ordinal).ToList();

    private void TrySave()
    {
        try { Save(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- repository reads

    private async Task<byte[]?> FetchAsync(string url, int maximum, bool optional, CancellationToken token, TimeSpan? timeout = null)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !GithubHosts.Contains(uri.IdnHost)) throw new InvalidDataException("Invalid original repository URL");
        try
        {
            var response = await _fetcher.FetchAsync(new PublicSourceRequest(uri, maximum, timeout ?? TimeSpan.FromSeconds(20)), token).ConfigureAwait(false);
            if (!GithubHosts.Contains(response.ResolvedUrl.IdnHost)) throw new InvalidDataException("Original repository redirected outside supported GitHub hosts");
            return response.Body;
        }
        catch (PublicSourceFetchException error) when (optional && error.StatusCode == 404) { return null; }
        catch (PublicSourceFetchException error) when (error.StatusCode is 403 or 429)
        { throw new InvalidDataException("GitHub rate limit or access restriction; archive discovery paused. Try later."); }
        catch (PublicSourceFetchException error)
        { throw new InvalidDataException("Original repository returned HTTP " + error.StatusCode.ToString(CultureInfo.InvariantCulture) + "; no alternate package was selected"); }
    }

    /// <summary>Only original repository files or releases, never manifest-supplied mirrors.</summary>
    public static string PackageUrl(string value, string commit)
    {
        if (!Commit.IsMatch(commit)) throw new InvalidDataException("Archive requires a complete repository commit");
        string host;
        try { RawCodeRules.PublicReference(value); host = new Uri(value).IdnHost.ToLowerInvariant(); }
        catch (Exception error) when (error is InvalidDataException or UriFormatException or ArgumentException) { return ""; }
        var path = RawCodeRules.OriginalUrlPath(value);
        if (host == "raw.githubusercontent.com" && RawPath.Match(path) is { Success: true } raw) return Raw + commit + "/" + raw.Groups[1].Value;
        if (host == "github.com")
        {
            if (ReleasePath.IsMatch(path)) return value;
            if (GithubRawPath.Match(path) is { Success: true } file) return Raw + commit + "/" + file.Groups[1].Value;
        }
        return "";
    }

    /// <summary>Official index.proto (Resources.jarUrl = 501) or the legacy JSON array; packages by name.</summary>
    public static Dictionary<string, JsonObject> ParseIndex(byte[] raw, string commit, JsonObject? assets = null)
    {
        var data = Expanded(raw); assets ??= new JsonObject();
        var candidates = new List<Candidate>();
        var start = 0; while (start < data.Length && data[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
        if (start < data.Length && data[start] == (byte)'[')
        {
            if (ParseNode(data) is not JsonArray rows || rows.Count > 10000) throw new InvalidDataException("Invalid legacy extension index");
            foreach (var row in rows.OfType<JsonObject>())
                candidates.Add(new(Str(row["name"]), Str(row["pkg"]), Str(row["version"]), Str(row["lang"]), Truthy(row["nsfw"]),
                    row["sources"] as JsonArray ?? [], Raw + commit + "/apk/" + Str(row["apk"]), ""));
        }
        else
        {
            var content = Fields(data).FirstOrDefault(field => field.Number == 101 && field.Bytes is not null).Bytes
                ?? throw new InvalidDataException("Repository index does not contain its extension list");
            foreach (var entry in Fields(content).Where(field => field.Number == 1 && field.Bytes is not null))
            {
                var fields = Fields(entry.Bytes!);
                var resources = Fields(fields.FirstOrDefault(field => field.Number == 3 && field.Bytes is not null).Bytes ?? []);
                var sources = new JsonArray();
                foreach (var encoded in fields.Where(field => field.Number == 8 && field.Bytes is not null))
                {
                    var source = Fields(encoded.Bytes!);
                    var id = source.FirstOrDefault(field => field.Number == 1 && field.Bytes is null).Integer;
                    sources.Add(new JsonObject
                    {
                        ["id"] = unchecked((long)id).ToString(CultureInfo.InvariantCulture), ["name"] = TextField(source, 2),
                        ["lang"] = TextField(source, 3), ["baseUrl"] = TextField(source, 4)
                    });
                }
                candidates.Add(new(TextField(fields, 1), TextField(fields, 2), TextField(fields, 6), "",
                    fields.Any(field => field.Number == 7 && field.Bytes is null && field.Integer is 2 or 3), sources,
                    TextField(resources, 1), TextField(resources, 501)));
            }
        }
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in candidates)
        {
            var package = row.Package;
            if (!Pkg.IsMatch(package) || package.EndsWith(".keiyoushi", StringComparison.Ordinal) || package.EndsWith(".mihon", StringComparison.Ordinal)) continue;
            var sources = row.Sources.OfType<JsonObject>().Where(source => SourceId.IsMatch(Str(source["id"]))).Take(200)
                .Select(source => (JsonObject)source.DeepClone()).ToList();
            var languages = sources.Select(source => Str(source["lang"])).Where(lang => lang.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            string artifact = "", fingerprint = "";
            foreach (var (kind, value) in new[] { ("jar", row.Jar), ("apk", row.Apk) })
            {
                var url = PackageUrl(value, commit);
                if (url.Length == 0) continue;
                var record = Get(Get(assets, package), kind) as JsonObject ?? new JsonObject();
                var checksum = Str(record["sha256"]);
                var sameName = record["name"] is JsonValue name && name.TryGetValue<string>(out var leaf) && leaf == url[(url.LastIndexOf('/') + 1)..];
                // Release URLs may be replaced or removed: a pinned original digest is mandatory there; old raw
                // repository objects are already commit-pinned.
                if (url.StartsWith(Raw, StringComparison.Ordinal) || sameName && Sha256.IsMatch(checksum))
                {
                    artifact = url; fingerprint = sameName ? checksum : "";
                    break;
                }
            }
            var title = row.Name.Length > 0 ? row.Name : package;
            result[package] = new JsonObject
            {
                ["pkgName"] = package, ["name"] = title.Length > 160 ? title[..160] : title,
                ["versionName"] = row.Version.Length > 60 ? row.Version[..60] : row.Version,
                ["lang"] = row.Language.Length > 0 ? row.Language : languages.Count != 1 ? "all" : languages[0],
                ["languages"] = new JsonArray(languages.Select(lang => (JsonNode?)JsonValue.Create(lang)).ToArray()),
                ["nsfw"] = row.Nsfw, ["sources"] = new JsonArray(sources.Select(source => (JsonNode?)source).ToArray()),
                ["source_count"] = sources.Count, ["origin"] = "Keiyoushi", ["commit"] = commit,
                ["commit_url"] = Repository + "/commit/" + commit, ["archive_url"] = artifact, ["sha256"] = fingerprint,
                ["module"] = "src/" + package["eu.kanade.tachiyomi.extension.".Length..].Replace('.', '/')
            };
        }
        if (result.Count == 0) throw new InvalidDataException("Repository index contains no real sources; an app-upgrade notice is not an empty catalog");
        return result;
    }

    private sealed record Candidate(string Name, string Package, string Version, string Language, bool Nsfw, JsonArray Sources, string Apk, string Jar);

    private static byte[] Expanded(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaximumIndex) throw new InvalidDataException("Repository index is empty or exceeds 8 MiB");
        if (data.Length < 2 || data[0] != 0x1f || data[1] != 0x8b) return data;
        try
        {
            using var input = new GZipStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
            using var output = new MemoryStream(); var buffer = new byte[32768];
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                output.Write(buffer, 0, read);
                if (output.Length > MaximumIndex) throw new InvalidDataException("Repository index is oversized or incomplete");
            }
            return output.ToArray();
        }
        catch (Exception error) when (error is InvalidDataException or IOException) { throw new InvalidDataException("Repository index is oversized or incomplete", error); }
    }

    private readonly record struct Field(int Number, ulong Integer, byte[]? Bytes);

    /// <summary>Protobuf fields: varints as integers, length-delimited and fixed values as bytes (0.4.x _protobuf_fields).</summary>
    private static List<Field> Fields(byte[] data)
    {
        var fields = new List<Field>(); var offset = 0;
        ulong Varint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (offset >= data.Length) throw new InvalidDataException("Incomplete protobuf manifest");
                var value = data[offset++];
                if (shift < 64) result |= (ulong)(value & 127) << shift;
                if ((value & 128) == 0) return result;
            }
            throw new InvalidDataException("Invalid protobuf integer");
        }
        while (offset < data.Length)
        {
            var key = Varint(); var number = key >> 3; var wire = key & 7;
            if (number == 0 || number > int.MaxValue) throw new InvalidDataException("Invalid protobuf field");
            if (wire == 0) fields.Add(new((int)number, Varint(), null));
            else if (wire is 1 or 2 or 5)
            {
                var length = wire == 2 ? Varint() : wire == 1 ? 8UL : 4UL;
                if (length > (ulong)(data.Length - offset)) throw new InvalidDataException("Truncated protobuf manifest");
                fields.Add(new((int)number, 0, data.AsSpan(offset, (int)length).ToArray())); offset += (int)length;
            }
            else throw new InvalidDataException("Unsupported protobuf wire type");
            if (fields.Count > 100000) throw new InvalidDataException("Repository manifest contains too many fields");
        }
        return fields;
    }

    private static string TextField(List<Field> fields, int number)
    {
        var field = fields.FirstOrDefault(item => item.Number == number);
        if (field.Number == 0 || field.Bytes is null) return "";
        try { return StrictUtf8.GetString(field.Bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Repository index text is not UTF-8", error); }
    }

    /// <summary>A bounded ZIP whose manifest declares <paramref name="expected"/> and that carries executable code.</summary>
    public static void ValidatePackage(byte[] data, string expected)
    {
        if (data.Length <= 100 || data.Length > MaximumPackage) throw new InvalidDataException("Invalid archived package size");
        var payloads = SourcePackage.Payloads(data);
        var package = (string?)SourceManifest.Read(data).Attribute("package") ?? "";
        if (package != expected) throw new InvalidDataException("Archived binary package identity does not match its original catalog entry");
        if (!payloads.Keys.Any(name => name.EndsWith(".class", StringComparison.Ordinal) || name.EndsWith(".dex", StringComparison.Ordinal)))
            throw new InvalidDataException("Archive has no executable extension code");
    }

    // ---------------------------------------------------------------- helpers

    private double Now() => _clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
    private static JsonObject DefaultRefresh() => new()
    {
        ["state"] = "idle", ["message"] = "Refresh to discover original archived sources", ["snapshots"] = 0, ["requests"] = 0, ["coverage_days"] = 730
    };
    private static JsonObject Idle() => new() { ["state"] = "idle" };
    private static JsonNode? Get(JsonNode? node, string key) => node is JsonObject value ? value[key] : null;
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static long Integer(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    /// <summary>Python truthiness of a JSON value: null, false, "", 0, [] and {} are false.</summary>
    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject value => value.Count > 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => value.ToJsonString() is not ("0" or "0.0" or "-0" or "-0.0"),
            _ => false
        },
        _ => false
    };

    /// <summary>Python str() of a JSON scalar for names and identifiers; null becomes "".</summary>
    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.ToJsonString(),
        JsonValue value when value.GetValueKind() == JsonValueKind.True => "True",
        JsonValue value when value.GetValueKind() == JsonValueKind.False => "False",
        _ => node!.ToJsonString()
    };

    private static JsonNode? ParseNode(byte[] bytes)
    {
        try { return JsonNode.Parse(StrictUtf8.GetString(bytes), documentOptions: new() { MaxDepth = 64 }); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException) { throw new InvalidDataException("Original repository returned invalid JSON", error); }
    }

    private static JsonArray ParseArray(byte[] bytes) => ParseNode(bytes) as JsonArray ?? throw new InvalidDataException("Original repository returned an unexpected commit list");
}
