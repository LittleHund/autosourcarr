using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Connections.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoSourcarr.Tests.Connections;

/// <summary>
/// CON-3 change feed against an in-process fake Suwayomi (Kestrel): graphql-transport-ws handshake, both subscriptions,
/// next → typed events, ping/pong both ways, reconnect after the server closes, library diff while live, and the polling
/// fallback (chapter/download counts and membership) when the WebSocket is refused.
/// </summary>
public static class SuwayomiChangeFeedTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("suwayomi change feed: live subscriptions deliver download and chapter changes, answer pings and reconnect", () =>
        {
            using var server = FakeSuwayomi.Start(acceptWebSockets: true).GetAwaiter().GetResult();
            server.SetLibrary((1, 10, 0), (2, 5, 1));
            var changes = new ConcurrentQueue<SuwayomiChange>();
            var errors = new ConcurrentQueue<Exception>();
            using var http = new SocketsHttpHandler();
            var feed = new SuwayomiChangeFeed(new SuwayomiEndpoint(new Uri(server.Address), "me", "pw"), http, changes.Enqueue, errors.Enqueue,
                new SuwayomiChangeFeedOptions
                {
                    LivePollInterval = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromMilliseconds(100),
                    KeepAlive = TimeSpan.FromMilliseconds(300), ReconnectMin = TimeSpan.FromMilliseconds(50), ReconnectMax = TimeSpan.FromSeconds(1),
                });
            try
            {
                feed.Start();
                Wait(() => feed.Mode == SuwayomiFeedMode.Live && server.Subscriptions.Count >= 2, "the feed subscribes");
                Check.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me:pw")), server.UpgradeAuthorization);
                Check.True(server.Subscriptions.Any(item => item.Contains("downloadStatusChanged(input: {maxUpdates: 50})", StringComparison.Ordinal)), "download subscription");
                Check.True(server.Subscriptions.Any(item => item.Contains("libraryUpdateStatusChanged(input: {maxUpdates: 50})", StringComparison.Ordinal)), "library update subscription");
                Wait(() => changes.OfType<SuwayomiFeedModeChanged>().Any(change => change.Mode == SuwayomiFeedMode.Live), "Live mode is announced");
                // While live the subscriptions carry chapters and downloads: the library diff reads ids only.
                Wait(() => server.Queries.Any(query => !query.Contains("totalCount", StringComparison.Ordinal)), "an ids-only library read while live");

                server.Send("""
                    {"id":"autosourcarr-downloads","type":"next","payload":{"data":{"downloadStatusChanged":{"omittedUpdates":false,"updates":[
                      {"type":"PROGRESS","download":{"state":"DOWNLOADING","chapter":{"id":78,"mangaId":6}}},
                      {"type":"FINISHED","download":{"state":"FINISHED","chapter":{"id":77,"mangaId":5}}}]}}}}
                    """);
                var download = WaitFor<SuwayomiDownloadChanged>(changes, "download change");
                Check.True(download.MangaIds.SequenceEqual([5L]) && download.ChapterIds.SequenceEqual([77L]) && !download.Incomplete,
                    "Only the finished download counts: " + string.Join(",", download.MangaIds));

                server.Send("""
                    {"id":"autosourcarr-updates","type":"next","payload":{"data":{"libraryUpdateStatusChanged":{"omittedUpdates":true,"jobsInfo":{"isRunning":true},
                      "mangaUpdates":[{"status":"RUNNING","manga":{"id":1}},{"status":"COMPLETE","manga":{"id":2}}]}}}}
                    """);
                var chapters = WaitFor<SuwayomiChaptersChanged>(changes, "chapter change");
                Check.True(chapters.MangaIds.SequenceEqual([2L]) && chapters.Incomplete, "Completed manga, omitted updates flagged.");

                server.Send("""{"type":"ping"}""");
                Wait(() => server.Received.Any(text => text.Contains("\"pong\"", StringComparison.Ordinal)), "the feed answers a server ping");
                Wait(() => server.Received.Any(text => text.Contains("\"ping\"", StringComparison.Ordinal)), "the feed pings the server");

                server.SetLibrary((1, 10, 0), (2, 6, 1), (9, 1, 0));
                var library = WaitFor<SuwayomiLibraryChanged>(changes, "library add while live");
                Check.True(library.Added.SequenceEqual([9L]) && library.Removed.Count == 0, "Added manga 9.");

                server.CloseClient();
                Wait(() => server.Connections >= 2 && server.Subscriptions.Count >= 4 && feed.Mode == SuwayomiFeedMode.Live, "the feed reconnects and subscribes again");
                Check.True(changes.OfType<SuwayomiFeedModeChanged>().Any(change => change.Mode == SuwayomiFeedMode.Polling), "The gap is announced.");
                server.Send("""
                    {"id":"autosourcarr-downloads","type":"next","payload":{"data":{"downloadStatusChanged":{"omittedUpdates":false,"updates":[
                      {"type":"QUEUED","download":{"state":"QUEUED","chapter":{"id":90,"mangaId":9}}}]}}}}
                    """);
                Check.True(WaitFor<SuwayomiDownloadChanged>(changes, "download after reconnect").MangaIds.SequenceEqual([9L]), "Events flow after reconnect.");
            }
            finally { feed.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)); }
            Check.Equal(SuwayomiFeedMode.Stopped, feed.Mode);
        });

        suite.Add("suwayomi change feed: polls the library when subscriptions are unavailable and reports membership, chapter and download changes", () =>
        {
            using var server = FakeSuwayomi.Start(acceptWebSockets: false).GetAwaiter().GetResult();
            server.SetLibrary((1, 3, 0), (2, 5, 1));
            var changes = new ConcurrentQueue<SuwayomiChange>();
            var errors = new ConcurrentQueue<Exception>();
            using var http = new SocketsHttpHandler();
            var feed = new SuwayomiChangeFeed(new SuwayomiEndpoint(new Uri(server.Address)), http, changes.Enqueue, errors.Enqueue,
                new SuwayomiChangeFeedOptions { PollInterval = TimeSpan.FromMilliseconds(100), ReconnectMin = TimeSpan.FromSeconds(30), ReconnectMax = TimeSpan.FromSeconds(30) });
            try
            {
                feed.Start();
                Wait(() => feed.Mode == SuwayomiFeedMode.Polling && server.Polls >= 1, "the feed falls back to polling");
                Wait(() => !errors.IsEmpty, "the unavailable socket is reported");
                var polls = server.Polls;
                Wait(() => server.Polls >= polls + 2, "the feed keeps polling");
                Check.True(!changes.OfType<SuwayomiLibraryChanged>().Any(), "The first read is the baseline, not a change.");

                server.SetLibrary((1, 4, 0), (3, 1, 0));
                var library = WaitFor<SuwayomiLibraryChanged>(changes, "membership diff");
                Check.True(library.Added.SequenceEqual([3L]) && library.Removed.SequenceEqual([2L]), "Added 3, removed 2.");
                Check.True(WaitFor<SuwayomiChaptersChanged>(changes, "chapter count diff").MangaIds.SequenceEqual([1L]), "Manga 1 got a chapter.");
                server.SetLibrary((1, 4, 2), (3, 1, 0));
                Check.True(WaitFor<SuwayomiDownloadChanged>(changes, "download count diff").MangaIds.SequenceEqual([1L]), "Manga 1 downloaded chapters.");
                Check.Equal(1, errors.Count);
            }
            finally { feed.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)); }
        });
    }

    private static void Wait(Func<bool> condition, string what, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new InvalidOperationException("Timed out waiting for " + what + ".");
            Thread.Sleep(20);
        }
    }

    /// <summary>Waits for the next change of type <typeparamref name="T"/>, dropping earlier changes of that type it already returned.</summary>
    private static T WaitFor<T>(ConcurrentQueue<SuwayomiChange> changes, string what) where T : SuwayomiChange
    {
        T? found = null;
        Wait(() =>
        {
            while (changes.TryDequeue(out var change))
                if (change is T match) { found = match; return true; }
            return false;
        }, what);
        return found!;
    }

    private sealed class FakeSuwayomi : IDisposable
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _send = new(1, 1);
        private IHost? _host;
        private WebSocket? _socket;
        private (long Id, int Chapters, int Downloads)[] _library = [];
        private int _connections, _polls;

        public bool AcceptWebSockets { get; private init; }
        public string Address { get; private set; } = "";
        public string? UpgradeAuthorization { get; private set; }
        public ConcurrentQueue<string> Received { get; } = new();
        public ConcurrentQueue<string> Subscriptions { get; } = new();
        public int Connections => Volatile.Read(ref _connections);
        public int Polls => Volatile.Read(ref _polls);

        public static async Task<FakeSuwayomi> Start(bool acceptWebSockets)
        {
            var fake = new FakeSuwayomi { AcceptWebSockets = acceptWebSockets };
            fake._host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).ConfigureWebHost(web => web
                .UseKestrel(options => options.Listen(IPAddress.Loopback, 0))
                .Configure(app =>
                {
                    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
                    app.Run(fake.HandleAsync);
                })).Build();
            await fake._host.StartAsync();
            fake.Address = fake._host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return fake;
        }

        public ConcurrentQueue<string> Queries { get; } = new();

        public void SetLibrary(params (long Id, int Chapters, int Downloads)[] library) { lock (_gate) _library = library; }

        private async Task HandleAsync(HttpContext context)
        {
            if (context.Request.Path != "/api/graphql") { context.Response.StatusCode = 404; return; }
            if (context.WebSockets.IsWebSocketRequest)
            {
                if (!AcceptWebSockets) { context.Response.StatusCode = 404; return; }
                UpgradeAuthorization = context.Request.Headers.Authorization.ToString();
                await RunSocketAsync(context);
                return;
            }
            if (!HttpMethods.IsPost(context.Request.Method)) { context.Response.StatusCode = 405; return; }
            using var reader = new StreamReader(context.Request.Body);
            var request = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            Check.True(request["query"]!.GetValue<string>().Contains("inLibrary: true", StringComparison.Ordinal), "The feed only reads the library.");
            Interlocked.Increment(ref _polls);
            Queries.Enqueue(request["query"]!.GetValue<string>());
            (long Id, int Chapters, int Downloads)[] library;
            lock (_gate) library = _library;
            var nodes = new JsonArray(library.Select(item => (JsonNode)new JsonObject
            {
                ["id"] = item.Id, ["downloadCount"] = item.Downloads, ["chapters"] = new JsonObject { ["totalCount"] = item.Chapters },
            }).ToArray());
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(new JsonObject
            {
                ["data"] = new JsonObject { ["mangas"] = new JsonObject { ["nodes"] = nodes, ["pageInfo"] = new JsonObject { ["hasNextPage"] = false, ["endCursor"] = null } } },
            }.ToJsonString());
        }

        private async Task RunSocketAsync(HttpContext context)
        {
            Check.True(context.WebSockets.WebSocketRequestedProtocols.Contains(SuwayomiChangeFeed.Protocol), "The feed asks for graphql-transport-ws.");
            using var socket = await context.WebSockets.AcceptWebSocketAsync(SuwayomiChangeFeed.Protocol);
            lock (_gate) _socket = socket;
            Interlocked.Increment(ref _connections);
            var buffer = new byte[64 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);
                    var text = Encoding.UTF8.GetString(message.ToArray());
                    Received.Enqueue(text);
                    var json = JsonNode.Parse(text)!.AsObject();
                    switch (json["type"]!.GetValue<string>())
                    {
                        case "connection_init": await SendAsync(socket, """{"type":"connection_ack"}"""); break;
                        case "subscribe": Subscriptions.Enqueue(json["id"]!.GetValue<string>() + "|" + json["payload"]!["query"]!.GetValue<string>()); break;
                        case "ping": await SendAsync(socket, """{"type":"pong"}"""); break;
                    }
                }
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
            finally { lock (_gate) if (ReferenceEquals(_socket, socket)) _socket = null; }
        }

        private async Task SendAsync(WebSocket socket, string text)
        {
            await _send.WaitAsync();
            try { await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _send.Release(); }
        }

        /// <summary>Sends a server message to the connected feed.</summary>
        public void Send(string text)
        {
            WebSocket? socket;
            lock (_gate) socket = _socket;
            Check.True(socket is not null, "A feed is connected.");
            SendAsync(socket!, text).GetAwaiter().GetResult();
        }

        /// <summary>Closes the current socket from the server side.</summary>
        public void CloseClient()
        {
            WebSocket? socket;
            lock (_gate) socket = _socket;
            if (socket is null) return;
            _send.Wait();
            try { socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).GetAwaiter().GetResult(); }
            finally { _send.Release(); }
        }

        public void Dispose()
        {
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                try { _host?.StopAsync(stop.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _host?.Dispose();
            _send.Dispose();
        }
    }
}
