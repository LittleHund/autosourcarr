using System.Buffers.Binary;
using System.Text;

namespace AutoSourcarr.Core.Repair.Migration;

/// <summary>One changed CONSTANT_Utf8 entry (pool index, decoded text before and after).</summary>
public sealed record Utf8Change(int Index, string Before, string After);

/// <summary>A rewritten class: the new bytes (the input itself when nothing changed or the class did not parse),
/// the number of replaced Utf8 constants and, for a class whose constant pool could not be read, the reason.</summary>
public sealed record ClassRewrite(byte[] Bytes, int Replacements, IReadOnlyList<Utf8Change> Changes, string? Error)
{
    public bool Parsed => Error is null;
}

/// <summary>The parts of a class file the structure check compares.</summary>
public sealed record ClassFileInfo(int MinorVersion, int MajorVersion, int PoolCount, int PoolEnd, string ThisClass, IReadOnlyList<string> Utf8);

/// <summary>
/// Minimal Java class-file constant-pool editor (JVMS §4.1, §4.4). It reads the magic, version and every constant
/// (tags 1, 3–12 and 15–20; long and double take two pool slots) and rewrites CONSTANT_Utf8 entries only, encoding
/// them again in modified UTF-8 with their new length. Everything else — other constants, unchanged Utf8 entries and
/// the whole rest of the class after the pool — is copied byte for byte, so no index, offset or attribute moves.
/// A class whose pool does not parse is returned unchanged with the reason.
/// </summary>
public static class ClassFileStrings
{
    public const uint Magic = 0xCAFEBABE;
    private const int MaximumUtf8Bytes = ushort.MaxValue;

    private readonly record struct Entry(int Index, byte Tag, int Offset, int Length);

    /// <summary>Reads magic, version, constant pool and this_class; throws InvalidDataException when the class is malformed.</summary>
    public static ClassFileInfo Parse(ReadOnlySpan<byte> bytes)
    {
        var entries = ReadPool(bytes, out var minor, out var major, out var count, out var end);
        // access_flags u2, this_class u2, super_class u2, interfaces_count u2 must follow the pool.
        if (bytes.Length - end < 8) throw new InvalidDataException("Class file ends right after its constant pool");
        var thisIndex = BinaryPrimitives.ReadUInt16BigEndian(bytes[(end + 2)..]);
        var utf8 = new List<string>();
        var byIndex = new Dictionary<int, Entry>();
        foreach (var entry in entries)
        {
            byIndex[entry.Index] = entry;
            if (entry.Tag == 1) utf8.Add(DecodeModifiedUtf8(bytes.Slice(entry.Offset + 3, entry.Length - 3)));
        }
        if (!byIndex.TryGetValue(thisIndex, out var thisClass) || thisClass.Tag != 7) throw new InvalidDataException("this_class is not a CONSTANT_Class entry");
        var nameIndex = BinaryPrimitives.ReadUInt16BigEndian(bytes[(thisClass.Offset + 1)..]);
        if (!byIndex.TryGetValue(nameIndex, out var name) || name.Tag != 1) throw new InvalidDataException("this_class does not name a CONSTANT_Utf8 entry");
        return new(minor, major, count, end, DecodeModifiedUtf8(bytes.Slice(name.Offset + 3, name.Length - 3)), utf8);
    }

    /// <summary>Replaces every CONSTANT_Utf8 entry for which <paramref name="replace"/> returns a different text.</summary>
    public static ClassRewrite Rewrite(byte[] bytes, Func<string, string> replace)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(replace);
        List<Entry> entries; int end;
        try
        {
            entries = ReadPool(bytes, out _, out _, out _, out end);
            if (bytes.Length - end < 8) throw new InvalidDataException("Class file ends right after its constant pool");
        }
        catch (InvalidDataException error) { return new(bytes, 0, [], error.Message); }

        var changes = new List<Utf8Change>();
        var replaced = new Dictionary<int, byte[]>();
        foreach (var entry in entries)
        {
            if (entry.Tag != 1) continue;
            var before = DecodeModifiedUtf8(bytes.AsSpan(entry.Offset + 3, entry.Length - 3));
            var after = replace(before);
            if (string.Equals(before, after, StringComparison.Ordinal)) continue;
            var encoded = EncodeModifiedUtf8(after);
            if (encoded.Length > MaximumUtf8Bytes) return new(bytes, 0, [], "A replaced constant would exceed 65535 bytes");
            replaced[entry.Index] = encoded;
            changes.Add(new(entry.Index, before, after));
        }
        if (changes.Count == 0) return new(bytes, 0, [], null);

