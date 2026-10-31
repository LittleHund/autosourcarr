using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

public static class SourceClientTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("source client: strict GraphQL envelopes and secret-safe errors", () =>
        {
            foreach (var body in new[] { "[]", "{}", "{\"data\":null}", "{\"data\":{},\"errors\":{}}", "{\"data\":{},\"errors\":[{}]}" })
            {
                using var client = Client(new((_, _) => Task.FromResult(Json(body))));
                Check.Throws<SuwayomiException>(() => client.GraphQlAsync("query { __typename }").GetAwaiter().GetResult());
            }
            using var secretClient = Client(new((_, _) => Task.FromResult(Json("{\"errors\":[{\"message\":\"request failed: sentinel-password\"}]}"))), password: "sentinel-password");
            var error = Check.Throws<SuwayomiException>(() => secretClient.GraphQlAsync("query { __typename }").GetAwaiter().GetResult());
            Check.True(!error.Message.Contains("sentinel-password", StringComparison.Ordinal));
        });
        suite.Add("source client: Basic credentials stay on the exact runtime origin", () =>
        {
            List<(string Url, string? Auth)> seen = [];
            var handler = new Handler((request, _) =>
            {
                seen.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
                return Task.FromResult(seen.Count == 1 ? Redirect("http://source.test:4568/image", HttpStatusCode.TemporaryRedirect)
                    : Binary(new MemoryStream([1, 2, 3])));
            });
            using var client = Client(handler, password: "secret");
            var result = client.FetchBinaryAsync("http://source.test:4567/image").GetAwaiter().GetResult();
            Check.Equal(3, result.Bytes.Length);
            Check.Equal(2, seen.Count);
            Check.True(seen[0].Auth?.StartsWith("Basic ", StringComparison.Ordinal) == true);
            Check.True(seen[1].Auth is null);
        });
        suite.Add("source client: protected assets and GraphQL cannot redirect to another origin", () =>
        {
            foreach (var graphQl in new[] { false, true })
            {
                var calls = 0;
                using var client = Client(new((_, _) =>
                {
                    calls++;
                    return Task.FromResult(Redirect("https://other.test/stolen", HttpStatusCode.TemporaryRedirect));
                }));
                Check.Throws<SuwayomiException>(() =>
                {
                    if (graphQl) client.GraphQlAsync("mutation { updateManga }").GetAwaiter().GetResult();
                    else client.FetchAssetAsync("/icon").GetAwaiter().GetResult();
                });
                Check.Equal(1, calls);
            }
        });
        suite.Add("source client: decoded buffering is bounded and disposes every response", () =>
        {
            var stream = new TrackingStream([1, 2, 3, 4]);
            using var client = Client(new((_, _) => Task.FromResult(Binary(stream))));
            Check.Throws<SuwayomiException>(() => client.FetchBinaryAsync("image", maximumBytes: 3).GetAwaiter().GetResult());
            Check.True(stream.Closed);
            var exact = new TrackingStream([1, 2, 3]);
            using var exactClient = Client(new((_, _) => Task.FromResult(Binary(exact))));
            Check.Equal(3, exactClient.FetchBinaryAsync("/image", maximumBytes: 3).GetAwaiter().GetResult().Bytes.Length);
            Check.True(exact.Closed);
        });
        suite.Add("source client: shutdown owns and closes unconsumed streaming responses", () =>
        {
            var stream = new TrackingStream([1, 2, 3]);
            var client = Client(new((_, _) => Task.FromResult(Binary(stream))));
            var response = client.OpenBinaryStreamAsync("/image").GetAwaiter().GetResult();
            Check.True(!stream.Closed);
            client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check.True(stream.Closed);
            Check.True(response.CancellationToken.IsCancellationRequested);
            response.Dispose();
            client.Dispose();
            Check.Throws<OperationCanceledException>(() => client.GraphQlAsync("query { __typename }").GetAwaiter().GetResult());
        });
        suite.Add("source client: caller cancellation and shutdown join header requests", () =>
        {
            foreach (var callerCancel in new[] { true, false })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var cancel = new CancellationTokenSource();
                var client = Client(new(async (_, token) =>
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    throw new InvalidOperationException("Unreachable");
                }));
                var request = client.GraphQlAsync("query { __typename }", cancellationToken: cancel.Token);
                entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                if (callerCancel) cancel.Cancel();
                else client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check.Throws<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                client.Dispose();
            }
        });
    }

    private static SuwayomiClient Client(Handler handler, string password = "") => new(SettingsLoader.Load(),
        "http://source.test:4567", username: password.Length > 0 ? "user" : "", password: password, handler: handler);
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Binary(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    private static HttpResponseMessage Redirect(string location, HttpStatusCode status)
    {
        var result = new HttpResponseMessage(status);
        result.Headers.Location = new Uri(location);
        return result;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Closed { get; private set; }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
}
