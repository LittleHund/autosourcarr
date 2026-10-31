using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoSourcarr.Core.Repair.Runner.Isolation;

public sealed record VmBudget(int MemoryMiB = 3072, int Processors = 2, long ScratchBytes = 8L * 1024 * 1024 * 1024)
{
    public void Validate()
    {
        if (MemoryMiB is < 512 or > 4096 || Processors is < 1 or > 4
            || ScratchBytes is < 67108864 or > 8589934592)
            throw new ArgumentOutOfRangeException(nameof(MemoryMiB), "Invalid bounded repair VM budget.");
    }
}

public sealed record VmLaunchPlan(string Executable, IReadOnlyList<string> Arguments, string Workspace, string ProofNonce)
{
    public static VmLaunchPlan Create(string jobDirectory, GuestAssets assets, VmBudget budget)
    {
        budget.Validate();
        var root = Path.GetFullPath(jobDirectory);
        var leaf = Path.GetFileName(root);
        if (leaf.Length != 32 || leaf.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || Path.GetFileName(Path.GetDirectoryName(root)) != "vm-jobs"
            || Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(root))) != "source-maintenance")
            throw new ArgumentException("Repair workspace is outside its dedicated job scope.", nameof(jobDirectory));
        if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null)
            throw new InvalidDataException("Repair workspace must be an existing private directory.");

        var proof = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var scratch = Path.Combine(root, "scratch.raw");
        var arguments = new List<string>
        {
            root, proof, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--", "-no-user-config", "-nodefaults",
            "-machine", "microvm,pit=off,pic=off,rtc=off,acpi=off",
            "-accel", "kvm", "-cpu", "host,-vmx,-svm",
            "-m", budget.MemoryMiB.ToString(CultureInfo.InvariantCulture),
            "-smp", budget.Processors.ToString(CultureInfo.InvariantCulture),
            "-display", "none", "-monitor", "none", "-serial", "null", "-nic", "none", "-no-reboot",
            "-kernel", assets.Kernel,
            "-append", "root=/dev/vda ro rootfstype=ext4 console=ttyS0 quiet panic=-1 init=/sbin/autosourcarr-guest-init",
            "-sandbox", "on,obsolete=deny,elevateprivileges=deny,spawn=deny,resourcecontrol=deny",
            "-chardev", "stdio,id=repair,signal=off",
            "-device", "virtio-serial-device",
            "-device", "virtserialport,chardev=repair,name=org.autosourcarr.repair"
        };
        AddDisk(arguments, assets.RootDisk, "os", readOnly: true);
        AddDisk(arguments, scratch, "work", readOnly: false);
        return new("/opt/autosourcarr-native/repair-launcher", arguments, root, proof);
    }

    public ProcessStartInfo StartInfo()
    {
        if (Arguments.Count < 4 || Arguments[2] != Environment.ProcessId.ToString(CultureInfo.InvariantCulture) || Arguments[3] != "--")
            throw new InvalidDataException("The repair launch plan is not bound to the current parent process");
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Workspace,
            CreateNoWindow = true
        };
        foreach (var argument in Arguments)
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["LANG"] = "C.UTF-8";
        start.Environment["TZ"] = "UTC";
        start.Environment["HOME"] = "/nonexistent";
        start.Environment["TMPDIR"] = Workspace;
        return start;
    }

    private static void AddDisk(List<string> arguments, string filename, string name, bool readOnly)
    {
        // JSON keeps commas and other path characters out of QEMU's suboption grammar.
        arguments.AddRange(["-blockdev", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["driver"] = "file", ["node-name"] = name + "-file", ["filename"] = filename,
            ["read-only"] = readOnly, ["aio"] = "threads"
        }), "-blockdev", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["driver"] = "raw", ["node-name"] = name, ["file"] = name + "-file", ["read-only"] = readOnly
        }), "-device", "virtio-blk-device,drive=" + name]);
    }
}
