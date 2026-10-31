using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Persistence;

public sealed record DatabasePaths(string Directory, string Catalog, string Origins)
{
    public static DatabasePaths Resolve(AutoSourcarrSettings settings)
    {
        var data = FullPath(settings.DataDir);
        var target = settings.DatabaseDir is null ? data : FullPath(settings.DatabaseDir);
        System.IO.Directory.CreateDirectory(data);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(data, target, comparison))
        {
            System.IO.Directory.CreateDirectory(target);
        }
        else
        {
            if (!System.IO.Directory.Exists(target))
                throw new ConfigurationException("Configured DATABASE_DIR is not an existing directory. Restore the persistent database mount; an empty replacement will not be created.");
            foreach (var filename in new[] { "catalog.sqlite", "chapter-origins.sqlite" })
                if (!File.Exists(Path.Combine(target, filename)))
                    throw new ConfigurationException($"Configured DATABASE_DIR is missing {filename}. Restore the existing database mount before starting.");
        }
        return new(target, Path.Combine(target, "catalog.sqlite"), Path.Combine(target, "chapter-origins.sqlite"));
    }

    private static string FullPath(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length == 1 ? "" : path[2..]);
        return Path.GetFullPath(path);
    }
}
