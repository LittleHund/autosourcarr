using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AutoSourcarr.Core.Repair;

/// <summary>The source loading identity declared by the retained Android manifest.</summary>
public sealed record SourceManifestIdentity(string Package, string ClassName, string LibraryVersion);

/// <summary>Bounded XML/Android binary manifest parsing without executing source code.</summary>
public static class SourceManifest
{
    public const int MaximumBytes = 256 * 1024;
    public const int MaximumElements = 4096;
    public const int MaximumDepth = 64;
    public const int MaximumAttributes = 1024;
    public const int MaximumExpandedCharacters = MaximumBytes * 2;
    public const string AndroidNamespace = "http://schemas.android.com/apk/res/android";
    public static readonly XNamespace Android = AndroidNamespace;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding LittleEndian = new(false, false, true);
    private static readonly UnicodeEncoding BigEndian = new(true, false, true);
    private static readonly Regex Declarations = new(@"<!\s*(?:DOCTYPE|ENTITY)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Qualified = new(@"\A[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)+\z", RegexOptions.CultureInvariant);
    private static readonly Regex Library = new(@"\A[0-9]{1,3}\.[0-9]{1,3}\z", RegexOptions.CultureInvariant);

    public static XElement Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumBytes) throw Invalid("Extension manifest is empty or exceeds its size limit");
        try
        {
            if (bytes.StartsWith(new byte[] { 3, 0 })) return AndroidBinaryManifest.Parse(bytes.ToArray());
            string text;
            if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) text = LittleEndian.GetString(bytes[2..]);
            else if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) text = BigEndian.GetString(bytes[2..]);
            else if (bytes.StartsWith(new byte[] { (byte)'<', 0 })) text = LittleEndian.GetString(bytes);
            else if (bytes.StartsWith(new byte[] { 0, (byte)'<' })) text = BigEndian.GetString(bytes);
            else text = StrictUtf8.GetString(bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes[3..] : bytes);
            if (Declarations.IsMatch(text)) throw Invalid("Extension manifest contains unsupported XML declarations");
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                IgnoreComments = true, IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = MaximumExpandedCharacters, MaxCharactersFromEntities = MaximumExpandedCharacters
            });
            var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root ?? throw Invalid("Extension manifest is not readable");
            ValidateTree(root);
            return root;
        }
        catch (Exception error) when (error is XmlException or DecoderFallbackException or ArgumentException or OverflowException)
        { throw Invalid("Extension manifest is not readable"); }
    }

    public static XElement Read(byte[] package)
    {
        using var archive = CheckedZipArchive.Open(package, SourcePackage.MaximumBytes, SourcePackage.MaximumEntries);
        var info = archive.Entries.SingleOrDefault(entry => entry.FullName == "AndroidManifest.xml")
            ?? throw Invalid("Package has no Android manifest");
        if (info.Length > MaximumBytes) throw Invalid("Extension manifest exceeds the size limit");
        return Parse(archive.ReadEntry(info, MaximumBytes));
    }

    public static SourceManifestIdentity Contract(byte[] package) => Identity(Read(package));

    public static SourceManifestIdentity Identity(XElement root)
    {
        if (root.Name != "manifest" || root.Elements("application").Count() != 1)
            throw Invalid("Extension manifest lacks one package application");
        var package = (string?)root.Attribute("package") ?? "";
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in root.Element("application")!.Elements("meta-data"))
        {
            var name = (string?)element.Attribute(Android + "name");
            if (name is "tachiyomi.extension.class" or "tachiyomix.extensionLib")
            {
                if (!metadata.TryAdd(name, (string?)element.Attribute(Android + "value") ?? ""))
                    throw Invalid("Extension manifest has ambiguous identity metadata");
            }
        }
        var className = metadata.GetValueOrDefault("tachiyomi.extension.class", "").Trim();
        if (className.StartsWith('.')) className = package + className;
        var library = metadata.GetValueOrDefault("tachiyomix.extensionLib", "");
        if (library is "" or "0")
        {
            var version = (string?)root.Attribute(Android + "versionName") ?? "";
            var point = version.LastIndexOf('.'); library = point < 0 ? version : version[..point];
        }
        if (!Qualified.IsMatch(package) || !Qualified.IsMatch(className) || !Library.IsMatch(library))
            throw Invalid("Extension manifest lacks package, source entrypoint or library version");
        return new(package, className, library);
    }

    internal static byte[] XmlBytes(XElement root)
    {
        var copy = new XElement(root);
        if (copy.DescendantsAndSelf().Attributes().Any(attribute => attribute.Name.NamespaceName == AndroidNamespace))
        {
            var current = copy.GetNamespaceOfPrefix("android");
            if (current is null) copy.Add(new XAttribute(XNamespace.Xmlns + "android", AndroidNamespace));
        }
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false, CloseOutput = false }))
            new XDocument(new XDeclaration("1.0", "utf-8", null), copy).Save(writer);
        var bytes = output.ToArray();
        if (bytes.Length > MaximumBytes) throw Invalid("Decoded APK manifest exceeds the native import limit");
        return bytes;
    }

    private static void ValidateTree(XElement root)
    {
        var pending = new Stack<(XElement Element, int Depth)>(); pending.Push((root, 1));
        var count = 0; long expanded = 0;
        while (pending.TryPop(out var item))
        {
            if (++count > MaximumElements || item.Depth > MaximumDepth) throw Invalid("Extension manifest tree exceeds its limit");
            var attributes = item.Element.Attributes().ToArray();
            if (attributes.Length > MaximumAttributes) throw Invalid("Extension manifest attributes exceed their limit");
            foreach (var attribute in attributes)
            {
                expanded += CodePoints(attribute.Name.ToString()) + CodePoints(attribute.Value);
                if (expanded > MaximumExpandedCharacters) throw Invalid("Extension manifest expanded attributes exceed their limit");
            }
            foreach (var child in item.Element.Elements()) pending.Push((child, item.Depth + 1));
        }
    }

    internal static int CodePoints(string text) { var count = 0; foreach (var _ in text.EnumerateRunes()) count++; return count; }
    internal static InvalidDataException Invalid(string message) => new(message);
}
