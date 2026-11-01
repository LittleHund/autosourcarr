using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Sources.Inventory;

public sealed class RetainedApkEvidence
{
    private readonly byte[] _bytes;
    internal RetainedApkEvidence(byte[] bytes, string basis) { _bytes = (byte[])bytes.Clone(); Basis = basis; Sha256 = SourceInventoryFiles.Digest(bytes); }
    public string Basis { get; }
    public string Sha256 { get; }
    public byte[] Contents() => (byte[])_bytes.Clone();
}

/// <summary>Retains original binary evidence per instance. Does not convert, load, install or infer source identity.</summary>
public sealed class SourceBaselineStore(IInstalledSourceCaptures installed, string maintenanceRoot, BoundedWorkScheduler io, TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly string _root = Path.GetFullPath(maintenanceRoot);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly OperationOwner _owner = new();
    public async Task<RetainedApkEvidence?> SelectOriginalAsync(JsonObject extension, string? baselineSha256, CancellationToken cancellationToken = default)
    {
        var copy = (JsonObject)extension.DeepClone(); RawCodeRules.Package(SourceInventoryFiles.Text(copy, "pkgName")); using var lease = _owner.Enter(); using var linked = Lifetime(cancellationToken);
        if (baselineSha256 is not null) RawCodeRules.Hash(baselineSha256);
        return await io.RunAsync(() => SelectOriginal(copy, baselineSha256), linked.Token).ConfigureAwait(false);
    }
    /// <summary>Caller owns sharedSourceAdmin, including the capture and original-APK attachment.</summary>
    public async Task<JsonObject> AttachOriginalAsync(string package, string version, byte[] bytes, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed) throw new InvalidDataException("Confirm that this is the original APK used for the selected installed version");
        var data = (byte[])bytes.Clone(); using var lease = _owner.Enter(); using var linked = Lifetime(cancellationToken); var token = linked.Token;
        var capture = await installed.CaptureAsync(package, version, token).ConfigureAwait(false) ?? throw new InvalidDataException("The installed source package or version changed; refresh before preparing repair input");
        return await io.RunAsync(() =>
        {
            installed.RequireCurrent(capture); var checkedApk = InspectApk(data, "original.apk");
            if (SourceInventoryFiles.Text(checkedApk, "pkgName") != package) throw new InvalidDataException("The APK belongs to a different package; choose the installed source it came from");
            var digest = SourceInventoryFiles.Text(checkedApk, "sha256"); var directory = Path.Combine(_root, "repair-apks", digest); PrivateDocumentStore.ValidateDirectory(directory);
            AtomicSourceFiles.Write(Path.Combine(directory, "uploaded.apk"), data, SourceInventoryFiles.PackageLimit, immutable: true);
            var receipt = (JsonObject)checkedApk.DeepClone(); receipt["state"] = "provided"; receipt["confirmed_original"] = true; receipt["target_version"] = version; receipt["target_jar_sha256"] = capture.Source.BinarySha256; receipt["completed_at"] = (_clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds;
            AtomicSourceFiles.Write(Path.Combine(directory, "receipt.json"), CanonicalJson.Bytes(receipt), 65536); installed.RequireCurrent(capture);
            return new JsonObject { ["pkg_name"] = package, ["sha256"] = digest, ["message"] = "Original APK retained as repair evidence. Installed source and settings are preserved; start a new repair to verify the match." };
        }, token).ConfigureAwait(false);
    }
    /// <summary>Original repair input only. Isolated conversion equality and cold import remain mandatory.</summary>
    public async Task<JsonObject> PrepareRepairInputAsync(string package, string version, bool rawFirst = false, CancellationToken cancellationToken = default)
    {
        using var lease = _owner.Enter(); using var linked = Lifetime(cancellationToken); var token = linked.Token;
        var capture = await installed.CaptureAsync(package, version, token).ConfigureAwait(false) ?? throw new InvalidDataException("The installed source package or version changed; refresh before preparing repair input");
        return await io.RunAsync(() =>
        {
            installed.RequireCurrent(capture); var native = capture.Source; var baseline = capture.Contents(); SaveBackup(native.Extension, baseline);
            var result = new JsonObject { ["baseline"] = Convert.ToBase64String(baseline) };
            void NativeIdentity()
            {
                result["installed_version"] = SourceInventoryFiles.Text(native.Extension, "versionName");
                result["installed_sources"] = new JsonArray(native.Extension["source"]!["nodes"]!.AsArray().OfType<JsonObject>().Select(source =>
                {
                    var row = new JsonObject(); foreach (var key in new[] { "id", "name", "lang", "baseUrl" }) row[key] = source.ContainsKey(key) ? source[key]?.DeepClone() : JsonValue.Create(""); return (JsonNode?)row;
                }).ToArray());
            }
            if (rawFirst) { result["raw_first"] = true; NativeIdentity(); }
            IReadOnlyDictionary<string, SourcePackagePayload> payloads;
            try { payloads = SourcePackage.Payloads(baseline); }
            catch (InvalidDataException) { installed.RequireCurrent(capture); return result; } // Preserve the isolated worker's rejection of malformed original input.
            if (payloads.ContainsKey("AndroidManifest.xml") || !payloads.Keys.Any(name => name.EndsWith(".class", StringComparison.Ordinal))) { installed.RequireCurrent(capture); return result; }
            var original = SelectOriginal(native.Extension, native.BinarySha256);
            if (original is null)
            {
                if (rawFirst) { installed.RequireCurrent(capture); return result; }
                throw new InvalidDataException("The installed APK was converted to a JAR without its manifest, and its matching original APK is not retained. Attach its Original APK for repair, then start a new repair. Existing source settings, library data and rollback backup are retained.");
            }
            result["source_apk"] = Convert.ToBase64String(original.Contents()); result["original_apk_basis"] = original.Basis; NativeIdentity(); installed.RequireCurrent(capture); return result;
        }, token).ConfigureAwait(false);
    }
    private void SaveBackup(JsonObject extension, byte[] baseline)
    {
        var directory = Path.Combine(_root, "backups", SourceInventoryFiles.Digest(baseline)); PrivateDocumentStore.ValidateDirectory(directory);
        AtomicSourceFiles.Write(Path.Combine(directory, "installed.jar"), baseline, SourceInventoryFiles.PackageLimit, immutable: true);
        AtomicSourceFiles.Write(Path.Combine(directory, "identity.json"), CanonicalJson.Bytes(extension), 1024 * 1024);
    }
    private RetainedApkEvidence? SelectOriginal(JsonObject extension, string? baselineSha256)
    {
        var choices = new List<(double Stamp, string Directory, string Basis)>(); var seen = 0;
        foreach (var (relative, accepted) in new[] { ("repair-apks", "provided"), ("uploads", "installed") })
        {
            var root = new DirectoryInfo(Path.Combine(_root, relative)); if (!root.Exists || root.LinkTarget is not null || (root.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            SourceInventoryFiles.ExistingDirectory(root.FullName);
            foreach (var item in Directory.EnumerateFileSystemEntries(root.FullName))
            {
                if (++seen > 512) throw new InvalidDataException("Retained upload selection exceeded its bounded receipt limit; no package was selected");
                var folder = new DirectoryInfo(item); if (!SourceInventoryFiles.IsDigest(folder.Name) || !folder.Exists || folder.LinkTarget is not null || (folder.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                JsonObject receipt;
                try { receipt = SourceInventoryFiles.ReadObject(Path.Combine(item, "receipt.json"), 65536); }
                catch (Exception error) when (error is IOException or InvalidDataException) { continue; }
                if (SourceInventoryFiles.Text(receipt, "state") != accepted || SourceInventoryFiles.Text(receipt, "pkgName") != SourceInventoryFiles.Text(extension, "pkgName")) continue;
                if (accepted == "provided" && (receipt["confirmed_original"]?.GetValueKind() != JsonValueKind.True || SourceInventoryFiles.Text(receipt, "target_version") != SourceInventoryFiles.Text(extension, "versionName") || baselineSha256 is null || SourceInventoryFiles.Text(receipt, "target_jar_sha256") != baselineSha256)) continue;
                if (receipt["completed_at"] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !value.TryGetValue<double>(out var stamp) || !double.IsFinite(stamp) || stamp < 0 || SourceInventoryFiles.Text(receipt, "sha256") != folder.Name) continue;
                choices.Add((stamp, item, accepted == "provided" ? "user-confirmed-original" : "recorded-installed-upload"));
            }
            if (choices.Count != 0) break;
        }
        if (choices.Count == 0) return null; choices.Sort((left, right) => right.Stamp.CompareTo(left.Stamp));
        if (choices.Count > 1 && choices[0].Stamp == choices[1].Stamp) throw new InvalidDataException("Original source upload receipts are ambiguous; no APK was selected");
        var selected = choices[0]; var data = SourceInventoryFiles.ReadRequired(Path.Combine(selected.Directory, "uploaded.apk"), SourceInventoryFiles.PackageLimit);
        if (SourceInventoryFiles.Digest(data) != Path.GetFileName(selected.Directory) || SourceInventoryFiles.Text(InspectApk(data, "retained.apk"), "pkgName") != SourceInventoryFiles.Text(extension, "pkgName")) throw new InvalidDataException("Retained original APK failed its saved digest or package check");
        return new(data, selected.Basis);
    }
    public static JsonObject InspectApk(byte[] data, string filename)
    {
        if (!filename.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || filename.Length > 240 || filename.IndexOfAny(['/', '\\', '\r', '\n', '\0']) >= 0) throw new InvalidDataException("Choose a source APK file with a plain filename");
        var payloads = SourcePackage.Payloads(data); var manifest = SourceManifest.Read(data); var package = (string?)manifest.Attribute("package") ?? "";
        if (!SourceInventoryFiles.IsApkPackage(package)) throw new InvalidDataException("APK package identity is invalid");
        if (!payloads.Keys.Any(SourceInventoryFiles.IsDexName)) throw new InvalidDataException("The file does not contain Android extension code; choose its APK, not a renamed JAR");
        if (package == "eu.kanade.tachiyomi.extension.all.autosourcarr") throw new InvalidDataException("Install the AutoSourcarr reader extension in your main Suwayomi, not in its integrated source engine");
        return new() { ["pkgName"] = package, ["sha256"] = SourceInventoryFiles.Digest(data), ["bytes"] = data.Length };
    }
    public Task StopAsync() => _owner.StopAsync();
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); await _owner.DisposeAsync().ConfigureAwait(false); }
    private CancellationTokenSource Lifetime(CancellationToken token) => CancellationTokenSource.CreateLinkedTokenSource(token, _owner.CancellationToken);
}

