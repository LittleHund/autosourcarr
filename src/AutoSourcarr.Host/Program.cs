using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AutoSourcarr.Core.Settings;
using Microsoft.Win32.SafeHandles;

namespace AutoSourcarr.Host;

internal static partial class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return Refuse("The native startup resolver requires Linux x86_64.");
        if (args.Length != 1 || args[0] != "--resolve-native-startup")
            return Refuse("Invalid startup resolver invocation.");
        return Resolve();
    }

    [SupportedOSPlatform("linux")]
    private static int Resolve()
    {
        try
        {
            // The one-shot native parent must drop ALL credentials/groups and
            // capabilities before CLR/configuration code is allowed to execute.
            if (GetResUid(out var realUid, out var effectiveUid, out var savedUid) != 0
                || GetResGid(out var realGid, out var effectiveGid, out var savedGid) != 0
                || realUid != 568 || effectiveUid != 568 || savedUid != 568
                || realGid != 568 || effectiveGid != 568 || savedGid != 568
                || GetGroups(0, IntPtr.Zero) != 0 || Prctl(39, 0, 0, 0, 0) != 1)
                throw new InvalidOperationException("Unprivileged startup identity was not established.");
            var header = new CapabilityHeader { Version = 0x20080522 };
            if (CapGet(ref header, out var caps) != 0 || caps.Effective0 != 0 || caps.Permitted0 != 0
                || caps.Inheritable0 != 0 || caps.Effective1 != 0 || caps.Permitted1 != 0 || caps.Inheritable1 != 0)
                throw new InvalidOperationException("Startup capabilities were not cleared.");
            // Normal ELF exec resets dumpability; restore privacy in this CLR
            // process before any environment/configuration value is loaded.
            if (Prctl(4, 0, 0, 0, 0) != 0 || Prctl(3, 0, 0, 0, 0) != 0)
                throw new InvalidOperationException("Startup process privacy was not established.");
            RequirePipe(3);
            RequirePipe(4);
            using var inputHandle = new SafeFileHandle((IntPtr)3, ownsHandle: true);
            using var outputHandle = new SafeFileHandle((IntPtr)4, ownsHandle: true);
            using var input = new FileStream(inputHandle, FileAccess.Read);
            using var output = new FileStream(outputHandle, FileAccess.Write);
            var challenge = new byte[65];
            input.ReadExactly(challenge);
            if (challenge[64] != '\n' || input.ReadByte() != -1 || challenge.Take(64).Any(value => value > 127))
                throw new InvalidDataException("Invalid native bootstrap challenge.");
            var nonce = Encoding.ASCII.GetString(challenge, 0, 64);
            // Validate before touching any supplied configuration or environment.
            _ = new SourceStartupRequest("external", 0, 0).Encode(nonce);
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables())
                environment[(string)pair.Key] = pair.Value?.ToString();
            var snapshot = StartupConfigurationLoader.Load(Environment.CurrentDirectory, environment, bundledImage: true);
            var frame = snapshot.SourceRequest.Encode(nonce);
            output.Write(frame);
            output.Flush();
            return 0;
        }
        catch (Exception error)
        {
            // A wrong-field value or environment file can contain credentials.
            // Neither their values nor exception messages reach the native pipe.
            return Refuse("Startup configuration could not be resolved (" + error.GetType().Name + ").");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void RequirePipe(int descriptor)
    {
        if (Statx(descriptor, "", 0x1000, 0x201, out var status) != 0
            || (status.Mode & 0xF000) != 0x1000)
            throw new InvalidOperationException("The startup resolver requires inherited anonymous pipes.");
    }

    private static int Refuse(string message)
    {
        Console.Error.WriteLine(message);
        return 78;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CapabilityHeader { public uint Version; public int Pid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CapabilityPair
    {
        public uint Effective0, Permitted0, Inheritable0;
        public uint Effective1, Permitted1, Inheritable1;
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileStatus { [FieldOffset(28)] public ushort Mode; }

    [LibraryImport("libc", EntryPoint = "getresuid", SetLastError = true)]
    private static partial int GetResUid(out uint real, out uint effective, out uint saved);
    [LibraryImport("libc", EntryPoint = "getresgid", SetLastError = true)]
    private static partial int GetResGid(out uint real, out uint effective, out uint saved);
    [LibraryImport("libc", EntryPoint = "getgroups", SetLastError = true)]
    private static partial int GetGroups(int size, IntPtr groups);
    [LibraryImport("libc", EntryPoint = "capget", SetLastError = true)]
    private static partial int CapGet(ref CapabilityHeader header, out CapabilityPair capabilities);
    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static partial int Prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out FileStatus status);
}
