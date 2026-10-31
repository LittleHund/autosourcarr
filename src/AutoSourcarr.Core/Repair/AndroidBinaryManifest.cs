using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AutoSourcarr.Core.Repair;

/// <summary>Android resource XML reader for the retained APK's manifest only.</summary>
internal static class AndroidBinaryManifest
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);
    private static readonly Regex XmlName = new(@"\A[A-Za-z_][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant);
    public static XElement Parse(byte[] data) => new Reader(data).Parse();

    private sealed class Reader(byte[] data)
    {
        private List<string>? _strings;
        private readonly Stack<XElement> _stack = [];
        private XElement? _root;
        private int _elements;
        private long _attributeCharacters;

        public XElement Parse()
        {
            var kind = U16(0, data.Length); var header = U16(2, data.Length); var size = U32(4, data.Length);
            if (kind != 3 || header != 8 || size != data.Length) throw Invalid("Unknown or incomplete binary extension manifest");
            var offset = (int)header;
            while (offset < data.Length)
            {
                kind = U16(offset, data.Length); header = U16(offset + 2, data.Length); var chunkSize = U32(offset + 4, data.Length);
                if (header < 8 || chunkSize < header || chunkSize > data.Length - offset) throw Invalid("Invalid binary manifest chunk");
                var end = checked(offset + (int)chunkSize);
                switch (kind)
                {
                    case 1: ReadStrings(offset, header, end); break;
                    case 0x180:
                        if (_strings is null || header != 8 || (chunkSize - header) % 4 != 0) throw Invalid("Invalid binary manifest resource map");
                        break;
                    case 0x100 or 0x101 or 0x102 or 0x103:
                        if (_strings is null || header != 16) throw Invalid("Invalid binary manifest XML node");
                        ReadNode(kind, offset + header, end); break;
                    default: throw Invalid("Unsupported binary manifest chunk");
                }
                offset = end;
            }
            if (_root is null || _stack.Count != 0) throw Invalid("Incomplete binary manifest elements");
            return _root;
        }

        private void ReadStrings(int offset, int header, int end)
        {
            if (_strings is not null || _root is not null || header < 28) throw Invalid("Invalid binary manifest string pool");
            var count = U32(offset + 8, offset + header); var styles = U32(offset + 12, offset + header);
            var flags = U32(offset + 16, offset + header); var start = U32(offset + 20, offset + header); var stylesStart = U32(offset + 24, offset + header);
            if (count > 10000 || styles > 10000 || header + ((long)count + styles) * 4 > start || start > end - offset)
                throw Invalid("Invalid binary manifest string offsets");
            var stringEndLong = offset + (stylesStart == 0 ? (long)end - offset : stylesStart);
            if (offset + (long)start > stringEndLong || stringEndLong > end) throw Invalid("Invalid binary manifest string boundary");
            var stringEnd = checked((int)stringEndLong); var tableEnd = checked(offset + (int)start);
            var decoded = new Dictionary<uint, string>(); long decodedUnits = 0;
            _strings = new List<string>((int)count);
            for (var index = 0; index < count; index++)
            {
                var relative = U32(offset + header + index * 4, tableEnd);
                if (decoded.TryGetValue(relative, out var duplicate)) { _strings.Add(duplicate); continue; }
                if (relative >= stringEnd - tableEnd) throw Invalid("Invalid binary manifest string location");
                var position = checked(tableEnd + (int)relative); var utf8 = (flags & 0x100) != 0;
                var units = Length(ref position, utf8 ? 1 : 2, stringEnd); decodedUnits += units;
                if (decodedUnits > SourceManifest.MaximumExpandedCharacters) throw Invalid("Binary manifest decoded strings exceed their limit");
                string value;
                if (utf8)
                {
                    var bytes = Length(ref position, 1, stringEnd);
                    if (bytes > stringEnd - position - 1 || data[position + bytes] != 0) throw Invalid("Unterminated binary manifest string");
                    value = Utf8.GetString(Field(position, bytes, stringEnd));
                    if (value.Length != units) throw Invalid("Invalid binary manifest UTF8 length");
                }
                else
                {
                    if ((long)units * 2 + 2 > stringEnd - position) throw Invalid("Unterminated binary manifest string");
                    var bytes = checked(units * 2);
                    if (U16(position + bytes, stringEnd) != 0) throw Invalid("Unterminated binary manifest string");
                    value = Utf16.GetString(Field(position, bytes, stringEnd));
                }
                if (value.Contains('\0')) throw Invalid("Invalid binary manifest string");
                _strings.Add(value); decoded.Add(relative, value);
            }
        }

        private void ReadNode(int kind, int extension, int end)
        {
            if (kind is 0x100 or 0x101)
            {
                _ = String(U32(extension, end), optional: true); _ = String(U32(extension + 4, end)); return;
            }
            var name = Qualified(U32(extension, end), U32(extension + 4, end));
            if (kind == 0x103)
            {
                if (_stack.Count == 0 || _stack.Peek().Name != name) throw Invalid("Unbalanced binary manifest elements");
                _stack.Pop(); return;
            }
            var start = U16(extension + 8, end); var step = U16(extension + 10, end); var count = U16(extension + 12, end);
            _ = U16(extension + 18, end);
            if (step < 20 || count > SourceManifest.MaximumAttributes || start < 20 || extension + (long)start + (long)count * step > end)
                throw Invalid("Invalid binary manifest attributes");
            var element = new XElement(name);
            for (var index = 0; index < count; index++)
            {
                var position = extension + start + index * step;
                var key = Qualified(U32(position, end), U32(position + 4, end)); var raw = U32(position + 8, end);
                var valueSize = U16(position + 12, end); var reserved = U8(position + 14, end); var valueType = U8(position + 15, end); var value = U32(position + 16, end);
                if (valueSize != 8 || reserved != 0) throw Invalid("Invalid binary manifest typed value");
                if (element.Attribute(key) is not null) throw Invalid("Duplicate binary manifest attribute");
                var text = raw != uint.MaxValue ? String(raw) : valueType switch
                {
                    3 => String(value),
                    0x10 or 0x11 => value.ToString(CultureInfo.InvariantCulture),
                    4 => Float(value),
                    0x12 => value == 0 ? "false" : "true",
                    >= 0x1c and <= 0x1f => "#" + value.ToString("x8", CultureInfo.InvariantCulture),
                    1 or 2 => (valueType == 1 ? "@" : "?") + "0x" + value.ToString("x8", CultureInfo.InvariantCulture),
                    _ => throw Invalid("Unsupported binary manifest typed value")
                };
                _attributeCharacters += SourceManifest.CodePoints(key.ToString()) + SourceManifest.CodePoints(text);
                if (_attributeCharacters > SourceManifest.MaximumExpandedCharacters) throw Invalid("Binary manifest expanded attributes exceed their limit");
                element.Add(new XAttribute(key, text));
            }
            if (++_elements > SourceManifest.MaximumElements || _stack.Count >= SourceManifest.MaximumDepth) throw Invalid("Binary manifest tree exceeds its limit");
            if (_stack.Count > 0) _stack.Peek().Add(element);
            else if (_root is null) _root = element;
            else throw Invalid("Multiple binary manifest roots");
            _stack.Push(element);
        }

        private static string Float(uint bits)
        {
            var number = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            if (!float.IsFinite(number)) throw Invalid("Invalid binary manifest float");
            var text = "";
            for (var precision = 1; precision <= 9; precision++)
            {
                text = ((double)number).ToString("G" + precision, CultureInfo.InvariantCulture).Replace('E', 'e');
                if (BitConverter.SingleToInt32Bits(float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)) == unchecked((int)bits)) break;
            }
            return text.Contains('.') || text.Contains('e') ? text : text + ".0";
        }

        private string String(uint index, bool optional = false)
        {
            if (optional && index == uint.MaxValue) return "";
            return _strings is not null && index < _strings.Count ? _strings[(int)index] : throw Invalid("Invalid binary manifest string index");
        }
        private XName Qualified(uint uriIndex, uint nameIndex)
        {
            var name = String(nameIndex);
            if (!XmlName.IsMatch(name)) throw Invalid("Invalid binary manifest XML name");
            return XName.Get(name, String(uriIndex, optional: true));
        }
        private int Length(ref int position, int width, int end)
        {
            int value = width == 1 ? U8(position, end) : U16(position, end); position += width;
            var high = width == 1 ? 0x80 : 0x8000;
            if ((value & high) != 0)
            {
                var second = width == 1 ? U8(position, end) : U16(position, end); position += width;
                value = ((value & (high - 1)) << (8 * width)) | second;
            }
            return value;
        }
        private ReadOnlySpan<byte> Field(int offset, int length, int end) => offset >= 0 && length >= 0 && end <= data.Length && offset <= end - length
            ? data.AsSpan(offset, length) : throw Invalid("Incomplete binary extension manifest");
        private byte U8(int offset, int end) => Field(offset, 1, end)[0];
        private ushort U16(int offset, int end) => BinaryPrimitives.ReadUInt16LittleEndian(Field(offset, 2, end));
        private uint U32(int offset, int end) => BinaryPrimitives.ReadUInt32LittleEndian(Field(offset, 4, end));
    }
    private static InvalidDataException Invalid(string message) => SourceManifest.Invalid(message);
}
