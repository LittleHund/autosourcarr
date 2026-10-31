#!/usr/bin/env python3
"""A local stand-in for a Cloudflare JavaScript challenge, for the source host's WebView check in CI.

Without a cf_clearance cookie every page answers 503 with `Server: cloudflare` and a page whose script sets the
cookie (bound to the browser's user agent) and reloads, as Cloudflare's JS challenge does. With the cookie and the
same user agent the page answers 200. /stats reports what happened. Standard library only.
"""
import hashlib, http.server, json, sys, threading

stats = {"challenged": 0, "passed": 0, "wrong_agent": 0}
lock = threading.Lock()

def token(agent):
    return hashlib.sha256(("autosourcarr-test|" + (agent or "")).encode()).hexdigest()[:32]

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def send(self, code, body, kind="text/html; charset=utf-8"):
        data = body.encode()
        self.send_response(code); self.send_header("Server", "cloudflare"); self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)
    def do_GET(self):
        if self.path == "/stats":
            with lock: return self.send(200, json.dumps(stats), "application/json")
        agent = self.headers.get("User-Agent", "")
        cookies = dict(part.strip().split("=", 1) for part in self.headers.get("Cookie", "").split(";") if "=" in part)
        if cookies.get("cf_clearance") == token(agent):
            with lock: stats["passed"] += 1
            return self.send(200, "<html><head><title>Passed</title></head><body>challenge passed</body></html>")
        if "cf_clearance" in cookies:
            with lock: stats["wrong_agent"] += 1
        with lock: stats["challenged"] += 1
        # The browser computes the same token from its own user agent (SHA-256 via SubtleCrypto).
        script = """
<script>
(async () => {
  const data = new TextEncoder().encode('autosourcarr-test|' + navigator.userAgent);
  const hash = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', data))).map(b => b.toString(16).padStart(2, '0')).join('').slice(0, 32);
  await new Promise(resolve => setTimeout(resolve, 1500));
  document.cookie = 'cf_clearance=' + hash + '; path=/; max-age=3600';
  location.reload();
})();
</script>"""
        self.send(503, "<html><head><title>Just a moment...</title></head><body>Checking your browser" + script + "</body></html>")

if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8899
    http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
