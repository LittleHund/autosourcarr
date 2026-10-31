using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace AutoSourcarr.Api.Http;

public sealed class ApiProblem(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class ApiJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // Only the two explicitly mapped raw-body uploads use this metadata. Their
    // decoded entity limit remains enforced below, independently of chunk framing.
    internal static IRequestSizeLimitMetadata BoundedUploadMetadata { get; } = new UploadSizeMetadata();
    private sealed class UploadSizeMetadata : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => null; }

    /// <summary>Read one authorized upload without taking ownership of the server's request stream.</summary>
    public static Task<byte[]> ReadUploadAsync(HttpRequest request, int maximumBytes, string tooLargeMessage, string timeoutMessage)
        => ReadUploadAsync(request, maximumBytes, tooLargeMessage, timeoutMessage, TimeProvider.System);

    internal static async Task<byte[]> ReadUploadAsync(HttpRequest request, int maximumBytes, string tooLargeMessage, string timeoutMessage, TimeProvider timeProvider)
    {
        if (maximumBytes < 1 || maximumBytes == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (request.ContentLength > maximumBytes) throw new ApiProblem(413, tooLargeMessage);
        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is not null)
        {
            if (feature.IsReadOnly) throw new ApiProblem(409, "Upload request body already started; select the file again");
            feature.MaxRequestBodySize = null;
        }
        // One absolute deadline for the complete body, never a fresh timer per chunk.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted, deadline.Token);
        using var output = new MemoryStream(); var buffer = new byte[32768];
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var capacity = Math.Min(buffer.Length, maximumBytes + 1 - checked((int)output.Length));
                var read = await request.Body.ReadAsync(buffer.AsMemory(0, capacity), linked.Token).ConfigureAwait(false);
                // A stream ignoring cancellation still owns its physical read; join it
                // before reporting expiry, and never accept its late data as success.
                linked.Token.ThrowIfCancellationRequested();
                if (read == 0) break;
                output.Write(buffer, 0, read);
                if (output.Length > maximumBytes) throw new ApiProblem(413, tooLargeMessage);
            }
        }
        catch (OperationCanceledException) when (!request.HttpContext.RequestAborted.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new ApiProblem(408, timeoutMessage); }
        return output.ToArray();
    }

    public static async Task<JsonObject> ReadObjectAsync(HttpRequest request, int maximumBytes = 16384)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (request.ContentLength > maximumBytes) throw new ApiProblem(413, "Request exceeds its byte limit");
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new ApiProblem(413, "Request exceeds its byte limit");
            output.Write(buffer, 0, read);
        }
        try
        {
            var text = StrictUtf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            using var document = JsonDocument.Parse(text, new() { MaxDepth = 64 });
            RejectDuplicateKeys(document.RootElement);
            return JsonNode.Parse(text, documentOptions: new() { MaxDepth = 64 }) as JsonObject ?? throw new ApiProblem(400, "Expected a request object");
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new ApiProblem(400, "Expected a valid UTF-8 JSON request object"); }
    }

    public static async Task WriteAsync(HttpContext context, int statusCode, JsonObject value)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
    public static Task ProblemAsync(HttpContext context, int statusCode, string detail) => WriteAsync(context, statusCode, new() { ["detail"] = detail });
    private static void RejectDuplicateKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new ApiProblem(400, "Duplicate JSON property");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) RejectDuplicateKeys(item);
    }
}
