using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoSourcarr.Core.Repair.Runner.Isolation;
using AutoSourcarr.Core.Repair.Runner.Protocol;

namespace AutoSourcarr.Tests;

public static class RepairIsolationTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("repair: unsupported local platform cannot enable source execution", () =>
        {
            if (!OperatingSystem.IsLinux())
            {
                var capabilities = new NativeIsolationProbe().Inspect();
                Check.True(!capabilities.Supported);
                Check.Equal("hardware-repair-requires-linux-x64", capabilities.Reason);
            }
        });
        suite.Add("repair: asset tampering blocks use of the guest", () =>
        {
            using var temp = new Sandbox();
            var assets = Assets(temp.Path);
            Check.Equal(64, assets.ImageHash.Length);
            File.AppendAllText(assets.RootDisk, "tampered");
            Check.Throws<InvalidDataException>(() => GuestAssets.Load(assets.Root));
        });
        suite.Add("repair: launch permits KVM only and exposes no host network or shared folders", () =>
        {
            using var temp = new Sandbox();
            var assets = Assets(Path.Combine(temp.Path, "guest,comma=injection"));
            var job = Path.Combine(temp.Path, "source-maintenance", "vm-jobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            var plan = VmLaunchPlan.Create(job, assets, new());
            var args = plan.Arguments.ToArray();
            Check.Equal("kvm", args[Array.IndexOf(args, "-accel") + 1]);
            Check.Equal("none", args[Array.IndexOf(args, "-nic") + 1]);
            Check.Equal("none", args[Array.IndexOf(args, "-monitor") + 1]);
            Check.True(!args.Any(arg => arg.Contains("tcg", StringComparison.OrdinalIgnoreCase)
                || arg.Contains("virtiofs", StringComparison.OrdinalIgnoreCase)
                || arg.Contains("9p", StringComparison.OrdinalIgnoreCase)));
            var start = plan.StartInfo();
            Check.True(!start.UseShellExecute);
            Check.Equal(4, start.Environment.Count);
            Check.True(!start.Environment.ContainsKey("REPAIR_AI_API_KEY"));
            var disks = new List<JsonElement>();
            for (var i = 0; i < args.Length; i++)
                if (args[i] == "-blockdev")
                    disks.Add(JsonSerializer.Deserialize<JsonElement>(args[i + 1]));
            Check.Equal(assets.RootDisk, disks[0].GetProperty("filename").GetString());
            Check.True(disks[0].GetProperty("read-only").GetBoolean());
            Check.Equal(Path.Combine(job, "scratch.raw"), disks[2].GetProperty("filename").GetString());
            Check.True(!disks[2].GetProperty("read-only").GetBoolean());
        });
        suite.Add("repair: arbitrary workspace and unbounded resource requests are rejected", () =>
        {
            using var temp = new Sandbox();
            var assets = Assets(Path.Combine(temp.Path, "guest"));
            Check.Throws<ArgumentException>(() => VmLaunchPlan.Create(temp.Path, assets, new()));
            Check.Throws<ArgumentOutOfRangeException>(() => new VmBudget(MemoryMiB: int.MaxValue).Validate());
            Check.Throws<ArgumentOutOfRangeException>(() => new VmBudget(ScratchBytes: long.MaxValue).Validate());
        });
        suite.Add("repair protocol: frame round trip retains opaque IDs and Unicode", () =>
        {
            using var bytes = new MemoryStream();
            using (var writer = new GuestChannel(Stream.Null, bytes))
                writer.WriteAsync(JsonSerializer.SerializeToElement(new { type = "result", id = "9007199254740993", payload = new { title = "保持", url = "/%2F?Case=X" } }), default).GetAwaiter().GetResult();
            bytes.Position = 0;
            using var reader = new GuestChannel(bytes, Stream.Null);
            using var frame = reader.ReadAsync(default).GetAwaiter().GetResult();
            Check.Equal("9007199254740993", frame.RootElement.GetProperty("id").GetString());
            Check.Equal("保持", frame.RootElement.GetProperty("payload").GetProperty("title").GetString());
        });
        suite.Add("repair protocol: oversize, corrupt UTF8 and ambiguous frames are refused", () =>
        {
            using (var bytes = new MemoryStream())
            {
                var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, GuestChannel.MaximumFrameBytes + 1);
                bytes.Write(header);
                bytes.Position = 0;
                using var channel = new GuestChannel(bytes, Stream.Null);
                Check.Throws<InvalidDataException>(() => channel.ReadAsync(default).GetAwaiter().GetResult());
            }
            foreach (var payload in new[]
            {
                Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"type\":\"result\"}"),
                Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"unexpected\":true}"),
                new byte[] { (byte)'{', (byte)'\"', 0xff, (byte)'\"', (byte)':', (byte)'0', (byte)'}' }
            })
            {
                using var bytes = Framed(payload);
                using var channel = new GuestChannel(bytes, Stream.Null);
                Check.Throws<Exception>(() => channel.ReadAsync(default).GetAwaiter().GetResult());
            }
        });
    }

    private static GuestAssets Assets(string root)
    {
        Directory.CreateDirectory(root);
        var kernel = Encoding.UTF8.GetBytes("synthetic trusted kernel test input");
        var disk = Encoding.UTF8.GetBytes("synthetic trusted root filesystem test input");
        File.WriteAllBytes(Path.Combine(root, "kernel"), kernel);
        File.WriteAllBytes(Path.Combine(root, "rootfs.raw"), disk);
        File.WriteAllText(Path.Combine(root, "guest-assets.json"), JsonSerializer.Serialize(new
        {
            format = 1,
            kernelSha256 = Convert.ToHexStringLower(SHA256.HashData(kernel)),
            rootfsSha256 = Convert.ToHexStringLower(SHA256.HashData(disk))
        }));
        return GuestAssets.Load(root);
    }

    private static MemoryStream Framed(byte[] payload)
    {
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }
}
