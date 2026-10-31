using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Persistence;

/// <summary>Shared regular-file checks for private journals and immutable raw objects.</summary>
internal static class AtomicSourceFiles
{
    public static byte[]? Read(string path, int maximum)
    {
        if (maximum is < 0 or > RawCodeRules.MaximumRepositoryZip) throw new ArgumentOutOfRangeException(nameof(maximum));
        PrivateDocumentStore.ValidateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FileStream input;
        try { input = PrivateDocumentStore.OpenRegular(path, writable: false, create: false, maximum); }
        catch (FileNotFoundException) { return null; }
        using (input)
        using (var output = new MemoryStream())
        {
            var buffer = new byte[8192];
            while (true)
            {
                var count = input.Read(buffer, 0, Math.Min(buffer.Length, maximum + 1 - checked((int)output.Length)));
                if (count == 0) return output.ToArray(); output.Write(buffer, 0, count);
                if (output.Length > maximum) throw new InvalidDataException("Source document exceeds its byte bound");
            }
        }
    }
    public static void Write(string path, byte[] bytes, int maximum, bool publicCode = false, bool immutable = false, bool executable = false)
    {
        if (bytes.Length > maximum || maximum is < 0 or > RawCodeRules.MaximumRepositoryZip) throw new InvalidDataException("Source document exceeds its byte bound");
        path = Path.GetFullPath(path); var parent = Path.GetDirectoryName(path)!;
        PrivateDocumentStore.ValidateDirectory(parent, publicCode);
        var previous = Read(path, maximum);
        if (previous is not null)
        {
            if (previous.AsSpan().SequenceEqual(bytes)) return;
            if (immutable) throw new InvalidDataException("Immutable source storage already contains different bytes");
        }
        var temporary = Path.Combine(parent, ".source-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = publicCode ? (executable ? (UnixFileMode)0x1ed : (UnixFileMode)0x1a4) : (executable ? (UnixFileMode)0x1c0 : (UnixFileMode)0x180);
            using (var output = new FileStream(temporary, options)) { output.Write(bytes); output.Flush(flushToDisk: true); }
            try { File.Move(temporary, path, overwrite: !immutable); }
            catch (IOException) when (immutable)
            {
                // Another instance may have published exactly the same object. Never replace it.
                var observed = Read(path, maximum);
                if (observed is null || !observed.AsSpan().SequenceEqual(bytes)) throw;
            }
            DurableConfigurationFiles.SyncDirectory(parent);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
