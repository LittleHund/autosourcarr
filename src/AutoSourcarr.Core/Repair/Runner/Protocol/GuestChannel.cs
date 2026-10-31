using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace AutoSourcarr.Core.Repair.Runner.Protocol;

/// <summary>Bounded RPC frames on the private VM serial link, never a host directory share.</summary>
public sealed class GuestChannel(Stream input, Stream output) : IDisposable
{
    public const int MaximumFrameBytes = 65536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> Types = ["hello", "request", "result", "chunk", "http-open", "http-data", "http-close", "log", "error", "cancel"];
    private static readonly HashSet<string> Fields = ["type", "id", "nonce", "payload"];
    private readonly SemaphoreSlim _write = new(1, 1);

    public async Task<JsonDocument> ReadAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > MaximumFrameBytes)
            throw new InvalidDataException("Repair guest frame exceeds its bounds.");
        var bytes = new byte[length];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        _ = StrictUtf8.GetCharCount(bytes);
        var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        try
        {
            Validate(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(JsonElement frame, CancellationToken cancellationToken)
    {
        Validate(frame);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        if (bytes.Length > MaximumFrameBytes)
            throw new InvalidDataException("Repair guest frame exceeds its bounds.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose() => _write.Dispose();

    private static void Validate(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Repair guest frame must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in frame.EnumerateObject())
            if (!Fields.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException("Repair guest frame contains ambiguous fields.");
        if (!frame.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !Types.Contains(type.GetString()!))
            throw new InvalidDataException("Unknown repair guest frame type.");
        foreach (var name in new[] { "id", "nonce" })
            if (frame.TryGetProperty(name, out var field) && (field.ValueKind != JsonValueKind.String || field.GetString()!.Length > 128))
                throw new InvalidDataException("Invalid repair guest correlation identity.");
    }
}
