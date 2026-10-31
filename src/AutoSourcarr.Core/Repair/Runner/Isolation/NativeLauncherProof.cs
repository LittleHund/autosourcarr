using System.Text;
using System.Text.Json;

namespace AutoSourcarr.Core.Repair.Runner.Isolation;

/// <summary>
/// Evidence emitted by the fixed native launcher before it execs QEMU. A guest message
/// cannot substitute for this host-side check. It is not proof of a successful guest boot.
/// </summary>
public sealed class NativeLauncherProof
{
    private NativeLauncherProof(int processId, uint userId, int parentProcessId, int landlockAbi)
    { ProcessId = processId; UserId = userId; ParentProcessId = parentProcessId; LandlockAbi = landlockAbi; }
    public int ProcessId { get; }
    public uint UserId { get; }
    public int ParentProcessId { get; }
    public int ParentDeathSignal => 9;
    public int LandlockAbi { get; }
    public const int MaximumLineBytes = 4096;
    private const string Prefix = "AUTOSOURCARR_SANDBOX_V2 ";
    private static readonly HashSet<string> Fields = ["pid", "uid", "ppid", "pdeathsig", "landlock", "nnp", "seccomp"];

    public static Task<NativeLauncherProof> ReadAsync(Stream standardError, int expectedProcessId,
        uint expectedUserId, string expectedNonce, CancellationToken cancellationToken) =>
        ReadAsync(standardError, expectedProcessId, expectedUserId, Environment.ProcessId, expectedNonce, cancellationToken);

    internal static async Task<NativeLauncherProof> ReadAsync(Stream standardError, int expectedProcessId,
        uint expectedUserId, int expectedParentProcessId, string expectedNonce, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        if (expectedProcessId <= 1 || expectedUserId == 0 || expectedParentProcessId < 1 || expectedParentProcessId == expectedProcessId || expectedNonce.Length != 64
            || expectedNonce.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid expected native launcher identity.");
        // Do not prefetch QEMU diagnostics into a discarded StreamReader buffer.
        var line = new byte[MaximumLineBytes];
        var one = new byte[1];
        var length = 0;
        while (true)
        {
            if (await standardError.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                throw new EndOfStreamException("The repair launcher closed without isolation evidence.");
            if (one[0] == (byte)'\n') break;
            if (length == line.Length || one[0] is < 0x20 or > 0x7e)
                throw new InvalidDataException("The repair launcher's isolation evidence is invalid or oversized.");
            line[length++] = one[0];
        }
        var text = Encoding.ASCII.GetString(line, 0, length);
        var expectedPrefix = Prefix + expectedNonce + " ";
        if (!text.StartsWith(expectedPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("The native repair launcher's session identity did not match.");
        try
        {
            using var document = JsonDocument.Parse(text[expectedPrefix.Length..], new() { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid isolation evidence shape.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Fields.Contains(property.Name) || !seen.Add(property.Name))
                    throw new InvalidDataException("Ambiguous native isolation evidence.");
            if (seen.Count != Fields.Count || !Integer(root, "pid", out var pid) || pid != expectedProcessId
                || !Integer(root, "uid", out var uid) || uid != expectedUserId
                || !Integer(root, "ppid", out var parent) || parent != expectedParentProcessId
                || !Integer(root, "pdeathsig", out var signal) || signal != 9
                || !Integer(root, "landlock", out var abi) || abi is < 6 or > int.MaxValue
                || !Integer(root, "nnp", out var nnp) || nnp != 1
                || !Integer(root, "seccomp", out var seccomp) || seccomp != 2)
                throw new InvalidDataException("The native repair launcher did not establish the required restrictions.");
            return new((int)pid, (uint)uid, (int)parent, (int)abi);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid native repair isolation evidence.", error); }
    }

    private static bool Integer(JsonElement value, string name, out long result)
    {
        result = 0;
        return value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out result);
    }
}
