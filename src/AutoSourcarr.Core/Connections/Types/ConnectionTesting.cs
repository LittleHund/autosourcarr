using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>A connection answered with an HTTP error or a body that does not match the service's contract.</summary>
public sealed class ConnectionHttpException : Exception
{
    public ConnectionHttpException() { }
    public ConnectionHttpException(string message) : base(message) { }
    public ConnectionHttpException(string message, Exception inner) : base(message, inner) { }
    public ConnectionHttpException(string message, HttpStatusCode? status, Exception? inner = null) : base(message, inner) => StatusCode = status;
    public HttpStatusCode? StatusCode { get; }
}

/// <summary>Small HTTP helpers shared by the connection types that are not metadata providers (Komf, MalwareBazaar, Suwayomi).</summary>
internal static class ConnectionHttp
{
    public const int MaxBody = 8 * 1024 * 1024;

    public static HttpClient Client(HttpMessageHandler handler, TimeSpan timeout)
        => new(handler, disposeHandler: false) { Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : timeout };

    /// <summary>Sends and returns the JSON body. Non-2xx, a body over 8 MiB or a body that is not JSON throws <see cref="ConnectionHttpException"/>.</summary>
    public static async Task<JsonNode?> SendJsonAsync(HttpClient client, HttpRequestMessage request, string label, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException(label + " did not answer within " + client.Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s.", error); }
        using (response)
        {
            var bytes = await ReadBoundedAsync(response, label, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ConnectionHttpException(label + " answered HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".", response.StatusCode);
            if (bytes.Length == 0) return null;
            try { return JsonNode.Parse(bytes); }
            catch (JsonException error) { throw new ConnectionHttpException(label + " answered with a body that is not JSON.", response.StatusCode, error); }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, string label, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxBody) throw new ConnectionHttpException(label + " answered with more than 8 MiB.", response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBody) throw new ConnectionHttpException(label + " answered with more than 8 MiB.", response.StatusCode);
            }
            return buffer.ToArray();
        }
    }

    /// <summary>An absolute http(s) address without user info, or an error for the Test result.</summary>
    public static bool TryHttpUri(string value, out Uri uri, out string error)
    {
        uri = null!;
        error = "";
        if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        { error = "The address must be an absolute http:// or https:// URL."; return false; }
        if (parsed.UserInfo.Length > 0) { error = "Put the username and password in their own fields, not in the address."; return false; }
        uri = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");
        return true;
    }
}

/// <summary>Turns the usual failures of a Test into one short plain message (CON-1); never swallows the caller's cancellation.</summary>
internal static class ConnectionTesting
{
    public static async Task<ConnectionTestResult> RunAsync(string label, Func<Stopwatch, Task<ConnectionTestResult>> test, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try { return await test(watch).ConfigureAwait(false); }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not OutOfMemoryException)
        {
            var details = new JsonObject { ["latency_ms"] = watch.ElapsedMilliseconds, ["error_type"] = error.GetType().Name };
            return ConnectionTestResult.Fail(Message(label, error, details), details);
        }
    }

    private static string Message(string label, Exception error, JsonObject details)
    {
        HttpStatusCode? status = error switch
        {
            MetadataProviderException provider => provider.StatusCode,
            ConnectionHttpException http => http.StatusCode,
            HttpRequestException request => request.StatusCode,
            _ => null,
        };
        if (status is { } code) details["http_status"] = (int)code;
        if (error is MetadataRateLimitedException limited)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(limited.RetryAfter.TotalSeconds));
            details["retry_after_seconds"] = seconds;
            return label + " is rate limiting requests; try again in " + seconds.ToString(CultureInfo.InvariantCulture) + " s.";
        }
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return label + " rejected the credentials (HTTP " + ((int)status.Value).ToString(CultureInfo.InvariantCulture) + "). Check the token or password.";
        return error switch
        {
            MetadataProviderException when status is { } failed && (int)failed >= 400 => label + " answered HTTP " + ((int)failed).ToString(CultureInfo.InvariantCulture) + ".",
            MetadataProviderException => label + " answered unexpectedly: " + error.Message,
            ConnectionHttpException => error.Message,
            TimeoutException or OperationCanceledException => label + " did not answer in time.",
            HttpRequestException => "Could not reach " + label + ": " + error.Message,
            JsonException or InvalidOperationException or FormatException => label + " answered unexpectedly: " + error.Message,
            ArgumentException => error.Message,
            _ => label + " test failed: " + error.Message,
        };
    }

}
