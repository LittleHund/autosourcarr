using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Core.Repair.Runner.Protocol;

public enum GuestArtifactRole { RawProjectFile, RawProjectManifest, OriginalApk, InstalledJar, CandidateProjectFile, CandidateProjectManifest, CandidateApk, CandidateJar }
public enum GuestArtifactDirection { IntoGuest, FromGuest }

/// <summary>Full byte SHA256 only. It is distinct from raw snapshot IDs and decompressed package fingerprints.</summary>
public sealed record GuestArtifactDescriptor(GuestArtifactRole Role, long Length, string Sha256, string? LogicalName = null, string? ProjectId = null)
{
    public void Validate(GuestArtifactDirection direction)
    {
        if (!Enum.IsDefined(Role) || !Enum.IsDefined(direction)) throw new InvalidDataException("Unsupported guest artifact role");
        var input = Role is GuestArtifactRole.RawProjectFile or GuestArtifactRole.RawProjectManifest or GuestArtifactRole.OriginalApk or GuestArtifactRole.InstalledJar;
        if (input != (direction == GuestArtifactDirection.IntoGuest)) throw new InvalidDataException("Guest artifact role has the wrong direction");
        RawCodeRules.Hash(Sha256);
        var file = Role is GuestArtifactRole.RawProjectFile or GuestArtifactRole.CandidateProjectFile;
        var manifest = Role is GuestArtifactRole.RawProjectManifest or GuestArtifactRole.CandidateProjectManifest;
        var maximum = file ? RawCodeRules.MaximumFile : manifest ? RawCodeRules.MaximumManifest : SourcePackage.MaximumBytes;
        if (Length < 0 || Length > maximum || !file && Length == 0) throw new InvalidDataException("Guest artifact length exceeds its role bound");
        if (file)
        {
            if (LogicalName is null || RawCodeRules.RelativeName(LogicalName) != LogicalName) throw new InvalidDataException("Guest source names must be canonical relative names");
        }
        else if (LogicalName is not null) throw new InvalidDataException("Package/manifest artifacts cannot choose a filesystem name");
        if (file || manifest) { if (ProjectId is null) throw new InvalidDataException("Source artifacts require their pinned project identity"); RawCodeRules.Hash(ProjectId); }
        else if (ProjectId is not null) throw new InvalidDataException("Compiled artifacts are separate from raw projects");
    }
}

public sealed record GuestArtifactBinding(string JobId, string Session, string ArtifactId, GuestArtifactDirection Direction, GuestArtifactDescriptor Descriptor);

/// <summary>Transport receipt only. No raw-tree completeness, package validity, cold import or working-source claim.</summary>
public sealed record GuestArtifactReceipt(GuestArtifactBinding Binding, int Chunks);

