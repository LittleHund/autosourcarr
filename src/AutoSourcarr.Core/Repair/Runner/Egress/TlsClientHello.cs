using System.Buffers.Binary;
using System.Text;

namespace AutoSourcarr.Core.Repair.Runner.Egress;

/// <summary>
/// Phase 13: reads the server name (SNI) from the first TLS record a client sends through a CONNECT tunnel, so the
/// egress proxy can refuse a tunnel whose TLS handshake names another site than the CONNECT host (domain fronting).
/// Only a ClientHello that fits one TLS record is accepted (true for every real client hello, about 0.5–2 KB).
/// </summary>
public static class TlsClientHello
{
    /// <summary>The TLS record header (5 bytes) plus at most this much body is read.</summary>
    public const int MaximumRecordBytes = 16 * 1024;

    /// <summary>Total length of the first record (header included) once the 5-byte header is known; null when not a TLS handshake record.</summary>
    public static int? RecordLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < 5 || header[0] != 0x16 || header[1] != 0x03) return null;
        var length = BinaryPrimitives.ReadUInt16BigEndian(header[3..5]);
        return length is 0 or > MaximumRecordBytes ? null : 5 + length;
    }

    /// <summary>The host_name of the server_name extension, or null when the record is no ClientHello or names no host.</summary>
    public static string? ServerName(ReadOnlySpan<byte> record)
    {
        if (RecordLength(record) is not { } total || record.Length < total) return null;
        var body = record[5..total];
        if (body.Length < 4 || body[0] != 0x01) return null;
        var helloLength = (body[1] << 16) | (body[2] << 8) | body[3];
        if (helloLength > body.Length - 4) return null; // fragmented over several records: refused
        var hello = body.Slice(4, helloLength);
        var position = 2 + 32; // client_version, random
        if (!Skip(hello, ref position, 1)) return null; // session_id
        if (!Skip(hello, ref position, 2)) return null; // cipher_suites
        if (!Skip(hello, ref position, 1)) return null; // compression_methods
        if (position + 2 > hello.Length) return null;
        var extensionsEnd = position + 2 + BinaryPrimitives.ReadUInt16BigEndian(hello[position..]);
        position += 2;
        if (extensionsEnd > hello.Length) return null;
        while (position + 4 <= extensionsEnd)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(hello[position..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(hello[(position + 2)..]);
            position += 4;
            if (position + length > extensionsEnd) return null;
            if (type == 0)
            {
                var extension = hello.Slice(position, length);
                if (extension.Length < 2) return null;
                var listEnd = 2 + BinaryPrimitives.ReadUInt16BigEndian(extension);
                if (listEnd > extension.Length) return null;
                var item = 2;
                while (item + 3 <= listEnd)
                {
                    var nameType = extension[item];
                    var nameLength = BinaryPrimitives.ReadUInt16BigEndian(extension[(item + 1)..]);
                    item += 3;
                    if (item + nameLength > listEnd) return null;
                    if (nameType == 0)
                    {
                        var name = extension.Slice(item, nameLength);
                        foreach (var value in name) if (value is < 0x21 or > 0x7e) return null;
                        return Encoding.ASCII.GetString(name);
                    }
                    item += nameLength;
                }
                return null;
            }
            position += length;
        }
        return null;
    }

    private static bool Skip(ReadOnlySpan<byte> data, ref int position, int lengthBytes)
    {
        if (position + lengthBytes > data.Length) return false;
        var length = lengthBytes == 1 ? data[position] : BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
        position += lengthBytes + length;
        return position <= data.Length;
    }
}
