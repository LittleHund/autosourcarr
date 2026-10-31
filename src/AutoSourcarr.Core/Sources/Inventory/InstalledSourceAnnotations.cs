using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Core.Sources.Inventory;

public interface IInstalledSourceAnnotations
{
    // Local evidence only. Incomplete/corrupt provenance must throw, never substitute official origin.
    IReadOnlyList<JsonObject> Annotate(IReadOnlyList<JsonObject> extensions);
}

/// <summary>Bounded original orphan/manual labels and successful local-upload receipts; no repository reads.</summary>
public sealed class InstalledSourceAnnotations(string maintenanceRoot) : IInstalledSourceAnnotations
{
    private readonly string _root = Path.GetFullPath(maintenanceRoot);
    public IReadOnlyList<JsonObject> Annotate(IReadOnlyList<JsonObject> extensions)
    {
        var labels = new JsonObject(); var archived = new HashSet<string>(StringComparer.Ordinal); var current = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(_root) || new DirectoryInfo(_root).LinkTarget is not null) SourceInventoryFiles.ExistingDirectory(_root);
        var path = Path.Combine(_root, "orphans.json");
        if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
        {
            var saved = SourceInventoryFiles.ReadObject(path, 16 * 1024 * 1024);
            if (saved["labels"] is not null && saved["labels"] is not JsonObject || saved["records"] is not null && saved["records"] is not JsonObject || saved["current"] is not null && saved["current"] is not JsonArray) throw new InvalidDataException("Local source provenance is damaged; no repository origin was inferred");
            labels = saved["labels"] as JsonObject ?? new();
            foreach (var pair in saved["records"] as JsonObject ?? new()) archived.Add(pair.Key);
            foreach (var item in saved["current"] as JsonArray ?? []) { if (item?.GetValueKind() != System.Text.Json.JsonValueKind.String) throw new InvalidDataException("Local source provenance is damaged"); current.Add(item.GetValue<string>()); }
        }
        var uploads = Path.Combine(_root, "uploads");
        if (Directory.Exists(uploads) || new DirectoryInfo(uploads).LinkTarget is not null)
        {
            SourceInventoryFiles.ExistingDirectory(uploads); var seen = 0;
            foreach (var directory in Directory.EnumerateFileSystemEntries(uploads))
            {
                if (++seen > 512) throw new InvalidDataException("Local source provenance exceeded its bounded receipt limit");
                var info = new DirectoryInfo(directory); if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0 || !info.Exists) throw new InvalidDataException("Local source provenance contains an unsafe receipt path");
                if (!SourceInventoryFiles.IsDigest(info.Name)) continue;
                var receiptPath = Path.Combine(directory, "receipt.json"); if (!File.Exists(receiptPath)) continue;
                var receipt = SourceInventoryFiles.ReadObject(receiptPath, 65536); var package = SourceInventoryFiles.Text(receipt, "pkgName");
                if (SourceInventoryFiles.Text(receipt, "state") != "installed" || !SourceInventoryFiles.IsApkPackage(package)) continue;
                if (!labels.ContainsKey(package)) labels[package] = new JsonObject { ["orphaned"] = true, ["reason"] = "manual-upload" };
            }
        }
        var result = new List<JsonObject>();
        foreach (var original in extensions)
        {
            var row = (JsonObject)original.DeepClone(); var package = SourceInventoryFiles.Text(row, "pkgName");
            var label = labels[package] as JsonObject;
            if (labels.ContainsKey(package) && (label is null || label["orphaned"]?.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))) throw new InvalidDataException("Local source manual label is damaged");
            var local = row.ContainsKey("storeIndexUrl") && row.ContainsKey("apkUrl") && SourceInventoryFiles.Text(row, "storeIndexUrl").Length == 0 && SourceInventoryFiles.Text(row, "apkUrl").Length == 0;
            var oldArchive = archived.Contains(package) && !current.Contains(package); var obsolete = row["isObsolete"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
            var orphaned = label?["orphaned"]?.GetValue<bool>() ?? (local || obsolete || oldArchive);
            var reason = label?["orphaned"]?.GetValue<bool>() == true ? SourceInventoryFiles.Text(label, "reason") : local ? "manual-upload" : oldArchive ? "archived" : "catalog-missing";
            row["is_orphaned"] = orphaned; row["orphan_reason"] = orphaned ? reason : ""; result.Add(row);
        }
        return result;
    }
}
