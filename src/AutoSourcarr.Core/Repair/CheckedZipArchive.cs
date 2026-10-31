using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AutoSourcarr.Core.Repair;

/// <summary>Checked ZIP wire identities, bounded decoding and complete entry CRC validation.
/// The caller owns package-specific names, file selection and expanded aggregate limits.</summary>
internal sealed class CheckedZipArchive(ZipArchive standard, byte[] payload, List<CheckedZipArchive.WireEntry> names) : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Encoding LegacyNames = CodePagesEncodingProvider.Instance.GetEncoding(437)!;
    private static InvalidDataException Invalid(string message) => new(message);
    public static CheckedZipArchive Open(byte[] payload, int maximum, int entries, Action<string>? validateName = null)
    {
        if (payload.Length == 0 || payload.Length > maximum) throw Invalid("Raw source ZIP exceeds its bounded input limit or is empty");
        var names = ZipNames(payload, entries, validateName);
        var archive = new ZipArchive(new MemoryStream(payload, writable: false), ZipArchiveMode.Read, leaveOpen: false, LegacyNames);
        try
        {
            if (archive.Entries.Count != names.Count || archive.Entries.Where((entry, index) => entry.FullName != names[index].Name).Any()) throw Invalid("Raw source ZIP filename identity changed");
            return new(archive, payload, names);
        }
        catch { archive.Dispose(); throw; }
    }
    private static byte[] ReadEntryBytes(ZipArchiveEntry entry, Stream input, int maximum)
    {
        if (entry.IsEncrypted || entry.Length < 0 || entry.Length > maximum) throw Invalid("Raw source entry exceeds its bounded input limit");
        var bytes = new byte[checked((int)entry.Length)]; var offset = 0;
        while (offset < bytes.Length)
        {
            var count = input.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw Invalid("Raw source entry size changed"); offset += count;
        }
        if (input.ReadByte() != -1 || Crc32(bytes) != entry.Crc32) throw Invalid("Raw source entry failed its integrity check");
        return bytes;
    }
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var b in bytes) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ (0xedb88320u & (uint)-(int)(crc & 1)); }
        return ~crc;
    }
    // Inspect the original wire names before ZipArchive decodes with a replacement fallback.
    // Both ordinary and ZIP64 central directories are bounded before entry objects allocate.
    internal sealed record WireEntry(string Name, ushort Method, int DataOffset);
    private static List<WireEntry> ZipNames(byte[] payload, int maximum, Action<string>? validateName)
    {
        var data = payload.AsSpan(); var end = -1;
        for (var i = data.Length - 22; i >= Math.Max(0, data.Length - 65557); i--)
            if (U32(data, i) == 0x06054b50 && i + 22 + U16(data, i + 20) == data.Length) { end = i; break; }
        if (end < 0 || U16(data, end + 4) != 0 || U16(data, end + 6) != 0) throw Invalid("Raw source ZIP is unreadable or split across disks");
        ulong count = U16(data, end + 10), size = U32(data, end + 12), offset = U32(data, end + 16); var centralEnd = end;
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue || end >= 20 && U32(data, end - 20) == 0x07064b50)
        {
            var locator = end - 20;
            if (locator < 0 || U32(data, locator) != 0x07064b50 || U32(data, locator + 4) != 0 || U32(data, locator + 16) != 1) throw Invalid("Invalid raw source ZIP64 directory");
            var position = U64(data, locator + 8); if (position > (ulong)Math.Max(0, locator - 56)) throw Invalid("Invalid raw source ZIP64 directory");
            var p = checked((int)position);
            if (U32(data, p) != 0x06064b50 || U64(data, p + 4) < 44 || U64(data, p + 4) != (ulong)(locator - p - 12) || U32(data, p + 16) != 0 || U32(data, p + 20) != 0 || U64(data, p + 24) != U64(data, p + 32))
                throw Invalid("Invalid raw source ZIP64 directory");
            count = U64(data, p + 32); size = U64(data, p + 40); offset = U64(data, p + 48); centralEnd = p;
        }
        else if (U16(data, end + 8) != count) throw Invalid("Split raw source ZIP refused");
        if (count == 0 || count > (ulong)maximum || size > (ulong)centralEnd || offset > (ulong)centralEnd - size) throw Invalid("Raw source archive has excessive entries or an invalid directory");
        var cursor = centralEnd - (int)size; var prefix = (ulong)cursor - offset; var names = new List<WireEntry>((int)count); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ulong index = 0; index < count; index++)
        {
            if (cursor > centralEnd - 46 || U32(data, cursor) != 0x02014b50) throw Invalid("Invalid raw source directory entry");
            var flags = U16(data, cursor + 8); var length = U16(data, cursor + 28); var extra = U16(data, cursor + 30); var comment = U16(data, cursor + 32);
            if (length == 0 || cursor + 46L + length + extra + comment > centralEnd || (flags & 1) != 0 || U16(data, cursor + 34) != 0) throw Invalid("Unsafe raw source directory entry");
            string name;
            try { name = ((flags & 0x800) == 0 ? LegacyNames : StrictUtf8).GetString(data.Slice(cursor + 46, length)); }
            catch (DecoderFallbackException) { throw Invalid("Invalid raw source filename encoding"); }
            validateName?.Invoke(name);
            if (!seen.Add(name)) throw Invalid("Raw source archive has duplicate entries");
            ulong local = U32(data, cursor + 42);
            if (local == uint.MaxValue)
            {
                var field = cursor + 46 + length; var endExtra = field + extra; var found = false;
                while (field <= endExtra - 4)
                {
                    var tag = U16(data, field); var sizeExtra = U16(data, field + 2); field += 4;
                    if (sizeExtra > endExtra - field) throw Invalid("Invalid ZIP64 source entry");
                    if (tag == 1)
                    {
                        var skip = (U32(data, cursor + 24) == uint.MaxValue ? 8 : 0) + (U32(data, cursor + 20) == uint.MaxValue ? 8 : 0);
                        if (skip + 8 > sizeExtra) throw Invalid("Missing ZIP64 source offset");
                        local = U64(data, field + skip); found = true; break;
                    }
                    field += sizeExtra;
                }
                if (!found) throw Invalid("Missing ZIP64 source offset");
            }
            if (local > (ulong)centralEnd || prefix > (ulong)centralEnd - local || local + prefix > (ulong)Math.Max(0, centralEnd - 30)) throw Invalid("Invalid raw source local header");
            var header = checked((int)(local + prefix)); var method = U16(data, cursor + 10);
            if (U32(data, header) != 0x04034b50 || U16(data, header + 6) != flags || U16(data, header + 8) != method) throw Invalid("Raw source local header identity changed");
            var localLength = U16(data, header + 26); var localExtra = U16(data, header + 28); var start = header + 30 + localLength + localExtra;
            if (start > centralEnd || localLength != length || !data.Slice(header + 30, localLength).SequenceEqual(data.Slice(cursor + 46, length))) throw Invalid("Raw source local filename changed");
            names.Add(new(name, method, start)); cursor += 46 + length + extra + comment;
        }
        if (cursor != centralEnd) throw Invalid("Raw source directory size changed");
        return names;
    }
    private static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> data, int offset, int length) => offset >= 0 && offset <= data.Length - length ? data.Slice(offset, length) : throw Invalid("Truncated raw source ZIP directory");
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Field(data, offset, 2));
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Field(data, offset, 4));
    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(Field(data, offset, 8));
    private readonly Dictionary<string, WireEntry> _wire = names.ToDictionary(row => row.Name, StringComparer.Ordinal);
    private SharpCompress.Archives.IArchive? _legacy;
    private Dictionary<string, SharpCompress.Archives.IArchiveEntry>? _legacyEntries;
    public IReadOnlyList<ZipArchiveEntry> Entries => standard.Entries;
    public ushort CompressionMethod(ZipArchiveEntry entry) => _wire[entry.FullName].Method;
    public byte[] ReadEntry(ZipArchiveEntry entry, int maximum)
    {
        if (entry.IsEncrypted || entry.Length < 0 || entry.Length > maximum) throw Invalid("Raw source entry exceeds its bounded input limit");
        var wire = _wire[entry.FullName];
        if (wire.Method is not (0 or 8 or 12 or 14)) throw Invalid("Unsupported raw source ZIP compression method");
        if (wire.Method is not (12 or 14)) { using var input = entry.Open(); return ReadEntryBytes(entry, input, maximum); }
        if (wire.Method == 14)
        {
            // ZIP LZMA properties precede the compressed stream. Reject an attacker-sized
            // decoder dictionary before the codec allocates it; normal Python ZIPs use8MiB.
            var p = wire.DataOffset;
            if (U16(payload, p + 2) != 5 || U32(payload, p + 5) > 64 * 1024 * 1024) throw Invalid("Raw source LZMA dictionary exceeds its bound");
        }
        if (_legacy is null)
        {
            _legacy = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(new MemoryStream(payload, writable: false), new SharpCompress.Readers.ReaderOptions
            { LeaveStreamOpen = false, ArchiveEncoding = new SharpCompress.Common.ArchiveEncoding { Default = LegacyNames, UTF8 = StrictUtf8 } });
            _legacyEntries = _legacy.Entries.ToDictionary(row => row.Key ?? throw Invalid("Missing source entry name"), StringComparer.Ordinal);
            if (_legacyEntries.Count != _wire.Count || _legacyEntries.Keys.Any(key => !_wire.ContainsKey(key))) throw Invalid("Raw source codec filename identity changed");
        }
        var item = _legacyEntries![entry.FullName];
        if (item.IsEncrypted || item.Size != entry.Length || item.Crc != entry.Crc32) throw Invalid("Raw source codec entry identity changed");
        using var decoded = item.OpenEntryStream(); return ReadEntryBytes(entry, decoded, maximum);
    }
    public void Dispose() { _legacy?.Dispose(); standard.Dispose(); }
}
