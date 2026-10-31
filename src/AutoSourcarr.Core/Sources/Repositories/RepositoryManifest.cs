using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Sources.Repositories;

/// <summary>Read-only extension-repository metadata; recognition does not establish code compatibility or trust.</summary>
public static class RepositoryManifest
{
    public const int MaximumBytes = 3 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static JsonObject Parse(byte[] body)
    {
        if (body.Length == 0 || body.Length > MaximumBytes) throw Invalid("Repository manifest is empty or too large");
        if (body.AsSpan().StartsWith(new byte[] { 0x1f, 0x8b })) body = ExpandGzip(body);
        var first = 0;
        while (first < body.Length && body[first] is 9 or 10 or 11 or 12 or 13 or 32) first++;
        try
        {
            if (first < body.Length && body[first] is (byte)'{' or (byte)'[')
            {
                var data = JsonNode.Parse(Utf8.GetString(body), documentOptions: new() { MaxDepth = 32 }) as JsonObject
                    ?? throw Invalid("Use the repository manifest (repo.json or index.pb), not an extension list");
                if (data["meta"] is JsonObject meta && Truth(meta["name"])) return Result(Truncate(PythonString(meta["name"]), 160), "Legacy Mihon repository", null);
                if (data["name"] is not JsonValue name || !name.TryGetValue<string>(out var text)
                    || !new[] { "signingKey", "extensionList", "extensionListUrl" }.Any(data.ContainsKey))
                    throw Invalid("This JSON is not a recognized extension repository");
                var count = data["extensionList"] is JsonObject list && list["extensions"] is JsonArray extensions ? (int?)extensions.Count : null;
                return Result(Truncate(text, 160), "Suwayomi repository JSON", count);
            }
            ReadOnlyMemory<byte>? nameBytes = null, embedded = null; var marker = false;
            Fields(body, (field, bytes) =>
            {
                if (field == 1 && bytes is not null) nameBytes ??= bytes;
                if (field == 101 && bytes is not null) embedded ??= bytes;
                if (field == 3 || field == 101 || field == 102) marker = true;
            });
            var repositoryName = Utf8.GetString((nameBytes ?? ReadOnlyMemory<byte>.Empty).Span);
            if (string.IsNullOrWhiteSpace(repositoryName) || repositoryName.EnumerateRunes().Count() > 160 || !marker)
                throw Invalid("This file is not a recognized extension repository manifest");
            int? extensionCount = null;
            if (embedded is { } rows) { var count = 0; Fields(rows, (field, _) => { if (field == 1) count++; }); extensionCount = count; }
            return Result(repositoryName, "Suwayomi repository protobuf", extensionCount);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw Invalid("Repository manifest could not be decoded"); }
    }

    private static JsonObject Result(string name, string format, int? count) => new() { ["name"] = name, ["format"] = format, ["extension_count"] = count };
    private static string Truncate(string value, int length) => string.Concat(value.EnumerateRunes().Take(length).Select(rune => rune.ToString()));
    private static bool Truth(JsonNode? value) => value switch
    {
        null => false, JsonArray array => array.Count > 0, JsonObject obj => obj.Count > 0,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Length > 0,
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.Number => scalar.GetValue<double>() != 0, _ => true
    };
    private static string PythonString(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : Repr(value);
    private static string Repr(JsonNode? value) => value switch
    {
        null => "None", JsonObject obj => "{" + string.Join(", ", obj.Select(pair => Repr(JsonValue.Create(pair.Key)) + ": " + Repr(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(", ", array.Select(Repr)) + "]",
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag ? "True" : "False",
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => Quote(text), _ => CanonicalJson.Text(value)
    };
    private static string Quote(string text)
    {
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\''; var result = new StringBuilder().Append(quote);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == quote || rune.Value == '\\') result.Append('\\').Append(rune.ToString());
            else if (rune.Value is 9 or 10 or 13) result.Append(rune.Value switch { 9 => "\\t", 10 => "\\n", _ => "\\r" });
            else if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned
                || Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator && rune.Value != 32)
                result.Append(rune.Value <= 255 ? "\\x" + rune.Value.ToString("x2", CultureInfo.InvariantCulture) : rune.Value <= 65535 ? "\\u" + rune.Value.ToString("x4", CultureInfo.InvariantCulture) : "\\U" + rune.Value.ToString("x8", CultureInfo.InvariantCulture));
            else result.Append(rune.ToString());
        }
        return result.Append(quote).ToString();
    }

    private static void Fields(ReadOnlyMemory<byte> data, Action<BigInteger, ReadOnlyMemory<byte>?> visit)
    {
        var offset = 0; var count = 0;
        BigInteger Varint()
        {
            BigInteger result = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (offset >= data.Length) throw Invalid("Incomplete protobuf manifest");
                var next = data.Span[offset++]; result |= new BigInteger(next & 127) << shift;
                if ((next & 128) == 0) return result;
            }
            throw Invalid("Invalid protobuf integer");
        }
        while (offset < data.Length)
        {
            var key = Varint(); var field = key >> 3; var wire = (int)(key & 7); ReadOnlyMemory<byte>? bytes = null;
            if (field == 0) throw Invalid("Invalid protobuf field");
            if (wire == 0) _ = Varint();
            else if (wire is 1 or 2 or 5)
            {
                var length = wire == 2 ? Varint() : wire == 1 ? 8 : 4;
                if (length > data.Length - offset) throw Invalid("Truncated protobuf manifest");
                bytes = data.Slice(offset, (int)length); offset += (int)length;
            }
            else throw Invalid("Unsupported protobuf wire type");
            visit(field, bytes);
            if (++count > 100000) throw Invalid("Repository manifest contains too many fields");
        }
    }