public sealed class GuestArtifactReadLease(GuestArtifactBinding binding, Stream content) : IAsyncDisposable
{
    public GuestArtifactBinding Binding { get; } = binding;
    public Stream Content { get; } = content;
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>The provider stages into a private new quarantine slot and must publish/abort durably.
/// Commit is byte receipt publication only; the caller still owes independent source validation.</summary>
public abstract class GuestArtifactWriteLease(GuestArtifactBinding binding, Stream content) : IAsyncDisposable
{
    public GuestArtifactBinding Binding { get; } = binding;
    public Stream Content { get; } = content;
    public abstract Task CommitAsync(GuestArtifactReceipt receipt, CancellationToken cancellationToken);
    public abstract ValueTask DisposeAsync();
}

/// <summary>Required host-owned lease boundary. There is no default path or arbitrary-path opener.
/// Adapters must bind the job/session/role, use private regular non-link files, preserve exact original
/// inputs, refuse symlink/hard-link/overwrite escapes, and join all physical IO before disposal.</summary>
public interface IGuestArtifactLeaseProvider
{
    Task<GuestArtifactReadLease> OpenInputAsync(GuestArtifactBinding binding, CancellationToken cancellationToken);
    Task<GuestArtifactWriteLease> CreateOutputAsync(GuestArtifactBinding binding, CancellationToken cancellationToken);
}

/// <summary>One project tree and one of each package role in each direction, including the manifest in total bytes.</summary>
internal sealed class GuestArtifactQuota
{
    internal const long MaximumDirectionBytes = RawCodeRules.MaximumExpanded + RawCodeRules.MaximumManifest + 2L * SourcePackage.MaximumBytes;
    private sealed class Direction
    {
        public long Bytes, ProjectBytes;
        public int Files;
        public string? Project;
        public readonly HashSet<string> Names = new(StringComparer.Ordinal);
        public readonly HashSet<GuestArtifactRole> Unique = [];
    }
    private readonly Direction _input = new(), _output = new();
    public void Reserve(GuestArtifactDescriptor descriptor, GuestArtifactDirection direction)
    {
        descriptor.Validate(direction); var state = direction == GuestArtifactDirection.IntoGuest ? _input : _output;
        var file = descriptor.Role is GuestArtifactRole.RawProjectFile or GuestArtifactRole.CandidateProjectFile;
        if (descriptor.ProjectId is not null && state.Project is not null && descriptor.ProjectId != state.Project) throw new InvalidDataException("A transfer cannot switch its pinned project");
        if (state.Bytes + descriptor.Length > MaximumDirectionBytes || file && (state.Files >= RawCodeRules.MaximumFiles || state.ProjectBytes + descriptor.Length > RawCodeRules.MaximumExpanded))
            throw new InvalidDataException("Guest artifact aggregate quota exceeded");
        if (file ? !state.Names.Add(descriptor.LogicalName!) : !state.Unique.Add(descriptor.Role)) throw new InvalidDataException("Duplicate guest artifact role or raw filename");
        state.Project ??= descriptor.ProjectId; state.Bytes += descriptor.Length;
        if (file) { state.Files++; state.ProjectBytes += descriptor.Length; }
        // Failed/uncertain transfers are not refunded or replayed. Their session is killed.
    }
}

/// <summary>
/// artifact-transfer-v1: an explicit request/open ack, stop-and-wait 32KiB raw Base64 chunks,
/// then exact length/hash/chunk-count commit. All frames retain the conversation id+nonce and
/// an opaque artifact id. No peer-supplied host path exists. A real guest must keep partial
/// objects inaccessible to tools; raw-tree commit/validation is a later domain operation.
/// </summary>
internal sealed class GuestArtifactTransfer(GuestChannel channel, string nonce, string jobId, GuestArtifactQuota quota)
{
    internal const int ChunkBytes = 32 * 1024;
    public async Task<GuestArtifactReceipt> UploadAsync(string id, GuestArtifactDescriptor descriptor, IGuestArtifactLeaseProvider provider, CancellationToken token)
    {
        quota.Reserve(descriptor, GuestArtifactDirection.IntoGuest);
        var binding = Binding(descriptor, GuestArtifactDirection.IntoGuest, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        await using var lease = await provider.OpenInputAsync(binding, token).ConfigureAwait(false);
        if (lease.Binding != binding || !lease.Content.CanRead) throw new InvalidDataException("Artifact input lease does not match its private binding");
        await WriteAsync("request", id, Open("artifact-upload-v1", binding), token).ConfigureAwait(false);
        using (var opened = await ReplyAsync(id, "result", token).ConfigureAwait(false)) Ack(opened.RootElement, binding.ArtifactId, "open", 0, 0);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[ChunkBytes]; long offset = 0; var index = 0;
        while (offset < descriptor.Length)
        {
            var wanted = (int)Math.Min(buffer.Length, descriptor.Length - offset);
            await lease.Content.ReadExactlyAsync(buffer.AsMemory(0, wanted), token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            hash.AppendData(buffer.AsSpan(0, wanted));
            await WriteAsync("chunk", id, JsonSerializer.SerializeToElement(new { phase = "data", artifact_id = binding.ArtifactId, index, offset, data = Convert.ToBase64String(buffer, 0, wanted) }), token).ConfigureAwait(false);
            using var ack = await ReplyAsync(id, "result", token).ConfigureAwait(false); Ack(ack.RootElement, binding.ArtifactId, "data", index, offset + wanted);
            offset += wanted; index++;
        }
        var extra = await lease.Content.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false);
        if (extra != 0 || Convert.ToHexStringLower(hash.GetHashAndReset()) != descriptor.Sha256) throw new InvalidDataException("Artifact input bytes changed or exceeded their exact identity");
        token.ThrowIfCancellationRequested();
        await WriteAsync("request", id, Commit(binding.ArtifactId, descriptor, index), token).ConfigureAwait(false);
        using (var committed = await ReplyAsync(id, "result", token).ConfigureAwait(false)) Finished(committed.RootElement, binding.ArtifactId, descriptor, index);
        token.ThrowIfCancellationRequested(); return new(binding, index);
    }
    public async Task<GuestArtifactReceipt> DownloadAsync(string id, string artifactId, GuestArtifactDescriptor descriptor, IGuestArtifactLeaseProvider provider, CancellationToken token)
    {
        quota.Reserve(descriptor, GuestArtifactDirection.FromGuest); var binding = Binding(descriptor, GuestArtifactDirection.FromGuest, artifactId);
        await using var lease = await provider.CreateOutputAsync(binding, token).ConfigureAwait(false);
        if (lease.Binding != binding || !lease.Content.CanWrite) throw new InvalidDataException("Artifact output lease does not match its private binding");
        await WriteAsync("request", id, Open("artifact-download-v1", binding), token).ConfigureAwait(false);
        using (var opened = await ReplyAsync(id, "result", token).ConfigureAwait(false)) Ack(opened.RootElement, binding.ArtifactId, "open", 0, 0);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long offset = 0; var index = 0;
        while (offset < descriptor.Length)
        {
            using var frame = await ReplyAsync(id, "chunk", token).ConfigureAwait(false); var data = frame.RootElement;
            Exact(data, ["phase", "artifact_id", "index", "offset", "data"]);
            var encoded = Text(data, "data"); var wanted = (int)Math.Min(ChunkBytes, descriptor.Length - offset);
            if (Text(data, "phase") != "data" || Text(data, "artifact_id") != artifactId || !Integer(data, "index", index) || !Integer(data, "offset", offset) || encoded.Length != ((wanted + 2) / 3) * 4)
                throw new InvalidDataException("Artifact chunk identity, order or size changed");
            var bytes = new byte[wanted];
            if (!Convert.TryFromBase64String(encoded, bytes, out var length) || length != wanted || Convert.ToBase64String(bytes) != encoded) throw new InvalidDataException("Artifact chunk is not canonical bounded Base64");
            await lease.Content.WriteAsync(bytes, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); hash.AppendData(bytes);
            offset += wanted;
            await WriteAsync("result", id, JsonSerializer.SerializeToElement(new { phase = "data", artifact_id = artifactId, index, next_offset = offset }), token).ConfigureAwait(false); index++;
        }
        using (var complete = await ReplyAsync(id, "result", token).ConfigureAwait(false)) Finished(complete.RootElement, artifactId, descriptor, index);
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != descriptor.Sha256) throw new InvalidDataException("Artifact output failed its exact hash");
        await lease.Content.FlushAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        var receipt = new GuestArtifactReceipt(binding, index); await lease.CommitAsync(receipt, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        await WriteAsync("result", id, Commit(artifactId, descriptor, index), token).ConfigureAwait(false);
        return receipt;
    }
    private GuestArtifactBinding Binding(GuestArtifactDescriptor descriptor, GuestArtifactDirection direction, string artifactId)
    {
        if (artifactId.Length != 32 || artifactId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new InvalidDataException("Invalid opaque artifact identity");
        return new(jobId, nonce, artifactId, direction, descriptor);
    }
    private static JsonElement Open(string operation, GuestArtifactBinding binding) => JsonSerializer.SerializeToElement(new { operation,
        artifact_id = binding.ArtifactId, role = binding.Descriptor.Role.ToString(), length = binding.Descriptor.Length, sha256 = binding.Descriptor.Sha256,
        logical_name = binding.Descriptor.LogicalName, project_id = binding.Descriptor.ProjectId });
    private static JsonElement Commit(string artifactId, GuestArtifactDescriptor descriptor, int chunks) => JsonSerializer.SerializeToElement(new { phase = "commit", artifact_id = artifactId, length = descriptor.Length, sha256 = descriptor.Sha256, chunks });
    private static void Ack(JsonElement payload, string artifactId, string phase, int index, long offset)
    {
        Exact(payload, ["phase", "artifact_id", "index", "next_offset"]);
        if (Text(payload, "phase") != phase || Text(payload, "artifact_id") != artifactId || !Integer(payload, "index", index) || !Integer(payload, "next_offset", offset)) throw new InvalidDataException("Artifact acknowledgement did not match its outstanding chunk");
    }
    private static void Finished(JsonElement payload, string artifactId, GuestArtifactDescriptor descriptor, int chunks)
    {
        Exact(payload, ["phase", "artifact_id", "length", "sha256", "chunks"]);
        if (Text(payload, "phase") != "commit" || Text(payload, "artifact_id") != artifactId || !Integer(payload, "length", descriptor.Length) || Text(payload, "sha256") != descriptor.Sha256 || !Integer(payload, "chunks", chunks)) throw new InvalidDataException("Artifact commit did not match exact bytes");
    }
    private Task WriteAsync(string type, string id, JsonElement payload, CancellationToken token) => channel.WriteAsync(JsonSerializer.SerializeToElement(new { type, id, nonce, payload }), token);
    private async Task<JsonDocument> ReplyAsync(string id, string type, CancellationToken token)
    {
        var frame = await channel.ReadAsync(token).ConfigureAwait(false);
        try
        {
            if (Text(frame.RootElement, "id") != id || Text(frame.RootElement, "nonce") != nonce || Text(frame.RootElement, "type") != type || !frame.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Unexpected artifact frame or session binding");
            return JsonDocument.Parse(payload.GetRawText(), new JsonDocumentOptions { MaxDepth = 16 });
        }
        finally { frame.Dispose(); }
    }
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";
    private static bool Integer(JsonElement value, string name, long expected) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var number) && number == expected;
    private static void Exact(JsonElement value, string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidDataException("Ambiguous artifact payload fields");
        if (seen.Count != names.Length) throw new InvalidDataException("Incomplete artifact payload");
    }
}
