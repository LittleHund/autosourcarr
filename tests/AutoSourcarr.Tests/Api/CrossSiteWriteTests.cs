using AutoSourcarr.Api.Http;

namespace AutoSourcarr.Tests;

public static class CrossSiteWriteTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("cross-site write guard: released request_origin.py rules", () =>
        {
            static bool Blocked(string method, string path, string site = "", string origin = "", string host = "nas.local:8282", string forwarded = "") =>
                CrossSiteWritePolicy.IsCrossSiteWrite(method, path, site, origin, host, forwarded);
            // Safe methods and unprotected paths are never blocked.
            Check.True(!Blocked("GET", "/api/admin/entries", "cross-site", "https://evil.example"));
            Check.True(!Blocked("POST", "/static/app.js", "cross-site", "https://evil.example"));
            Check.True(!Blocked("POST", "/api/setup/finish", "cross-site", "https://evil.example"));
            // Scripts and the extension host send neither header.
            Check.True(!Blocked("POST", "/api/admin/maintenance/reset-database"));
            Check.True(!Blocked("DELETE", "/api/v1/entries/1"));
            // Browser cross-site writes.
            Check.True(Blocked("POST", "/api/admin/maintenance/reset-database", "cross-site"));
            Check.True(Blocked("POST", "/api/admin/maintenance/reset-database", "", "https://evil.example"));
            Check.True(Blocked("POST", "/api/admin/maintenance/full-reset", "", "null"));
            Check.True(Blocked("PUT", "/api/v1/settings", "", "not a url"));
            // Same origin, directly and behind a reverse proxy.
            Check.True(!Blocked("POST", "/api/admin/maintenance/reset-database", "same-origin", "http://nas.local:8282"));
            Check.True(!Blocked("POST", "/api/admin/maintenance/reset-database", "", "HTTP://NAS.LOCAL:8282"));
            Check.True(!Blocked("POST", "/api/admin/sources/sync", "", "https://manga.example.org", "autosourcarr:8282", "manga.example.org, other.example"));
            Check.True(Blocked("POST", "/api/admin/sources/sync", "", "https://other.example", "autosourcarr:8282", "manga.example.org"));
            // Default ports: an Origin without an explicit port matches a Host without one.
            Check.True(!Blocked("POST", "/api/admin/sources/sync", "", "https://manga.example.org", "manga.example.org"));
            Check.True(Blocked("POST", "/api/admin/sources/sync", "", "https://manga.example.org", "manga.example.org:8282"));
        });
    }
}
