using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoSourcarr.Core.Repair.Runner.Isolation;

namespace AutoSourcarr.Core.Repair.Runner.Protocol;

public enum HardwareGuestOperation { InspectPackage, BuildProject, ValidatePackage }

/// <summary>A structured report from the hash-verified guest; never substitutes for host launcher proof.</summary>
public sealed record GuestSelfTestReport(string Challenge, long ScratchBytes, uint EffectiveUserId, int MemoryMiB, int Processors);

/// <summary>
/// Protocol1 contract on the private virtio serial channel. The host first verifies native stderr proof.
/// It then sends a fresh hello nonce and its independently verified image SHA; the guest echo binds this
/// conversation and is not an independently embedded rootfs identity. The guest agent must implement
/// self-test-v1 by actually probing EROFS, NIC/share absence, non-root identity and scratch write/read.
/// No image/agent implementation is supplied here. Missing/unsupported replies fail closed.
/// HTTP-open/data/close frames are refused until an independently owned, public-IP-pinned, bounded
/// egress broker is integrated; no host socket, source cookie, authorization header or proxy is inherited.
/// </summary>
internal sealed class GuestConversation(Stream input, Stream output) : IDisposable
{
    private readonly GuestChannel _channel = new(input, output);
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly string _nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private readonly HashSet<string> _capabilities = new(StringComparer.Ordinal);
    private long _sequence;
    private bool _qualified;
    private readonly GuestArtifactQuota _artifactQuota = new();
    private GuestArtifactTransfer? _artifacts;
    private GuestArtifactSession? _artifactSession;
    public GuestArtifactSession ArtifactSession => _qualified && _artifactSession is not null ? _artifactSession : throw new InvalidOperationException("The guest artifact identity is not qualified");
    internal const int MaximumPayloadBytes = 48 * 1024;
    public async Task<GuestSelfTestReport> HandshakeAsync(GuestAssets assets, string jobId, VmBudget budget, CancellationToken token)
    {
        var id = NextId();
        await WriteAsync("hello", id, JsonSerializer.SerializeToElement(new { protocol = 1, agent = "autosourcarr-controller-v1", image_sha256 = assets.ImageHash, job_id = jobId }), token).ConfigureAwait(false);
        using (var hello = await ReadReplyAsync("hello", id, token).ConfigureAwait(false))
        {
            var payload = Payload(hello.RootElement); Exact(payload, ["protocol", "agent", "image_sha256", "capabilities"]);
            if (!Integer(payload, "protocol", 1) || Text(payload, "agent") != "autosourcarr-guest-v1" || Text(payload, "image_sha256") != assets.ImageHash ||
                payload.GetProperty("capabilities").ValueKind != JsonValueKind.Array || payload.GetProperty("capabilities").GetArrayLength() > 16)
                throw new InvalidDataException("The repair guest protocol or image binding did not match");
            foreach (var capability in payload.GetProperty("capabilities").EnumerateArray())
            {
                if (capability.ValueKind != JsonValueKind.String || capability.GetString() is not ("self-test-v1" or "package-inspect-v1" or "project-build-v1" or "package-validate-v1" or "artifact-transfer-v1") || !_capabilities.Add(capability.GetString()!))
                    throw new InvalidDataException("The repair guest advertised unsupported or ambiguous capabilities");
            }
            if (!_capabilities.Contains("self-test-v1")) throw new InvalidDataException("The current repair guest has no required self-test");
        }
        var challenge = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); id = NextId();
        await WriteAsync("request", id, JsonSerializer.SerializeToElement(new { operation = "self-test-v1", arguments = new { challenge, scratch_bytes = budget.ScratchBytes, memory_mib = budget.MemoryMiB, processors = budget.Processors } }), token).ConfigureAwait(false);
        using var response = await ReadReplyAsync("result", id, token).ConfigureAwait(false); var report = Payload(response.RootElement);
        Exact(report, ["protocol", "suite", "challenge", "scratch_bytes", "scratch_roundtrip_sha256", "root_write_errno", "non_loopback_interfaces", "host_share_mounts", "effective_uid", "working_directory", "scratch_device", "memory_mib", "processors"]);
        if (!Integer(report, "protocol", 1) || Text(report, "suite") != "autosourcarr-guest-selftest-v1" || Text(report, "challenge") != challenge ||
            !Integer(report, "scratch_bytes", budget.ScratchBytes) || Text(report, "scratch_roundtrip_sha256") != Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(challenge))) ||
            !Integer(report, "root_write_errno", 30) || !Integer(report, "non_loopback_interfaces", 0) || !Integer(report, "host_share_mounts", 0) ||
            !Integer(report, "effective_uid", 1000) || Text(report, "working_directory") != "/work" || Text(report, "scratch_device") != "/dev/vdb" ||
            !Integer(report, "memory_mib", budget.MemoryMiB) || !Integer(report, "processors", budget.Processors))
            throw new InvalidDataException("The repair guest self-test did not establish its required execution contract");
        token.ThrowIfCancellationRequested(); _artifactSession = new GuestArtifactSession(jobId, _nonce); _artifacts = new GuestArtifactTransfer(_channel, _nonce, jobId, _artifactQuota); _qualified = true;
        return new(challenge, budget.ScratchBytes, 1000, budget.MemoryMiB, budget.Processors);
    }
    public async Task<JsonElement> RequestAsync(HardwareGuestOperation operation, JsonElement arguments, CancellationToken token)
    {
        var name = operation switch { HardwareGuestOperation.InspectPackage => "package-inspect-v1", HardwareGuestOperation.BuildProject => "project-build-v1", HardwareGuestOperation.ValidatePackage => "package-validate-v1", _ => throw new ArgumentOutOfRangeException(nameof(operation)) };
        if (!_qualified || !_capabilities.Contains(name)) throw new InvalidDataException("The required repair guest operation has not been qualified");
        if (arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(arguments.GetRawText()) > MaximumPayloadBytes) throw new InvalidDataException("Repair guest arguments exceed their bounded object contract");
        await _requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var id = NextId(); await WriteAsync("request", id, JsonSerializer.SerializeToElement(new { operation = name, arguments }), token).ConfigureAwait(false);
            using var reply = await ReadReplyAsync("result", id, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); return Payload(reply.RootElement).Clone();
        }
        finally { _requests.Release(); }
    }
    public Task<GuestArtifactReceipt> UploadArtifactAsync(GuestArtifactDescriptor descriptor, IGuestArtifactLeaseProvider leases, CancellationToken token) =>
        ArtifactAsync((transfer, id) => transfer.UploadAsync(id, descriptor, leases, token), token);
    public Task<GuestArtifactReceipt> DownloadArtifactAsync(string artifactId, GuestArtifactDescriptor descriptor, IGuestArtifactLeaseProvider leases, CancellationToken token) =>
        ArtifactAsync((transfer, id) => transfer.DownloadAsync(id, artifactId, descriptor, leases, token), token);
    private async Task<GuestArtifactReceipt> ArtifactAsync(Func<GuestArtifactTransfer, string, Task<GuestArtifactReceipt>> action, CancellationToken token)
    {
        if (!_qualified || _artifacts is null || !_capabilities.Contains("artifact-transfer-v1")) throw new InvalidDataException("The current guest has no qualified artifact transfer capability");
        await _requests.WaitAsync(token).ConfigureAwait(false);
        try { return await action(_artifacts, NextId()).ConfigureAwait(false); }
        finally { _requests.Release(); }
    }
    private async Task<JsonDocument> ReadReplyAsync(string expectedType, string id, CancellationToken token)
    {
        var logs = 0; var bytes = 0;
        while (true)
        {
            var document = await _channel.ReadAsync(token).ConfigureAwait(false);
            try
            {
                var root = document.RootElement;
                if (Text(root, "id") != id || Text(root, "nonce") != _nonce) throw new InvalidDataException("Repair guest reply correlation did not match");
                var type = Text(root, "type");
                if (type == "log")
                {
                    bytes += Encoding.UTF8.GetByteCount(root.GetRawText());
                    if (++logs > 32 || bytes > 128 * 1024) throw new InvalidDataException("Repair guest log frames exceed their bound");
                    document.Dispose(); continue; // Private protocol text is deliberately neither retained nor logged.
                }
                if (type == "error") throw new InvalidDataException("The repair guest rejected its bounded operation");
                if (type != expectedType) throw new InvalidDataException("Unexpected repair guest protocol frame; no egress or command fallback is enabled");
                _ = Payload(root); return document;
            }
            catch { document.Dispose(); throw; }
        }
    }
    private Task WriteAsync(string type, string id, JsonElement payload, CancellationToken token) => _channel.WriteAsync(JsonSerializer.SerializeToElement(new { type, id, nonce = _nonce, payload }), token);
    private string NextId() => checked(++_sequence).ToString(CultureInfo.InvariantCulture);
    private static JsonElement Payload(JsonElement frame) => frame.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object ? payload : throw new InvalidDataException("Repair guest reply lacks its bounded object payload");
    private static string Text(JsonElement value, string field) => value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
    private static bool Integer(JsonElement value, string field, long expected) => value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) && number == expected;
    private static void Exact(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid guest report shape"); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidDataException("Ambiguous guest report fields");
        if (seen.Count != fields.Length) throw new InvalidDataException("Incomplete guest report");
    }
    public void Dispose() { _channel.Dispose(); _requests.Dispose(); }
}