    private static byte[] ExpandGzip(byte[] body)
    {
        if (body.Length < 18 || body[2] != 8 || (body[3] & 0xe0) != 0) throw Invalid("Repository gzip manifest could not be decoded");
        var offset = 10; var flags = body[3];
        void Need(int count) { if (count < 0 || offset + count > body.Length) throw Invalid("Repository gzip manifest is truncated or has trailing data"); }
        if ((flags & 4) != 0) { Need(2); var length = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset)); offset += 2; Need(length); offset += length; }
        foreach (var flag in new[] { 8, 16 }) if ((flags & flag) != 0) { do { Need(1); } while (body[offset++] != 0); }
        if ((flags & 2) != 0)
        {
            Need(2); if ((ushort)Crc(body.AsSpan(0, offset)) != BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset))) throw Invalid("Repository gzip manifest could not be decoded");
            offset += 2;
        }
        try
        {
            // The one-byte fence prevents DeflateStream read-ahead from hiding trailing gzip members/data.
            using var input = new GzipFence(body, offset); using var inflater = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var expanded = new MemoryStream(); var buffer = new byte[32768];
            while (true)
            {
                var count = inflater.Read(buffer, 0, Math.Min(buffer.Length, MaximumBytes + 1 - (int)expanded.Length));
                if (count == 0) break; expanded.Write(buffer, 0, count);
                if (expanded.Length > MaximumBytes) throw Invalid("Expanded repository manifest exceeds 3 MiB");
            }
            offset = input.Offset;
            if (body.Length - offset != 8) throw Invalid("Repository gzip manifest is truncated or has trailing data");
            var result = expanded.ToArray();
            if (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)) != Crc(result)
                || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 4)) != (uint)result.Length)
                throw Invalid("Repository gzip manifest could not be decoded");
            return result;
        }
        catch (InvalidDataException error) when (!error.Message.StartsWith("Repository", StringComparison.Ordinal) && !error.Message.StartsWith("Expanded", StringComparison.Ordinal))
        { throw Invalid("Repository gzip manifest could not be decoded"); }
    }

    private sealed class GzipFence(byte[] bytes, int offset) : Stream
    {
        public int Offset { get; private set; } = offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => Offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int start, int count)
        {
            if (count == 0 || Offset >= bytes.Length) return 0;
            buffer[start] = bytes[Offset++]; return 1;
        }
        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0 || Offset >= bytes.Length) return 0;
            buffer[0] = bytes[Offset++]; return 1;
        }
        public override void Flush() { }
        public override long Seek(long value, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
    }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var value = uint.MaxValue; foreach (var next in bytes) value = CrcTable[(value ^ next) & 255] ^ (value >> 8); return ~value;
    }
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < 256; index++) { var value = index; for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1; table[index] = value; }
        return table;
    }
    private static InvalidDataException Invalid(string message) => new(message);
}
