using System.Security.Cryptography;
using System.Text;

namespace AutoSourcarr.Core.Catalogue.Sharing;

/// <summary>RFC 4122 namespace UUIDs, using network order exactly as Python uuid.uuid5.</summary>
public static class PortableUuid
{
    public static Guid V5(Guid ns, string name)
    {
        var namespaceBytes = ns.ToByteArray(bigEndian: true);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input, 0); nameBytes.CopyTo(input, namespaceBytes.Length);
        var hash = SHA1.HashData(input);
        hash[6] = (byte)((hash[6] & 15) | 80); hash[8] = (byte)((hash[8] & 63) | 128);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
    public static string Entry(string parent, string name) => "asc1:entry:autosourcarr:" + V5(Guid.Parse(parent.Split(':')[^1]), name).ToString("D");
}
