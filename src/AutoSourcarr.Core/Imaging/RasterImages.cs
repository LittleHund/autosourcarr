using System.Buffers.Binary;
using System.Security;
using ImageMagick;
using ImageMagick.Configuration;

namespace AutoSourcarr.Core.Imaging;

public sealed class ImageAnalysisLimitException(string message) : IOException(message);

public sealed class RasterImage : IDisposable
{
    private readonly MagickImage _image;
    internal RasterImage(MagickImage image) => _image = image;
    public int Width => checked((int)_image.Width);
    public int Height => checked((int)_image.Height);
    public void Dispose() => _image.Dispose();
}

/// <summary>Only bounded raster pixels reach analysis; reader delivery remains streamed.</summary>
public static class RasterImages
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    public const long MaximumPixels = 40_000_000;
    private static readonly Lazy<bool> Initialized = new(Initialize, LazyThreadSafetyMode.ExecutionAndPublication);

    public static RasterImage Open(byte[] bytes, long maximumPixels = MaximumPixels)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new ImageAnalysisLimitException("Image is empty or exceeds the 32 MiB analysis limit");
        maximumPixels = Math.Min(MaximumPixels, maximumPixels);
        var format = Identify(bytes);
        _ = Initialized.Value;
        var image = new MagickImage();
        try
        {
            var settings = new MagickReadSettings { Format = format, FrameIndex = 0, FrameCount = 1, SyncImageWithExifProfile = false };
            image.Ping(bytes, settings);
            CheckDimensions(image, maximumPixels);
            // Native warnings are incomplete/corrupt input, never successful health evidence.
            image.Warning += (_, error) => throw new InvalidDataException("Raster decoding warning: " + error.Message);
            image.Read(bytes, settings);
            CheckDimensions(image, maximumPixels);
            return new(image);
        }
        catch (MagickResourceLimitErrorException error) { image.Dispose(); throw new ImageAnalysisLimitException("Image exceeds the bounded decode budget: " + error.Message); }
        catch (MagickException error) { image.Dispose(); throw new InvalidDataException("Sample raster image could not be decoded completely", error); }
        catch { image.Dispose(); throw; }
    }

    public static void VerifyHealthSample(byte[] bytes)
    {
        using var image = Open(bytes, 16_000_000);
        if (image.Width < 2 || image.Height < 2) throw new InvalidDataException("Placeholder image");
    }

    private static void CheckDimensions(MagickImage image, long maximumPixels)
    {
        if (image.Width < 1 || image.Height < 1 || (ulong)image.Width * image.Height > (ulong)Math.Max(0, maximumPixels))
            throw new ImageAnalysisLimitException("Image exceeds the analysis limit for decoded pixels (at most 40 megapixels)");
    }

    private static MagickFormat Identify(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return MagickFormat.Png;
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return MagickFormat.Jpeg;
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return MagickFormat.Gif;
        if (bytes.StartsWith("BM"u8)) return MagickFormat.Bmp;
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return MagickFormat.WebP;
        if (bytes.Length >= 16 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var boxBytes = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            if (boxBytes >= 16 && boxBytes <= Math.Min(bytes.Length, 4096))
                for (var at = 8; at + 4 <= boxBytes; at += at == 8 ? 8 : 4)
                    if (bytes.Slice(at, 4).SequenceEqual("avif"u8) || bytes.Slice(at, 4).SequenceEqual("avis"u8)) return MagickFormat.Avif;
        }
        throw new InvalidDataException("Unsupported or invalid raster image");
    }

    private static bool Initialize()
    {
        var scratch = Directory.CreateTempSubdirectory("asr-").FullName;
        // .NET can write extended-length paths that the bundled native Windows
        // library cannot read. Reserve space for its longer temporary blob/cache
        // leaves as well as policy.xml; shorten only our private directory name.
        if (OperatingSystem.IsWindows() && Path.Combine(scratch, new string('x', 48)).Length >= 260)
        {
            Directory.Delete(scratch); // Still empty and owned by this initializer.
            throw new InvalidOperationException("The private raster temporary path exceeds the native Windows filename limit.");
        }
        var permittedTemporaryFiles = SecurityElement.Escape(GlobLiteral(scratch.Replace('\\', '/')) + "/magick-*");
        var nativePathRule = OperatingSystem.IsWindows()
            ? "<policy domain=\"path\" rights=\"read|write\" pattern=\"" + SecurityElement.Escape(GlobLiteral(scratch + Path.DirectorySeparatorChar) + "magick-*") + "\"/>" : "";
        MagickNET.SetTempDirectory(scratch);
        var files = ConfigurationFiles.Default;
        files.Policy.Data = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <policymap>
              <policy domain="resource" name="thread" value="2"/>
              <policy domain="resource" name="time" value="30"/>
              <policy domain="resource" name="memory" value="256MiB"/>
              <policy domain="resource" name="map" value="0"/>
              <policy domain="resource" name="disk" value="0"/>
              <policy domain="resource" name="area" value="40MP"/>
              <policy domain="resource" name="width" value="40MP"/>
              <policy domain="resource" name="height" value="40MP"/>
              <policy domain="resource" name="list-length" value="16"/>
              <policy domain="system" name="max-memory-request" value="256MiB"/>
              <policy domain="system" name="max-profile-size" value="4MiB"/>
              <policy domain="delegate" rights="none" pattern="*"/>
              <policy domain="filter" rights="none" pattern="*"/>
              <policy domain="path" rights="none" pattern="*"/>
              <policy domain="path" rights="read|write" pattern="{{permittedTemporaryFiles}}"/>
              {{nativePathRule}}
              <policy domain="module" rights="none" pattern="*"/>
              <policy domain="module" rights="read|write" pattern="{GIF,JPEG,PNG,WEBP,HEIC,BMP}"/>
              <policy domain="coder" rights="none" pattern="*"/>
              <policy domain="coder" rights="read" pattern="{GIF,JPEG,JPG,PNG,PNG8,PNG24,PNG32,PNG48,PNG64,WEBP,AVIF,HEIC,BMP,BMP2,BMP3}"/>
            </policymap>
            """;
        // Fresh app-owned configuration is extracted internally; users run no setup script.
        // The default overload adds another GUID directory below scratch. Keep
        // the already private location explicit and verify actual enforcement.
        MagickNET.Initialize(files, scratch);
        try
        {
            // This is our own public assembly, never user data or a remote URL.
            // Success or a decode error would both mean the path policy failed.
            using var denied = new MagickImage(typeof(RasterImages).Assembly.Location, MagickFormat.Png);
        }
        catch (MagickPolicyErrorException) { return true; }
        catch (MagickException error) { throw new InvalidOperationException("The required raster path policy could not be verified.", error); }
        throw new InvalidOperationException("The required raster path policy is not active.");
    }
    // ImageMagick's Windows glob matcher treats a leading backslash literally,
    // rather than as a general glob escape. Character classes preserve paths
    // containing braces/brackets (including Windows sandbox temp directories).
    // Four backslashes survive the policy tokenizer as two inside the class.
    private static string GlobLiteral(string value) => string.Concat(value.Select(character => character switch
    {
        '\\' => @"[\\\\]",
        '*' => "[*]", '?' => "[?]", '[' => "[[]", '{' => "[{]",
        _ => character.ToString()
    }));
}
