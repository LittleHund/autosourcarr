using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Backup;

/// <summary>
/// SET-3 old-file removal (port of 0.4.x setup_cleanup.py, bounded and deliberately not a general file deleter).
/// Only files the app itself left are candidates: old one-off release reports in the app folder (with a matching
/// AutoSourcarr header and an older version), Python bytecode whose source is gone, and the app's own abandoned
/// temporary files (interrupted atomic writes, snapshots, restore and backup work folders older than a day).
/// Nothing is deleted: files move to a dated trash folder under the data folder (<c>trash/yyyy-MM-dd/…</c>).
/// Databases, config.json, config.key, env files (the app still reads source-host.env and autosourcarr.env),
/// source profiles, downloads, repair backups and unknown files are never touched. Preview first (token), then apply;
/// the same scan runs automatically after the setup wizard and on the first start of a new version.
/// </summary>
public sealed partial class OldFileCleanup(Func<string> appRoot, string configurationRoot, string dataDirectory, string databaseDirectory, string appVersion,
    TimeProvider? clock = null)
{
    public const string ResultFile = "cleanup-last.json";
    private const int MaxFiles = 1500;
    private static readonly TimeSpan TemporaryAge = TimeSpan.FromDays(1);
    private static readonly HashSet<string> Skip = new(StringComparer.Ordinal)
    { "data", "config", "source-host", "node_modules", ".git", ".venv", "venv", "build", "bundled", "repairs", "extension", "translate", "static" };
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    [GeneratedRegex(@"^(?:autosourcarr-v(\d+\.\d+\.\d+)-(?:upgrade|regression-audit)|(?:UPGRADE|REGRESSION-AUDIT)-(\d+\.\d+\.\d+))\.md$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Report();

    [GeneratedRegex(@"^\.(?:config\.json|accounts\.json)\.[0-9a-f]{32}\.tmp$|^\.autosourcarr-(?:snapshot|write-test)-[0-9a-f]{32}$|^(?:catalog|chapter-origins)\.sqlite\.restoring$", RegexOptions.CultureInvariant)]
    private static partial Regex Temporary();

    [GeneratedRegex(@"^\.(?:work|rollback)-[0-9a-f]{16,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkFolder();

    public sealed record Candidate(string Area, string Relative, string Kind, long Bytes, string FullPath, bool Directory, bool Delete = false);

    /// <summary>Trash folders older than this are offered for deletion (they already waited in the trash).</summary>
    public static readonly TimeSpan TrashAge = TimeSpan.FromDays(30);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DayFolder();

    private string Trash => Path.Combine(Path.GetFullPath(dataDirectory), "trash");

    /// <summary>The files a cleanup would move, with the preview token the apply must send back.</summary>
    public JsonObject Preview()
    {
        var (items, checkedCount, limited) = Scan();
        return new JsonObject
        {
            ["items"] = new JsonArray(items.Select(item => (JsonNode?)new JsonObject
            {
                ["path"] = item.Area + "/" + item.Relative, ["kind"] = item.Kind, ["bytes"] = item.Bytes, ["action"] = item.Delete ? "delete" : "move to trash",
            }).ToArray()),
            ["checked"] = checkedCount, ["limited"] = limited, ["preview_token"] = Token(items),
            ["trash"] = "trash/" + Day(), ["scope"] = Scope,
        };
    }

    /// <summary>Moves the previewed files to the trash; refused (409) when the scan changed since the preview.</summary>
    public JsonObject Apply(string token)
    {
        var (items, checkedCount, limited) = Scan();
        if (!string.Equals(Token(items), token, StringComparison.Ordinal))
            throw new BackupRefusedException("The old files changed since the preview; open the preview again.", 409);
        return Move(items, checkedCount, limited, "manual");
    }

    /// <summary>The automatic run after setup and on image updates (SET-3).</summary>
    public JsonObject RunAutomatic(string trigger)
    {
        try
        {
            var (items, checkedCount, limited) = Scan();
            return Move(items, checkedCount, limited, trigger);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var failed = new JsonObject { ["status"] = "failed", ["removed"] = new JsonArray(), ["trigger"] = trigger,
                ["message"] = "Setup was saved, but cleanup could not complete. Your existing files were retained." };
            Save(failed);
            return failed;
        }
    }

    /// <summary>Marks a run as started, so the setup wizard shows "pending" instead of the previous run's result.</summary>
    public void MarkPending(string trigger) => Save(new JsonObject
    {
        ["status"] = "pending", ["trigger"] = trigger, ["removed"] = new JsonArray(),
        ["at"] = _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
    });

    /// <summary>The last cleanup result (the setup wizard's cleanup status), or null when none ran yet.</summary>
    public JsonObject? Last()
    {
        var path = Path.Combine(Trash, ResultFile);
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception error) when (error is JsonException or IOException) { _ = error; return null; }
    }

    public const string Scope = "Only files AutoSourcarr itself left: old release reports, orphaned Python bytecode and abandoned temporary files. "
        + "They are moved to the trash folder under the data folder, not deleted; trash older than 30 days is deleted. Databases, configuration, "
        + "config.key and its temporary copies, env files, source profiles, downloads, repair backups and unknown files are kept.";

    private JsonObject Move(List<Candidate> items, int checkedCount, bool limited, string trigger)
    {
        var day = Day();
        var removed = new JsonArray(); var skipped = 0;
        foreach (var item in items)
        {
            var destination = Path.Combine(Trash, day, item.Area, item.Relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination) || Directory.Exists(destination))
                    destination += "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
                if (item.Delete) { Directory.Delete(item.FullPath, recursive: true); removed.Add(item.Area + "/" + item.Relative); continue; }
                if (item.Directory) Directory.Move(item.FullPath, destination);
                else
                {
                    var info = new FileInfo(item.FullPath);
                    if (!info.Exists || info.LinkTarget is not null || info.Length != item.Bytes) { skipped++; continue; }
                    File.Move(item.FullPath, destination);
                }
                removed.Add(item.Area + "/" + item.Relative);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _ = error; skipped++; }
        }
        var result = new JsonObject
        {
            ["status"] = "completed", ["trigger"] = trigger, ["at"] = _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["removed"] = removed, ["checked"] = checkedCount, ["skipped"] = skipped, ["limited"] = limited,
            ["trash"] = "trash/" + day, ["scope"] = Scope,
        };
        Save(result);
        return result;
    }

    private void Save(JsonObject result)
    {
        Directory.CreateDirectory(Trash);
        var path = Path.Combine(Trash, ResultFile);
        File.WriteAllText(path + ".pending", result.ToJsonString());
        File.Move(path + ".pending", path, overwrite: true);
    }

    private string Day() => _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Token(IEnumerable<Candidate> items) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', items.Select(item => item.Area + "/" + item.Relative + "|" + item.Bytes.ToString(CultureInfo.InvariantCulture))))));

    private (List<Candidate> Items, int Checked, bool Limited) Scan()
    {
        var items = new List<Candidate>();
        var count = 0; var limited = false;
        var current = Numeric(appVersion);
        // 1. The app folder: old release reports at the top, orphaned bytecode in app/__pycache__.
        var root = Path.GetFullPath(appRoot());
        if (System.IO.Directory.Exists(root))
        {
            var stack = new Stack<(string Path, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0 && !limited)
            {
                var (directory, depth) = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _ = error; continue; }
                foreach (var entry in entries)
                {
                    if (++count > MaxFiles) { limited = true; break; }
                    if (entry.LinkTarget is not null) continue;
                    if (entry is DirectoryInfo folder)
                    {
                        if (depth < 4 && !Skip.Contains(folder.Name)) stack.Push((folder.FullName, depth + 1));
                        continue;
                    }
                    var file = (FileInfo)entry;
                    var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                    if (depth == 0 && Report().Match(file.Name) is { Success: true } match && file.Length <= 256_000)
                    {
                        var old = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                        string header;
                        using (var reader = new StreamReader(file.FullName)) { var buffer = new char[512]; header = new string(buffer, 0, reader.Read(buffer, 0, buffer.Length)); }
                        if (Compare(Numeric(old), current) < 0 && Regex.IsMatch(header, @"^#.*AutoSourcarr.*" + Regex.Escape(old) + @"\b", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                            items.Add(new("app", relative, "old release report", file.Length, file.FullName, false));
                    }
                    else if (file.Extension == ".pyc" && file.Directory?.Name == "__pycache__" && relative is var path
                        && (path.StartsWith("app/__pycache__/", StringComparison.Ordinal) || path.StartsWith("autosourcarr/app/__pycache__/", StringComparison.Ordinal))
                        && file.Name.Contains(".cpython-", StringComparison.Ordinal))
                    {
                        var source = Path.Combine(file.Directory.Parent!.FullName, file.Name[..file.Name.IndexOf(".cpython-", StringComparison.Ordinal)] + ".py");
                        if (!File.Exists(source)) items.Add(new("app", relative, "orphaned Python bytecode", file.Length, file.FullName, false));
                    }
                }
            }
        }
        // 2. The app's own abandoned temporary files (top level only) in the config, data and database folders.
        var cutoff = _clock.GetUtcNow() - TemporaryAge;
        foreach (var (area, folder) in new[] { ("config", configurationRoot), ("data", dataDirectory), ("database", databaseDirectory) }
            .DistinctBy(pair => Path.GetFullPath(pair.Item2), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            if (!System.IO.Directory.Exists(folder)) continue;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                if (++count > MaxFiles) { limited = true; break; }
                if (file.LinkTarget is null && Temporary().IsMatch(file.Name) && file.LastWriteTimeUtc < cutoff.UtcDateTime)
                    items.Add(new(area, file.Name, "abandoned temporary file", file.Length, file.FullName, false));
            }
        }
        var backups = Path.Combine(dataDirectory, "instance-backups");
        if (System.IO.Directory.Exists(backups))
            foreach (var folder in new DirectoryInfo(backups).EnumerateDirectories())
                if (folder.LinkTarget is null && WorkFolder().IsMatch(folder.Name) && folder.LastWriteTimeUtc < cutoff.UtcDateTime)
                    items.Add(new("data", "instance-backups/" + folder.Name, "abandoned backup work folder", 0, folder.FullName, true));
        // Abandoned restore previews (never the one armed for the next start).
        var staged = Path.Combine(backups, "staged");
        string? armed = null;
        try { armed = File.Exists(Path.Combine(backups, InstanceBackupStore.PendingFile)) ? JsonNode.Parse(File.ReadAllText(Path.Combine(backups, InstanceBackupStore.PendingFile)))?["stage_id"]?.GetValue<string>() : null; }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException) { armed = "unknown"; }
        if (armed != "unknown" && System.IO.Directory.Exists(staged))
            foreach (var folder in new DirectoryInfo(staged).EnumerateFileSystemInfos())
                if (folder.LinkTarget is null && folder.Name != armed && folder.LastWriteTimeUtc < cutoff.UtcDateTime)
                    items.Add(new("data", "instance-backups/staged/" + folder.Name, "abandoned restore preview", folder is FileInfo file ? file.Length : 0, folder.FullName, folder is DirectoryInfo));
        // Expired trash: day folders older than TrashAge are deleted (they already waited there).
        var trashCutoff = _clock.GetUtcNow().UtcDateTime - TrashAge;
        if (System.IO.Directory.Exists(Trash))
            foreach (var folder in new DirectoryInfo(Trash).EnumerateDirectories())
                if (folder.LinkTarget is null && DayFolder().IsMatch(folder.Name)
                    && DateTime.TryParseExact(folder.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                    && day < trashCutoff)
                    items.Add(new("data", "trash/" + folder.Name, "expired trash (older than 30 days)", 0, folder.FullName, true, Delete: true));
        return (items.OrderBy(item => item.Area + "/" + item.Relative, StringComparer.Ordinal).ToList(), count, limited);
    }

    private static int[] Numeric(string version)
    {
        var parts = Regex.Match(version, @"^\d+(?:\.\d+){0,2}").Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return [.. Enumerable.Range(0, 3).Select(index => index < parts.Length && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)];
    }

    private static int Compare(int[] left, int[] right)
    {
        for (var index = 0; index < 3; index++) if (left[index] != right[index]) return left[index].CompareTo(right[index]);
        return 0;
    }
}
