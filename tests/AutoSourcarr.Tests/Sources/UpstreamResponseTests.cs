using System.Net;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

public static class UpstreamResponseTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("upstream ownership: shutdown joins a noncooperative body read after headers return", () =>
        {
            var body = new BlockedBody();
            var content = new CountedContent(body);
            var client = Client(content);
            var response = client.OpenBinaryStreamAsync("/image").GetAwaiter().GetResult();
            var stream = response.OpenBodyAsync().GetAwaiter().GetResult();
            var read = stream.ReadAsync(new byte[1]).AsTask();
            body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Task? stop = null;
            try
            {
                stop = client.DisposeAsync().AsTask();
                Check.True(!stop.IsCompleted, "The response lease ended while a physical read was still running.");
            }
            finally
            {
                body.Release.TrySetResult();
                Check.Throws<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                (stop ?? client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                response.Dispose();
            }
            Check.Equal(1, content.Disposals);
            Check.True(body.Disposals >= 1); // StreamContent also owns its cached read-only stream.
        });
        suite.Add("upstream ownership: shutdown joins noncooperative stream creation", () =>
        {
            var content = new BlockedContent();
            var client = Client(content);
            var response = client.OpenBinaryStreamAsync("/image").GetAwaiter().GetResult();
            var open = response.OpenBodyAsync();
            content.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Task? stop = null;
            try
            {
                stop = client.DisposeAsync().AsTask();
                Check.True(!stop.IsCompleted, "The response lease ended before physical body creation.");
            }
            finally
            {
                content.Release.TrySetResult();
                Check.Throws<OperationCanceledException>(() => open.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                (stop ?? client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
            Check.True(content.Body.Disposals > 0, "A body returned after closure must also be disposed.");
        });
        suite.Add("upstream ownership: one body wrapper closes its response and refuses new IO", () =>
        {
            var body = new CountingBody([1, 2, 3]);
            var content = new CountedContent(body);
            var client = Client(content);
            var response = client.OpenBinaryStreamAsync("/image").GetAwaiter().GetResult();
            var open = response.OpenBodyAsync();
            Check.True(ReferenceEquals(open, response.OpenBodyAsync()));
            var stream = open.GetAwaiter().GetResult();
            Check.True(!ReferenceEquals(body, stream));
            Check.True(stream.CanRead && !stream.CanWrite);
            Check.Equal(1, stream.ReadByte());
            Check.Throws<NotSupportedException>(() => stream.WriteByte(9));
            stream.Dispose();
            Check.Throws<OperationCanceledException>(() => stream.ReadByte());
            Check.Throws<OperationCanceledException>(() => response.OpenBodyAsync().GetAwaiter().GetResult());
            client.Dispose();
            Check.Equal(1, content.Disposals);
            Check.True(body.Disposals >= 1);
        });
        suite.Add("upstream ownership: direct response close retains outstanding read ownership", () =>
        {
            var body = new BlockedBody();
            var client = Client(new StreamContent(body));
            var response = client.OpenBinaryStreamAsync("/image").GetAwaiter().GetResult();
            var stream = response.OpenBodyAsync().GetAwaiter().GetResult();
            var read = stream.ReadAsync(new byte[1]).AsTask();
            body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Task? stop = null;
            try
            {
                response.Dispose();
                stop = client.DisposeAsync().AsTask();
                Check.True(!stop.IsCompleted);
            }
            finally
            {
                body.Release.TrySetResult();
                Check.Throws<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                (stop ?? client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
        });
        suite.Add("upstream ownership: cancellation callback errors do not release a running read", () =>
        {
            var body = new BlockedBody();
            var client = Client(new StreamContent(body));
            var response = client.OpenBinaryStreamAsync("/image").GetAwaiter().GetResult();
            using var registration = response.CancellationToken.Register(() => throw new InvalidOperationException("controlled cancellation failure"));
            var stream = response.OpenBodyAsync().GetAwaiter().GetResult();
            var read = stream.ReadAsync(new byte[1]).AsTask();
            body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Task? stop = null;
            try
            {
                stop = client.DisposeAsync().AsTask();
                Check.True(!stop.IsCompleted);
            }
            finally
            {
                body.Release.TrySetResult();
                Check.Throws<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                Check.Throws<Exception>(() => (stop ?? client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
            }
            Check.Throws<Exception>(() => client.Dispose());
        });
        suite.Add("upstream ownership: physical disposal is asynchronous and remains joined", () =>
        {
            var body = new BlockedDisposeBody();
            var client = Client(new StreamContent(body));
            var response = client.OpenBinaryStreamAsync("/image").GetAwaiter().GetResult();
            var stop = client.DisposeAsync().AsTask();
            try
            {
                body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check.True(!stop.IsCompleted);
            }
            finally
            {
                body.Release.TrySetResult();
                stop.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                response.Dispose();
            }
        });
        suite.Add("upstream ownership: late body closure errors remain visible after response removal", () =>
        {
            var client = Client(new StreamContent(new ThrowingDisposeBody()));
            var response = client.OpenBinaryStreamAsync("/image").GetAwaiter().GetResult();
            Check.Throws<Exception>(() => response.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
            Check.Throws<Exception>(() => client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
            Check.Throws<Exception>(() => client.Dispose());
        });
        suite.Add("upstream ownership: stopping buffered work remains cancellation, not an upstream timeout", () =>
        {
            var body = new BlockedBody();
            var client = Client(new StreamContent(body));
            var fetch = client.FetchBinaryAsync("/image");
            body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var stop = client.DisposeAsync().AsTask();
            try { Check.True(!stop.IsCompleted); }
            finally
            {
                body.Release.TrySetResult();
                Check.Throws<OperationCanceledException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                stop.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
        });
        suite.Add("upstream transport: real HTTP transport ignores ambient proxies and credentials", () =>
        {
            var proxy = new RecordingProxy();
            var previous = HttpClient.DefaultProxy;
            var transport = SuwayomiClient.CreateTransport(new AutoSourcarrSettings());
            Check.True(!transport.UseProxy && transport.Credentials is null);
            string? connectedHost = null;
            transport.ConnectCallback = (context, _) =>
            {
                connectedHost = context.DnsEndPoint.Host;
                return ValueTask.FromResult<Stream>(new HttpDuplex());
            };
            try
            {
                HttpClient.DefaultProxy = proxy;
                using var client = new SuwayomiClient(new AutoSourcarrSettings(), "http://source.test:4567", handler: transport);
                Check.Equal(0, client.GraphQlAsync("query { __typename }").WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult().Count);
                Check.Equal("source.test", connectedHost);
                Check.Equal(0, proxy.Calls);
            }
            finally { HttpClient.DefaultProxy = previous; }
        });
    }

    private static SuwayomiClient Client(HttpContent content) => new(new AutoSourcarrSettings(), "http://source.test:4567",
        handler: new Handler(content));
    private sealed class Handler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class RecordingProxy : IWebProxy
    {
        public int Calls;
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) { Calls++; return new("http://unwanted-proxy.invalid:9"); }
        public bool IsBypassed(Uri host) { Calls++; return false; }
    }

    private sealed class HttpDuplex : Stream
    {
        private readonly MemoryStream _input = new(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 11\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{\"data\":{}}"));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _input.Dispose(); base.Dispose(disposing); }
    }

    private sealed class CountingBody(byte[] bytes) : MemoryStream(bytes)
    {
        public int Disposals;
        protected override void Dispose(bool disposing) { if (disposing) Interlocked.Increment(ref Disposals); base.Dispose(disposing); }
    }

    private sealed class CountedContent(Stream body) : StreamContent(body)
    {
        public int Disposals;
        protected override void Dispose(bool disposing) { if (disposing) Interlocked.Increment(ref Disposals); base.Dispose(disposing); }
    }

    private sealed class BlockedDisposeBody : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Entered.TrySetResult();
                Check.True(Release.Task.Wait(TimeSpan.FromSeconds(5)));
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingDisposeBody : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) throw new IOException("controlled physical close failure");
        }
    }

    private sealed class BlockedBody : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false); // Deliberately ignores cancellation and disposal.
            buffer.Span[0] = 7;
            return 1;
        }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Interlocked.Increment(ref Disposals); base.Dispose(disposing); }
    }

    private sealed class BlockedContent : HttpContent
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CountingBody Body { get; } = new([1]);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false); // Deliberately ignores cancellation.
            return Body;
        }
    }
}
