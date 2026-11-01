using System.Net;
using System.Net.Sockets;
using System.Text;
using AutoSourcarr.Core.Repair.Runner.Egress;

namespace AutoSourcarr.Tests;

public static class EgressProxyTests
{
    private const string Agent = "AutoSourcarr/0.4.3 (+https://github.com/LittleHund/autosourcarr; source repair)";
    private const string Job = "0123456789abcdef0123456789abcdef";

    public static void Register(TestSuite suite)
    {
        suite.Add("egress proxy: CONNECT to a host outside the allow-list gets 403 and is counted", () =>
        {
            var logged = new List<string>();
            var proxy = EgressProxy.StartAsync(["allowed.test"], Agent, line => { lock (logged) logged.Add(line); }).GetAwaiter().GetResult();
            try
            {
                var response = Exchange(proxy.Port, "CONNECT denied.test:443 HTTP/1.1\r\nHost: denied.test:443\r\n\r\n");
                Check.True(response.StartsWith("HTTP/1.1 403 ", StringComparison.Ordinal), response);
                Check.True(response.Contains("not on the egress allow-list", StringComparison.Ordinal), response);
                Check.True(!response.Contains("Via:", StringComparison.OrdinalIgnoreCase), "A refusal must not carry a Via header");
                Check.Equal(1L, proxy.Refused); Check.Equal(0L, proxy.Allowed);
                Check.True(proxy.RefusedHosts.SequenceEqual(new[] { "denied.test" }), string.Join(",", proxy.RefusedHosts));
                lock (logged) Check.True(logged.Any(line => line.Contains("refused denied.test:443", StringComparison.Ordinal)), string.Join("|", logged));
            }
            finally { proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        suite.Add("egress proxy: IP literals, other ports and lookalike hosts are refused", () =>
        {
            var proxy = EgressProxy.StartAsync(["allowed.test", "*.cdn.test"], Agent, null).GetAwaiter().GetResult();
            try
            {
                foreach (var target in new[] { "93.184.215.14:443", "[2606:2800:21f:cb07:6820:80da:af6b:8b2c]:443", "allowed.test:8443",
                             "x.allowed.test:443", "allowed.test.evil:443", "cdn.test:443", "evilcdn.test:443" })
                {
                    var response = Exchange(proxy.Port, "CONNECT " + target + " HTTP/1.1\r\n\r\n");
                    Check.True(response.StartsWith("HTTP/1.1 403 ", StringComparison.Ordinal), target + ": " + response);
                }
                Check.Equal(7L, proxy.Refused); Check.Equal(0L, proxy.Allowed);
                Check.True(proxy.IsAllowedHost("img.cdn.test") && proxy.IsAllowedHost("a.b.cdn.test") && proxy.IsAllowedHost("ALLOWED.test."));
                Check.True(!proxy.IsAllowedHost("cdn.test") && !proxy.IsAllowedHost("x.allowed.test") && !proxy.IsAllowedHost("127.0.0.1"));
            }
            finally { proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            Check.Throws<ArgumentException>(() => EgressProxy.StartAsync(["*.com"], Agent, null).GetAwaiter().GetResult());
            Check.Throws<ArgumentException>(() => EgressProxy.StartAsync(["10.0.0.1"], Agent, null).GetAwaiter().GetResult());
            Check.Throws<ArgumentException>(() => EgressProxy.StartAsync(["allowed.test"], "bad\r\nagent", null).GetAwaiter().GetResult());
        });

        suite.Add("egress proxy: a loopback answer is refused unless the test hook allows it", () =>
        {
            var proxy = EgressProxy.StartAsync(["allowed.test"], Agent, null,
                new EgressTestHooks(Resolve: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }), AllowAnyPort: true)).GetAwaiter().GetResult();
            try
            {
                var response = Exchange(proxy.Port, "GET http://allowed.test:8080/ HTTP/1.1\r\nHost: allowed.test:8080\r\n\r\n");
                Check.True(response.StartsWith("HTTP/1.1 403 ", StringComparison.Ordinal) && response.Contains("private or reserved", StringComparison.Ordinal), response);
                Check.Equal(1L, proxy.Refused);
            }
            finally { proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        suite.Add("egress proxy: plain HTTP strips forwarding headers and sends the honest User-Agent", () =>
        {
            using var origin = new Origin("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\nKeep-Alive: timeout=5\r\n\r\nok");
            var proxy = LoopbackProxy();
            try
            {
                var response = Exchange(proxy.Port,
                    $"GET http://allowed.test:{origin.Port}/path?q=1#frag HTTP/1.1\r\nHost: allowed.test:{origin.Port}\r\nUser-Agent: Mozilla/5.0 (pretend browser)\r\n" +
                    "Via: 1.1 upstream-spy\r\nForwarded: for=10.0.0.1\r\nX-Forwarded-For: 10.0.0.1\r\nX-Forwarded-Proto: http\r\nX-Real-IP: 10.0.0.1\r\n" +
                    "Proxy-Authorization: Basic eDp5\r\nProxy-Connection: keep-alive\r\nConnection: keep-alive, X-Hop\r\nX-Hop: drop-me\r\nAccept: text/plain\r\n\r\n");
                Check.True(response.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal), response);
                Check.True(response.EndsWith("\r\n\r\nok", StringComparison.Ordinal), response);
                Check.True(response.Contains("\r\nConnection: close\r\n", StringComparison.Ordinal) && !response.Contains("keep-alive", StringComparison.OrdinalIgnoreCase), response);
                Check.True(!response.Contains("Via:", StringComparison.OrdinalIgnoreCase), "The proxy must not add Via");
                var seen = origin.Request();
                var lines = seen.Split("\r\n");
                Check.Equal("GET /path?q=1 HTTP/1.1", lines[0]);
                Check.True(lines.Contains($"Host: allowed.test:{origin.Port}"), seen);
                Check.True(lines.Contains("User-Agent: " + Agent), seen);
                Check.True(lines.Count(line => line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) == 1, seen);
                Check.True(lines.Contains("Accept: text/plain") && lines.Contains("Connection: close"), seen);
                foreach (var banned in new[] { "Via:", "Forwarded:", "X-Forwarded-", "X-Real-IP:", "Proxy-", "X-Hop:", "Mozilla", "keep-alive" })
                    Check.True(!seen.Contains(banned, StringComparison.OrdinalIgnoreCase), "Origin saw " + banned + ":\n" + seen);
                Check.Equal(1L, proxy.Allowed); Check.Equal(0L, proxy.Refused);
            }
            finally { proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        suite.Add("egress proxy: POST forwards exactly the declared body; chunked and oversized heads are rejected", () =>
        {
            using var origin = new Origin("HTTP/1.1 201 Created\r\nContent-Length: 0\r\n\r\n");
            var proxy = LoopbackProxy();
            try
            {
                var response = Exchange(proxy.Port, $"POST http://allowed.test:{origin.Port}/form HTTP/1.1\r\nContent-Type: application/x-www-form-urlencoded\r\n" +
                    "Content-Length: 7\r\n\r\na=1&b=2GET http://allowed.test/smuggled HTTP/1.1\r\n\r\n");
                Check.True(response.StartsWith("HTTP/1.1 201 ", StringComparison.Ordinal), response);
                var seen = origin.Request();
                Check.True(seen.StartsWith("POST /form HTTP/1.1\r\n", StringComparison.Ordinal) && seen.Contains("\r\nContent-Length: 7\r\n", StringComparison.Ordinal), seen);
                Check.True(seen.EndsWith("\r\n\r\na=1&b=2", StringComparison.Ordinal) && !seen.Contains("smuggled", StringComparison.Ordinal), seen);
                var chunked = Exchange(proxy.Port, $"POST http://allowed.test:{origin.Port}/ HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");
                Check.True(chunked.StartsWith("HTTP/1.1 411 ", StringComparison.Ordinal), chunked);
                var huge = Exchange(proxy.Port, "GET http://allowed.test/ HTTP/1.1\r\nX-Big: " + new string('a', EgressProxy.MaximumHeadBytes) + "\r\n\r\n");
                Check.True(huge.StartsWith("HTTP/1.1 431 ", StringComparison.Ordinal), huge[..Math.Min(80, huge.Length)]);
                var folded = Exchange(proxy.Port, "GET http://allowed.test/ HTTP/1.1\r\nX-A: 1\r\n folded\r\n\r\n");
                Check.True(folded.StartsWith("HTTP/1.1 400 ", StringComparison.Ordinal), folded);
                var put = Exchange(proxy.Port, $"PUT http://allowed.test:{origin.Port}/ HTTP/1.1\r\nContent-Length: 0\r\n\r\n");
                Check.True(put.StartsWith("HTTP/1.1 403 ", StringComparison.Ordinal), put);
                Check.Equal(1L, proxy.Allowed);
            }
            finally { proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        suite.Add("egress proxy: allowed CONNECT tunnels bytes both ways to the pinned address", () =>
        {
            using var origin = new Origin("pong", rawEcho: true);
            var proxy = LoopbackProxy();
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, proxy.Port);
                client.ReceiveTimeout = 10_000;
                var stream = client.GetStream();
                stream.Write(Encoding.ASCII.GetBytes($"CONNECT allowed.test:{origin.Port} HTTP/1.1\r\n\r\nping"));
                var text = ReadUntil(stream, "pong");
                Check.True(text.StartsWith("HTTP/1.1 200 Connection established\r\n\r\n", StringComparison.Ordinal), text);
                Check.Equal("ping", origin.Request());
                Check.Equal(1L, proxy.Allowed);
            }
            finally { proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });

        suite.Add("isolated runner proof: exact evidence parses; weakened or foreign evidence is refused", () =>
        {
            const string Body = "{\"pid\":42,\"uid\":568,\"ppid\":7,\"pdeathsig\":9,\"landlock\":6,\"nnp\":1,\"seccomp\":2,\"netgate\":1,\"proxy\":40000,\"scoped\":3}";
            var proof = IsolatedRunner.ParseProof("AUTOSOURCARR_RUNNER_V1 " + Job + " " + Body, 42, 568, 7, Job, 40000);
            Check.True(proof is { LandlockAbi: 6, ProxyPort: 40000, Scoped: true }, "valid proof rejected");
            // Landlock ABI 6 (signal scoping) is required: ABI 4/5 proofs and unscoped proofs are refused.
            foreach (var body in new[]
            {
                Body.Replace("\"landlock\":6", "\"landlock\":5", StringComparison.Ordinal), Body.Replace("\"landlock\":6", "\"landlock\":4", StringComparison.Ordinal),
                Body.Replace("\"scoped\":3", "\"scoped\":0", StringComparison.Ordinal), Body.Replace("\"netgate\":1", "\"netgate\":0", StringComparison.Ordinal),
                Body.Replace("\"seccomp\":2", "\"seccomp\":0", StringComparison.Ordinal), Body.Replace("\"proxy\":40000", "\"proxy\":40001", StringComparison.Ordinal),
                Body.Replace("\"uid\":568", "\"uid\":0", StringComparison.Ordinal), Body.Replace("\"ppid\":7", "\"ppid\":8", StringComparison.Ordinal),
                Body[..^1] + ",\"extra\":1}", Body[..^1] + ",\"pid\":42}", "[]"
            })
                Check.True(IsolatedRunner.ParseProof("AUTOSOURCARR_RUNNER_V1 " + Job + " " + body, 42, 568, 7, Job, 40000) is null, body);
            Check.True(IsolatedRunner.ParseProof("AUTOSOURCARR_RUNNER_V1 " + new string('f', 32) + " " + Body, 42, 568, 7, Job, 40000) is null, "foreign job accepted");
            Check.True(IsolatedRunner.ParseProof("AUTOSOURCARR_SANDBOX_V2 " + Job + " " + Body, 42, 568, 7, Job, 40000) is null, "launcher proof accepted");
        });
    }

    private static EgressProxy LoopbackProxy() =>
        EgressProxy.StartAsync(["allowed.test"], Agent, null, new EgressTestHooks(
            Resolve: (host, _) => Task.FromResult(host == "allowed.test" ? new[] { IPAddress.Loopback } : Array.Empty<IPAddress>()),
            AllowNonGlobalTargets: true, AllowAnyPort: true)).GetAwaiter().GetResult();

    private static string Exchange(int port, string request)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        client.ReceiveTimeout = 15_000; client.SendTimeout = 15_000;
        var stream = client.GetStream();
        try { stream.Write(Encoding.Latin1.GetBytes(request)); }
        catch (IOException) { /* The proxy may answer and close before reading an oversized head. */ }
        using var buffer = new MemoryStream();
        try { stream.CopyTo(buffer); }
        catch (IOException) when (buffer.Length > 0) { }
        return Encoding.Latin1.GetString(buffer.ToArray());
    }

    private static string ReadUntil(Stream stream, string marker)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var read = stream.Read(buffer);
            if (read == 0) break;
            text.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }
        return text.ToString();
    }

    /// <summary>One-shot loopback origin that records the request it receives.</summary>
    private sealed class Origin : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task<string> _request;
        public Origin(string reply, bool rawEcho = false)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _request = Task.Run(() => Serve(reply, rawEcho));
        }
        public int Port { get; }
        public string Request() => _request.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        private string Serve(string reply, bool rawEcho)
        {
            using var client = _listener.AcceptTcpClient();
            client.ReceiveTimeout = 10_000;
            var stream = client.GetStream();
            var received = new StringBuilder();
            var buffer = new byte[4096];
            while (true)
            {
                var text = received.ToString();
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (rawEcho && text.Length >= 4) break;
                if (end >= 0)
                {
                    var length = text.Split("\r\n").Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        .Select(line => int.Parse(line[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
                    if (text.Length >= end + 4 + length) break;
                }
                var read = stream.Read(buffer);
                if (read == 0) break;
                received.Append(Encoding.Latin1.GetString(buffer, 0, read));
            }
            stream.Write(Encoding.Latin1.GetBytes(reply));
            if (!rawEcho)
            {
                // Ignore the request's Connection: close for a moment to prove the proxy closes anyway.
                Thread.Sleep(100);
            }
            return received.ToString();
        }
        public void Dispose() => _listener.Stop();
    }
}