        using var output = new MemoryStream(bytes.Length + 64 * changes.Count);
        output.Write(bytes, 0, 10); // magic, minor, major, constant_pool_count
        Span<byte> header = stackalloc byte[3];
        foreach (var entry in entries)
        {
            if (replaced.TryGetValue(entry.Index, out var encoded))
            {
                header[0] = 1;
                BinaryPrimitives.WriteUInt16BigEndian(header[1..], (ushort)encoded.Length);
                output.Write(header);
                output.Write(encoded);
            }
            else output.Write(bytes, entry.Offset, entry.Length);
        }
        output.Write(bytes, end, bytes.Length - end);
        return new(output.ToArray(), changes.Count, changes, null);
    }

    /// <summary>Plain ordinal replacement of <paramref name="oldValue"/> inside Utf8 constants.</summary>
    public static ClassRewrite Rewrite(byte[] bytes, string oldValue, string newValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldValue);
        ArgumentNullException.ThrowIfNull(newValue);
        return Rewrite(bytes, text => text.Contains(oldValue, StringComparison.Ordinal) ? text.Replace(oldValue, newValue, StringComparison.Ordinal) : text);
    }

    private static List<Entry> ReadPool(ReadOnlySpan<byte> bytes, out int minor, out int major, out int count, out int end)
    {
        if (bytes.Length < 10 || BinaryPrimitives.ReadUInt32BigEndian(bytes) != Magic) throw new InvalidDataException("Not a Java class file (magic)");
        minor = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        major = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        if (major < 45) throw new InvalidDataException("Unsupported class file version " + major);
        count = BinaryPrimitives.ReadUInt16BigEndian(bytes[8..]);
        if (count < 1) throw new InvalidDataException("Constant pool count is zero");
        var entries = new List<Entry>(count);
        var offset = 10;
        for (var index = 1; index < count; index++)
        {
            if (offset >= bytes.Length) throw new InvalidDataException("Constant pool is truncated");
            var tag = bytes[offset];
            var size = tag switch
            {
                1 => 3 + (offset + 3 <= bytes.Length ? BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 1)..]) : 0), // Utf8
                3 or 4 => 5,              // Integer, Float
                5 or 6 => 9,              // Long, Double (two slots)
                7 or 8 or 16 or 19 or 20 => 3, // Class, String, MethodType, Module, Package
                9 or 10 or 11 or 12 or 17 or 18 => 5, // Field/Method/InterfaceMethodref, NameAndType, Dynamic, InvokeDynamic
                15 => 4,                  // MethodHandle: u1 reference_kind, u2 reference_index
                _ => throw new InvalidDataException("Unknown constant pool tag " + tag + " at entry " + index),
            };
            if (tag == 1 && offset + 3 > bytes.Length) throw new InvalidDataException("Constant pool is truncated");
            if (offset + size > bytes.Length) throw new InvalidDataException("Constant pool is truncated");
            if (tag == 15 && bytes[offset + 1] is < 1 or > 9) throw new InvalidDataException("Invalid MethodHandle reference kind");
            if (tag == 1) ValidateModifiedUtf8(bytes.Slice(offset + 3, size - 3));
            entries.Add(new(index, tag, offset, size));
            offset += size;
            if (tag is 5 or 6)
            {
                index++; // the following slot is unusable
                if (index >= count) throw new InvalidDataException("A long or double constant takes the last pool slot");
            }
        }
        end = offset;
        return entries;
    }

    private static void ValidateModifiedUtf8(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length;)
        {
            var b = data[i];
            if (b == 0 || b >= 0xF0) throw new InvalidDataException("Invalid modified UTF-8 in a Utf8 constant");
            if (b < 0x80) { i++; continue; }
            var length = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : 0;
            if (length == 0 || i + length > data.Length) throw new InvalidDataException("Invalid modified UTF-8 in a Utf8 constant");
            for (var k = 1; k < length; k++)
                if ((data[i + k] & 0xC0) != 0x80) throw new InvalidDataException("Invalid modified UTF-8 in a Utf8 constant");
            i += length;
        }
    }

    /// <summary>Modified UTF-8 (JVMS §4.4.7) to UTF-16; supplementary characters arrive as two encoded surrogates.</summary>
    public static string DecodeModifiedUtf8(ReadOnlySpan<byte> data)
    {
        ValidateModifiedUtf8(data);
        var text = new StringBuilder(data.Length);
        for (var i = 0; i < data.Length;)
        {
            var b = data[i];
            if (b < 0x80) { text.Append((char)b); i++; }
            else if ((b & 0xE0) == 0xC0) { text.Append((char)(((b & 0x1F) << 6) | (data[i + 1] & 0x3F))); i += 2; }
            else { text.Append((char)(((b & 0x0F) << 12) | ((data[i + 1] & 0x3F) << 6) | (data[i + 2] & 0x3F))); i += 3; }
        }
        return text.ToString();
    }

    /// <summary>UTF-16 to modified UTF-8: NUL as C0 80, each surrogate as its own three-byte form.</summary>
    public static byte[] EncodeModifiedUtf8(string text)
    {
        using var output = new MemoryStream(text.Length + 16);
        foreach (var c in text)
        {
            if (c != 0 && c < 0x80) output.WriteByte((byte)c);
            else if (c < 0x800)
            {
                output.WriteByte((byte)(0xC0 | (c >> 6)));
                output.WriteByte((byte)(0x80 | (c & 0x3F)));
            }
            else
            {
                output.WriteByte((byte)(0xE0 | (c >> 12)));
                output.WriteByte((byte)(0x80 | ((c >> 6) & 0x3F)));
                output.WriteByte((byte)(0x80 | (c & 0x3F)));
            }
        }
        return output.ToArray();
    }
}
