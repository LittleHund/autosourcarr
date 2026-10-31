namespace AutoSourcarr.Core.Settings;

/// <summary>Bounded reads and atomic, owner-only writes for config.json, config.key and conversion backups.</summary>
internal static class ConfigFiles
{
    public const int Limit = 2 * 1024 * 1024;

    public static byte[]? ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
            throw new ConfigurationException(Path.GetFileName(path) + " must be a regular file, not a link.");
        if (Directory.Exists(path))
            throw new ConfigurationException(Path.GetFileName(path) + " must be a regular file, not a folder.");
        if (!info.Exists)
            return null;
        if (info.Length > Limit)
            throw new ConfigurationException(Path.GetFileName(path) + " is larger than the supported 2 MB.");
        return File.ReadAllBytes(path);
    }

    /// <summary>Writes a sibling temporary file, flushes it to disk and renames it over the target.</summary>
    public static void WriteAtomic(string path, ReadOnlySpan<byte> data)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
