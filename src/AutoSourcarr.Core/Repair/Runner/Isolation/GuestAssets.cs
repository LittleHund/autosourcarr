using System.Security.Cryptography;
using System.Text.Json;

namespace AutoSourcarr.Core.Repair.Runner.Isolation;

public sealed class GuestAssets
{
    private GuestAssets(string root, string kernel, string disk, string imageHash)
    {
        Root = root;
        Kernel = kernel;
        RootDisk = disk;
        ImageHash = imageHash;
    }

    public string Root { get; }
    public string Kernel { get; }
    public string RootDisk { get; }
    public string ImageHash { get; }

    public static GuestAssets Load(string imageOwnedRoot)
    {
        var root = Path.GetFullPath(imageOwnedRoot);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The repair guest directory cannot be a link.");
        var manifest = Path.Combine(root, "guest-assets.json");
        if (new FileInfo(manifest).Length > 4096 || new FileInfo(manifest).LinkTarget is not null)
            throw new InvalidDataException("Invalid image-owned repair manifest.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(manifest), new() { MaxDepth = 8 });
        if (json.RootElement.GetProperty("format").GetInt32() != 1)
            throw new InvalidDataException("Unsupported repair guest manifest.");
        var kernel = Verify(root, "kernel", json.RootElement.GetProperty("kernelSha256").GetString());
        var disk = Verify(root, "rootfs.raw", json.RootElement.GetProperty("rootfsSha256").GetString());
        return new(root, kernel, disk, json.RootElement.GetProperty("rootfsSha256").GetString()!);
    }

    private static string Verify(string root, string name, string? expected)
    {
        if (expected is null || expected.Length != 64 || expected.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Invalid repair guest asset identity.");
        var path = Path.Combine(root, name);
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("Repair guest assets cannot be links.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexStringLower(SHA256.HashData(file));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Repair guest asset identity changed; repair remains disabled.");
        return path;
    }
}
