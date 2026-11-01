using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Connections;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Jobs;

namespace AutoSourcarr.Tests.Connections;

/// <summary>
/// Phase 7 integration connection types: each type's Test passes against a stub and fails clearly on a wrong body,
/// 401 or 5xx; the AniList limiter (30/min, Retry-After, X-RateLimit-Remaining), metadata mapping, Komf search,
/// the MalwareBazaar lookup and APK-preview hook, and the one-time Suwayomi import plan.
/// </summary>
public static class IntegrationConnectionTests
{
    private const string AniListMedia = """
        {"data":{"Page":{"media":[{"id":30002,"idMal":2,"siteUrl":"https://anilist.co/manga/30002","format":"MANGA","status":"RELEASING",
         "countryOfOrigin":"JP","isAdult":false,"genres":["Action"],"synonyms":["Berserk: The Prototype"],"startDate":{"year":1989},
         "title":{"romaji":"Berserk","english":"Berserk","native":"ベルセルク","userPreferred":"Berserk"},
         "coverImage":{"extraLarge":"https://img.test/b.jpg"},"description":"<b>Guts</b> &amp; Griffith",
         "tags":[{"name":"Dark Fantasy","isAdult":false,"rank":95},{"name":"Rare","isAdult":false,"rank":10}],
         "staff":{"edges":[{"role":"Story & Art","node":{"name":{"full":"Kentarou Miura"}}}]}}]}}}
        """;

    public static void Register(TestSuite suite)
    {
        suite.Add("connections integration: every phase 7 type registers with valid ids and field keys", () =>
        {
            var types = new ConnectionTypes(IntegrationConnectionTypes.All);
            foreach (var id in new[] { "anilist", "mangaupdates", "mangadex", "komf", "malwarebazaar", "suwayomi" })
                Check.True(types.Contains(id), "Registered: " + id);
            Check.True(!types.Find("suwayomi")!.Multiple, "One main Suwayomi only.");
            Check.Equal(ConnectionFieldKind.Secret, types.Find("malwarebazaar")!.Fields.Single(field => field.Key == "auth_key").Kind);
        });

        suite.Add("connections integration: AniList Test passes against a stub and reports latency and the remaining rate limit", () =>
        {
            string? body = null, agent = null;
            using var handler = new Handler(async (request, token) =>
            {
                Check.Equal(HttpMethod.Post, request.Method);
                Check.Equal("http://anilist.test/", request.RequestUri!.AbsoluteUri);
                agent = request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(" ", agents) : "";
                body = await request.Content!.ReadAsStringAsync(token);
                return Json(AniListMedia, headers: [("X-RateLimit-Limit", "30"), ("X-RateLimit-Remaining", "29")]);
            });
            var result = Run(new AniListConnectionType(), Config("anilist", ("base_url", "http://anilist.test")), handler);
            Check.True(result.Ok, result.Message);
            Check.True(body!.Contains("\"search\":\"Berserk\"", StringComparison.Ordinal) && body!.Contains("\"perPage\":1", StringComparison.Ordinal), body!);
            Check.True(agent!.Contains("AutoSourcarr", StringComparison.Ordinal), "User-Agent identifies the app: " + agent);
            Check.Equal(29, result.Details!["rate_limit"]!["remaining"]!.GetValue<int>());
            Check.True(result.Details!["latency_ms"] is not null, "Latency is reported.");
            Check.Equal("Berserk", result.Details!["sample"]!["title"]!.GetValue<string>());
            Check.True(result.Message.Contains("29 requests left", StringComparison.Ordinal), result.Message);
        });

        suite.Add("connections integration: AniList Test fails clearly on 401, 5xx and a wrong body", () =>
        {
            var type = new AniListConnectionType();
            var config = Config("anilist", ("base_url", "http://anilist-fail.test"), ("api_token", "bad-token"));
            string? authorization = null;
            using (var unauthorized = new Handler((request, _) =>
            {
                authorization = request.Headers.Authorization?.ToString();
                return Task.FromResult(Json("""{"errors":[{"message":"Invalid token","status":401}]}""", HttpStatusCode.Unauthorized));
            }))
            {
                var result = Run(type, config, unauthorized);
                Check.True(!result.Ok && result.Message.Contains("rejected the credentials", StringComparison.Ordinal), result.Message);
                Check.Equal("Bearer bad-token", authorization);
                Check.Equal(401, result.Details!["http_status"]!.GetValue<int>());
            }
            using (var broken = new Handler((_, _) => Task.FromResult(Json("""{"errors":[{"message":"down"}]}""", HttpStatusCode.InternalServerError))))
            {
                var result = Run(type, config, broken);
                Check.True(!result.Ok && result.Message.Contains("HTTP 500", StringComparison.Ordinal), result.Message);
            }
            using (var wrong = new Handler((_, _) => Task.FromResult(Json("""{"data":{"Viewer":{}}}"""))))
            {
                var result = Run(type, config, wrong);
                Check.True(!result.Ok && result.Message.Contains("unexpectedly", StringComparison.Ordinal), result.Message);
            }
            using (var html = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") })))
            {
                var result = Run(type, config, html);
                Check.True(!result.Ok && result.Message.Contains("not JSON", StringComparison.Ordinal), result.Message);
            }
            var invalid = Run(type, Config("anilist", ("base_url", "ftp://x")), new Handler((_, _) => throw new InvalidOperationException("no request expected")));
            Check.True(!invalid.Ok && invalid.Message.Contains("http", StringComparison.Ordinal), invalid.Message);
        });

        suite.Add("connections integration: AniList maps titles, aliases, year, people, cover and MAL link; get by id", () =>
        {
            using var handler = new Handler(async (request, token) =>
            {
                var body = await request.Content!.ReadAsStringAsync(token);
                if (body.Contains("\"id\":404", StringComparison.Ordinal))
                    return Json("""{"errors":[{"message":"Not Found.","status":404}],"data":{"Media":null}}""", HttpStatusCode.NotFound);
                return Json(body.Contains("Media(id:", StringComparison.Ordinal)
                    ? """{"data":{"Media":{"id":30002,"title":{"romaji":"Berserk"}}}}""" : AniListMedia);
            });
            using var client = new AniListClient(handler, Options("http://anilist-map.test/"));
            var found = client.SearchAsync("  Berserk ").GetAwaiter().GetResult().Single();
            Check.Equal("anilist", found.Provider);
            Check.Equal("30002", found.ExternalId);
            Check.Equal("Berserk", found.Title);
            Check.Equal(1989, found.Year);
            Check.True(found.AlternativeTitles.Contains("ベルセルク") && found.AlternativeTitles.Contains("Berserk: The Prototype"), string.Join("|", found.AlternativeTitles));
            Check.Equal("Kentarou Miura", found.Authors.Single());
            Check.Equal("Kentarou Miura", found.Artists.Single());
            Check.Equal("https://img.test/b.jpg", found.CoverUrl);
            Check.Equal("Guts & Griffith", found.Description);
            Check.Equal("ongoing", found.Status);
            Check.Equal("2", found.Links["myanimelist"]);
            Check.True(found.Genres.Contains("Dark Fantasy") && !found.Genres.Contains("Rare"), string.Join("|", found.Genres));
            Check.Equal("Berserk", client.GetAsync("30002").GetAwaiter().GetResult()!.Title);
            Check.True(client.GetAsync("404").GetAwaiter().GetResult() is null, "An unknown id is null.");
            Check.Throws<ArgumentException>(() => client.GetAsync("abc").GetAwaiter().GetResult());
        });

        suite.Add("connections integration: AniList limiter lets 30 requests through and makes the 31st wait", () =>
        {
            // Same shape as production (30 per window) with a 30 s window: the 31st waits up to 1 s for one refilled token.
            var limiter = new TokenBucketLimiter(30, TimeSpan.FromSeconds(30));
            var calls = 0;
            using var handler = new Handler((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(Json(AniListMedia)); });
            using var client = new AniListClient(handler, Options("http://anilist-limit.test/") with { Limiter = limiter });
            var burst = Stopwatch.StartNew();
            for (var index = 0; index < 30; index++) client.SearchAsync("Berserk", 1).GetAwaiter().GetResult();
            Check.True(burst.Elapsed < TimeSpan.FromMilliseconds(900), "The first 30 are not delayed: " + burst.ElapsedMilliseconds + " ms");
            Check.Equal(0, limiter.Available);
            var thirtyFirst = Stopwatch.StartNew();
            client.SearchAsync("Berserk", 1).GetAwaiter().GetResult();
            Check.True(thirtyFirst.Elapsed >= TimeSpan.FromMilliseconds(50), "The 31st request waited: " + thirtyFirst.ElapsedMilliseconds + " ms");
            Check.Equal(31, calls);
            Check.Equal(30, AniListClient.RequestsPerMinute);
            Check.Equal(30, AniListClient.SharedLimiter(AniListClient.DefaultBaseUri).Capacity);
            Check.Equal(TimeSpan.FromMinutes(1), AniListClient.SharedLimiter(AniListClient.DefaultBaseUri).Period);
            // Bounded: a wait longer than allowed fails at once instead of queueing.
            var slow = new TokenBucketLimiter(1, TimeSpan.FromMinutes(1));
            Check.True(slow.TryAcquire(out _), "First token.");
            var error = Check.Throws<MetadataRateLimitedException>(() => slow.WaitAsync("anilist", TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult());
            Check.True(error.RetryAfter > TimeSpan.FromSeconds(30), error.Message);
        });

        suite.Add("connections integration: AniList retries a 429 after Retry-After: 1 and fails clearly when Retry-After is too long", () =>
        {
            var calls = 0;
            using var handler = new Handler((_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? Json("""{"errors":[{"message":"Too Many Requests.","status":429}]}""", (HttpStatusCode)429, [("Retry-After", "1"), ("X-RateLimit-Remaining", "0")])
                : Json(AniListMedia)));
            using var client = new AniListClient(handler, Options("http://anilist-429.test/") with { Limiter = new TokenBucketLimiter(30, TimeSpan.FromMinutes(1)) });
            var watch = Stopwatch.StartNew();
            Check.Equal(1, client.SearchAsync("Berserk").GetAwaiter().GetResult().Count);
            Check.Equal(2, calls);
            Check.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900), "Retry-After was honoured: " + watch.ElapsedMilliseconds + " ms");

            var longCalls = 0;
            using var limited = new Handler((_, _) =>
            {
                Interlocked.Increment(ref longCalls);
                return Task.FromResult(Json("{}", (HttpStatusCode)429, [("Retry-After", "120")]));
            });
            using var bounded = new AniListClient(limited, Options("http://anilist-429b.test/") with { Limiter = new TokenBucketLimiter(30, TimeSpan.FromMinutes(1)), MaxRateWait = TimeSpan.FromSeconds(5) });
            watch.Restart();
            var error = Check.Throws<MetadataRateLimitedException>(() => bounded.SearchAsync("Berserk").GetAwaiter().GetResult());
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(3), "No 120 s wait: " + watch.ElapsedMilliseconds + " ms");
            Check.Equal(1, longCalls);
            Check.True(error.Message.Contains("retry in 120 s", StringComparison.Ordinal), error.Message);
            // The Test reports the limit instead of waiting.
            var test = Run(new AniListConnectionType(), Config("anilist", ("base_url", "http://anilist-429c.test")), limited);
            Check.True(!test.Ok && test.Message.Contains("rate limiting", StringComparison.Ordinal), test.Message);
        });

        suite.Add("connections integration: AniList respects X-RateLimit-Remaining: 0 until the reported reset", () =>
        {
            var calls = 0;
            var reset = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var handler = new Handler((_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? Json(AniListMedia, headers: [("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", reset)])
                : Json(AniListMedia, headers: [("X-RateLimit-Remaining", "28")])));
            var limiter = new TokenBucketLimiter(30, TimeSpan.FromMinutes(1));
            using var client = new AniListClient(handler, Options("http://anilist-remaining.test/") with { Limiter = limiter });
            client.SearchAsync("Berserk").GetAwaiter().GetResult();
            Check.Equal(0, limiter.Available);
            Check.Equal(0, client.RateStatus.Remaining);
            var watch = Stopwatch.StartNew();
            client.SearchAsync("Berserk").GetAwaiter().GetResult();
            Check.True(watch.Elapsed >= TimeSpan.FromMilliseconds(500), "The second request waited for the reset: " + watch.ElapsedMilliseconds + " ms");
            Check.Equal(28, client.RateStatus.Remaining);
        });

        suite.Add("connections integration: MangaUpdates Test passes against a stub and maps v1 series records; fails on 503", () =>
        {
            string? path = null, body = null;
            using var handler = new Handler(async (request, token) =>
            {
                path = request.RequestUri!.AbsolutePath;
                body = await request.Content!.ReadAsStringAsync(token);
                return Json("""
                    {"total_hits":1,"page":1,"per_page":1,"results":[{"record":{"series_id":58712130471,"title":"Berserk",
                     "url":"https://www.mangaupdates.com/series/qtm8hlz/berserk","associated":[{"title":"ベルセルク"},{"title":"Berserk (Prototype)"}],
                     "description":"Guts","image":{"url":{"original":"https://cdn.mangaupdates.com/image/i1.jpg","thumb":"https://cdn.mangaupdates.com/image/thumb/i1.jpg"}},
                     "type":"Manga","year":"1989","status":"41 Volumes (Ongoing)","genres":[{"genre":"Action"},{"genre":"Drama"}]},"hit_title":"Berserk"}]}
                    """);
            });
            var type = new MangaUpdatesConnectionType();
            var result = Run(type, Config("mangaupdates", ("base_url", "http://mu.test/v1")), handler);
            Check.True(result.Ok, result.Message);
            Check.Equal("/v1/series/search", path);
            Check.True(body!.Contains("\"perpage\":1", StringComparison.Ordinal), body!);
            using var client = new MangaUpdatesClient(handler, Options("http://mu.test/v1"));
            var found = client.SearchAsync("Berserk").GetAwaiter().GetResult().Single();
            Check.Equal("58712130471", found.ExternalId);
            Check.Equal(1989, found.Year);
            Check.Equal("https://cdn.mangaupdates.com/image/i1.jpg", found.CoverUrl);
            Check.Equal("ongoing", found.Status);
            Check.True(found.AlternativeTitles.Contains("Berserk (Prototype)"), string.Join("|", found.AlternativeTitles));
            Check.True(found.Genres.SequenceEqual(["Action", "Drama"]), string.Join("|", found.Genres));

            using var down = new Handler((_, _) => Task.FromResult(Json("""{"status":"exception","reason":"maintenance"}""", HttpStatusCode.ServiceUnavailable)));
            var failed = Run(type, Config("mangaupdates", ("base_url", "http://mu-down.test/v1")), down);
            Check.True(!failed.Ok && failed.Message.Contains("HTTP 503", StringComparison.Ordinal), failed.Message);
            using var wrong = new Handler((_, _) => Task.FromResult(Json("""{"hello":"world"}""")));
            var unexpected = Run(type, Config("mangaupdates", ("base_url", "http://mu-wrong.test/v1")), wrong);
            Check.True(!unexpected.Ok && unexpected.Message.Contains("unexpectedly", StringComparison.Ordinal), unexpected.Message);
        });

        suite.Add("connections integration: MangaDex Test passes against a stub with includes and ratings; get by id; error envelope fails", () =>
        {
            const string Id = "801513ba-a712-498c-8f57-cae55b38cc92";
            string? query = null;
            using var handler = new Handler((request, _) =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath == "/manga/" + "00000000-0000-0000-0000-000000000000")
                    return Task.FromResult(Json("""{"result":"error","errors":[{"status":404,"title":"Not found"}]}""", HttpStatusCode.NotFound));
                query = uri.Query;
                var record = """
                    {"id":"801513ba-a712-498c-8f57-cae55b38cc92","type":"manga","attributes":{"title":{"en":"Berserk"},
                     "altTitles":[{"ja":"ベルセルク"},{"ja-ro":"Beruseruku"}],"description":{"en":"Guts"},"links":{"al":"30002","mal":"2","mu":"qtm8hlz"},
                     "originalLanguage":"ja","status":"ongoing","year":1989,"contentRating":"suggestive","publicationDemographic":"seinen",
                     "tags":[{"attributes":{"name":{"en":"Action"}}}]},
                     "relationships":[{"type":"author","attributes":{"name":"Miura Kentarou"}},{"type":"artist","attributes":{"name":"Miura Kentarou"}},
                      {"type":"cover_art","attributes":{"fileName":"cover.jpg"}}]}
                    """;
                return Task.FromResult(Json(uri.AbsolutePath == "/manga/" + Id ? """{"result":"ok","data":""" + record + "}" : """{"result":"ok","data":[""" + record + "]}"));
            });
            var type = new MangaDexConnectionType();
            var result = Run(type, Config("mangadex", ("base_url", "http://md.test")), handler);
            Check.True(result.Ok, result.Message);
            var first = query!;
            Check.True(first.Contains("title=Berserk", StringComparison.Ordinal) && first.Contains("includes%5B%5D=cover_art", StringComparison.Ordinal)
                && first.Contains("contentRating%5B%5D=pornographic", StringComparison.Ordinal), first);
            using var client = new MangaDexClient(handler, Options("http://md.test/") with { IncludeAdult = false });
            var found = client.GetAsync(Id.ToUpperInvariant()).GetAwaiter().GetResult()!;
            Check.Equal(MangaDexClient.CoverHost + "/covers/" + Id + "/cover.jpg.512.jpg", found.CoverUrl);
            Check.Equal("30002", found.Links["anilist"]);
            Check.Equal("Miura Kentarou", found.Authors.Single());
            Check.True(found.AlternativeTitles.Contains("Beruseruku"), string.Join("|", found.AlternativeTitles));
            client.SearchAsync("Berserk").GetAwaiter().GetResult();
            Check.True(!query!.Contains("pornographic", StringComparison.Ordinal), "Adult rating left out when disabled: " + query);
            Check.True(client.GetAsync("00000000-0000-0000-0000-000000000000").GetAwaiter().GetResult() is null, "Unknown id is null.");
            Check.Throws<ArgumentException>(() => client.GetAsync("not-a-uuid").GetAwaiter().GetResult());

            using var error = new Handler((_, _) => Task.FromResult(Json("""{"result":"error","errors":[{"status":400,"detail":"title must be a string"}]}""")));
            var failed = Run(type, Config("mangadex", ("base_url", "http://md-error.test")), error);
            Check.True(!failed.Ok && failed.Message.Contains("title must be a string", StringComparison.Ordinal), failed.Message);
            using var unauthorized = new Handler((_, _) => Task.FromResult(Json("""{"result":"error"}""", HttpStatusCode.Unauthorized)));
            var denied = Run(type, Config("mangadex", ("base_url", "http://md-401.test"), ("api_token", "x")), unauthorized);
            Check.True(!denied.Ok && denied.Message.Contains("rejected the credentials", StringComparison.Ordinal), denied.Message);
        });

        suite.Add("connections integration: Komf Test lists enabled providers; search maps results; non-Komf answers fail", () =>
        {
            using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/kavita/metadata/providers" => Json("""["MANGA_UPDATES","ANILIST"]"""),
                "/api/kavita/media-server/connected" => Json("""{"success":false,"httpStatusCode":401,"errorMessage":"Kavita refused"}"""),
                "/api/kavita/metadata/search" when request.RequestUri!.Query == "?name=Berserk%20Deluxe" => Json("""
                    [{"url":"https://anilist.co/manga/30002","imageUrl":null,"title":"Berserk","provider":"ANILIST","resultId":"30002"},
                     {"url":null,"title":"","provider":"MANGADEX","resultId":"x"}]
                    """),
                _ => Json("""{"message":"not found"}""", HttpStatusCode.NotFound),
            }));
            var type = new KomfConnectionType();
            var result = Run(type, Config("komf", ("url", "http://komf.test:8085"), ("media_server", "Kavita")), handler);
            Check.True(result.Ok, result.Message);
            Check.True(result.Message.Contains("MANGA_UPDATES, ANILIST", StringComparison.Ordinal), result.Message);
            Check.Equal(false, result.Details!["media_server_connected"]!.GetValue<bool>());
            using var client = KomfConnectionType.CreateClient(Config("komf", ("url", "http://komf.test:8085"), ("media_server", "kavita")), handler);
            var found = client.SearchAsync("Berserk Deluxe").GetAwaiter().GetResult().Single();
            Check.Equal(new KomfSearchResult("Berserk", "ANILIST", "30002", "https://anilist.co/manga/30002", ""), found);

            var komga = Run(type, Config("komf", ("url", "http://komf.test:8085")), handler);
            Check.True(!komga.Ok && komga.Message.Contains("HTTP 404", StringComparison.Ordinal), komga.Message);
            using var other = new Handler((_, _) => Task.FromResult(Json("""{"status":"UP"}""")));
            var wrong = Run(type, Config("komf", ("url", "http://not-komf.test")), other);
            Check.True(!wrong.Ok && wrong.Message.Contains("not like Komf", StringComparison.Ordinal), wrong.Message);
            var badServer = Run(type, Config("komf", ("url", "http://komf.test"), ("media_server", "plex")), other);
            Check.True(!badServer.Ok && badServer.Message.Contains("komga or kavita", StringComparison.Ordinal), badServer.Message);
        });

        suite.Add("connections integration: MalwareBazaar Test sends Auth-Key and the documented test hash; 401 and unknown statuses fail", () =>
        {
            string? key = null, form = null;
            using var handler = new Handler(async (request, token) =>
            {
                key = request.Headers.TryGetValues("Auth-Key", out var values) ? values.Single() : null;
                form = await request.Content!.ReadAsStringAsync(token);
                return Json("""{"query_status":"hash_not_found"}""");
            });
            var type = new MalwareBazaarConnectionType();
            var result = Run(type, Config("malwarebazaar", ("auth_key", "k-123"), ("base_url", "http://mb.test/api/v1")), handler);
            Check.True(result.Ok, result.Message);
            Check.Equal("k-123", key);
            Check.Equal("query=get_info&hash=" + MalwareBazaarConnectionType.TestHash, form);
            Check.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("AutoSourcarr MalwareBazaar connection test"))),
                MalwareBazaarConnectionType.TestHash);

            using var unauthorized = new Handler((_, _) => Task.FromResult(Json("""{"query_status":"unknown_auth_key"}""", HttpStatusCode.Unauthorized)));
            var denied = Run(type, Config("malwarebazaar", ("auth_key", "wrong")), unauthorized);
            Check.True(!denied.Ok && denied.Message.Contains("rejected the credentials", StringComparison.Ordinal), denied.Message);
            using var status = new Handler((_, _) => Task.FromResult(Json("""{"query_status":"wrong_auth_key"}""")));
            var refused = Run(type, Config("malwarebazaar", ("auth_key", "wrong")), status);
            Check.True(!refused.Ok && refused.Message.Contains("rejected the credentials", StringComparison.Ordinal), refused.Message);
            using var server = new Handler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.BadGateway)));
            var broken = Run(type, Config("malwarebazaar", ("auth_key", "k")), server);
            Check.True(!broken.Ok && broken.Message.Contains("HTTP 502", StringComparison.Ordinal), broken.Message);
            var missing = Run(type, Config("malwarebazaar"), server);
            Check.True(!missing.Ok && missing.Message.Contains("Auth-Key", StringComparison.Ordinal), missing.Message);
        });

        suite.Add("connections integration: MalwareBazaar lookup and the APK preview hook (known → warning, slow → skipped, never blocks)", () =>
        {
            const string Hash = "275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f";
            using var known = new Handler((_, _) => Task.FromResult(Json("""
                {"query_status":"ok","data":[{"sha256_hash":"275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f","file_name":"eicar.apk",
                  "signature":"EICAR-Test","tags":["eicar","test"],"first_seen":"2020-01-01 00:00:00"}]}
                """)));
            using var client = new MalwareBazaarClient("k", known, new Uri("http://mb.test/api/v1/"));
            var lookup = client.LookupAsync(Hash.ToUpperInvariant()).GetAwaiter().GetResult();
            Check.True(lookup.Known, "Known sample.");
            Check.Equal("EICAR-Test", lookup.Signature);
            Check.True(lookup.Tags.SequenceEqual(["eicar", "test"]), string.Join(",", lookup.Tags));
            Check.Equal("2020-01-01 00:00:00", lookup.FirstSeen);
            Check.Throws<ArgumentException>(() => client.LookupAsync("abc").GetAwaiter().GetResult());
            var (json, warning) = MalwareBazaarPreview.CheckAsync(client, Hash, MalwareBazaarPreview.DefaultBound, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(json!["known"]!.GetValue<bool>(), "Lookup JSON says known.");
            Check.True(warning!.Contains("EICAR-Test", StringComparison.Ordinal) && warning!.Contains("eicar, test", StringComparison.Ordinal), warning!);

            using var slow = new Handler(async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(30), token); return Json("{}"); });
            using var slowClient = new MalwareBazaarClient("k", slow, new Uri("http://mb.test/api/v1/"));
            var watch = Stopwatch.StartNew();
            var (none, skipped) = MalwareBazaarPreview.CheckAsync(slowClient, Hash, TimeSpan.FromMilliseconds(200), CancellationToken.None).GetAwaiter().GetResult();
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(3), "Bounded: " + watch.ElapsedMilliseconds + " ms");
            Check.True(none is null && skipped!.Contains("skipped", StringComparison.Ordinal), skipped ?? "no warning");
            using var unknown = new Handler((_, _) => Task.FromResult(Json("""{"query_status":"hash_not_found"}""")));
            using var cleanClient = new MalwareBazaarClient("k", unknown, new Uri("http://mb.test/api/v1/"));
            var (clean, noWarning) = MalwareBazaarPreview.CheckAsync(cleanClient, Hash, MalwareBazaarPreview.DefaultBound, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(clean is not null && !clean["known"]!.GetValue<bool>() && noWarning is null, "Unknown hash: no warning.");
            Check.True(MalwareBazaarPreview.CheckAsync(null, Hash, MalwareBazaarPreview.DefaultBound, CancellationToken.None).GetAwaiter().GetResult() == (null, null),
                "No connection: no lookup and no warning.");
        });

        suite.Add("connections integration: Suwayomi Test reads aboutServer and library size with Basic auth; 401 and non-Suwayomi fail", () =>
        {
            string? authorization = null, body = null;
            using var handler = new Handler(async (request, token) =>
            {
                Check.Equal("/api/graphql", request.RequestUri!.AbsolutePath);
                authorization = request.Headers.Authorization?.ToString();
                body = await request.Content!.ReadAsStringAsync(token);
                return Json("""{"data":{"aboutServer":{"name":"Suwayomi-Server","version":"v2.4.2366","revision":"r2366","buildType":"Stable"},"mangas":{"totalCount":412}}}""");
            });
            var type = new SuwayomiConnectionType();
            var result = Run(type, Config("suwayomi", ("url", "http://suwayomi.test:4567"), ("username", "me"), ("password", "pw")), handler);
            Check.True(result.Ok, result.Message);
            Check.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me:pw")), authorization);
            Check.True(body!.Contains("aboutServer", StringComparison.Ordinal) && !body!.Contains("mutation", StringComparison.Ordinal), body!);
            Check.Equal(412, result.Details!["library_count"]!.GetValue<int>());
            Check.True(result.Message.Contains("v2.4.2366", StringComparison.Ordinal) && result.Message.Contains("412 manga", StringComparison.Ordinal), result.Message);

            using var unauthorized = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
            var denied = Run(type, Config("suwayomi", ("url", "http://suwayomi.test:4567")), unauthorized);
            Check.True(!denied.Ok && denied.Message.Contains("rejected the credentials", StringComparison.Ordinal), denied.Message);
            using var other = new Handler((_, _) => Task.FromResult(Json("""{"data":{"aboutServer":null}}""")));
            var wrong = Run(type, Config("suwayomi", ("url", "http://other.test")), other);
            Check.True(!wrong.Ok && wrong.Message.Contains("not like Suwayomi", StringComparison.Ordinal), wrong.Message);
            using var failing = new Handler((_, _) => Task.FromResult(Json("""{"errors":[{"message":"Validation error"}]}""")));
            var error = Run(type, Config("suwayomi", ("url", "http://other.test")), failing);
            Check.True(!error.Ok && error.Message.Contains("Validation error", StringComparison.Ordinal), error.Message);
        });

        suite.Add("connections integration: Suwayomi one-time import stores a plan awaiting matching and does not run twice", () =>
        {
            using var temp = new Sandbox();
            var library = """
                {"data":{"mangas":{"nodes":[
                  {"id":2,"sourceId":"111","url":"/manga/b","realUrl":"https://b.test/manga/b","title":"B","thumbnailUrl":"/t/2","source":{"id":"111","name":"Bee","lang":"en"}},
                  {"id":1,"sourceId":"111","url":"/manga/a","realUrl":null,"title":"A","thumbnailUrl":null,"source":{"id":"111","name":"Bee","lang":"en"}},
                  {"id":3,"sourceId":"222","url":"/manga/c","title":"C","source":null},
                  {"id":4,"sourceId":"","url":"","title":"broken"}],
                 "pageInfo":{"hasNextPage":false,"endCursor":null}}}}
                """;
            using var handler = new Handler((_, _) => Task.FromResult(Json(library)));
            using var graph = new SuwayomiGraphQlHttp(new SuwayomiEndpoint(new Uri("http://suwayomi.test/")), handler);
            var at = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
            var plan = SuwayomiImport.ReadAsync(graph, at, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(3, plan.Total);
            Check.Equal(2, plan.Sources);
            Check.Equal(SuwayomiImport.AwaitingMatching, plan.Status);
            Check.True(plan.Items.Select(item => item.MangaId).SequenceEqual([1L, 2L, 3L]), "Sorted by manga id, broken rows left out.");
            Check.Equal("Bee", plan.BySource[0].SourceName);
            Check.Equal(2, plan.BySource[0].Count);

            var tasks = new TaskRegistry();
            var jobs = new JobQueue(tasks);
            try
            {
                var reads = 0;
                var first = SuwayomiImport.Enqueue(jobs, temp.Path, token => { reads++; return SuwayomiImport.ReadAsync(graph, at, token); });
                Check.Equal(JobOutcome.Completed, first.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
                Check.True(tasks.Find(first.Id)!.Message.Contains("awaiting", StringComparison.OrdinalIgnoreCase)
                    && tasks.Find(first.Id)!.Message.Contains("phase 8", StringComparison.Ordinal), tasks.Find(first.Id)!.Message);
                var stored = SuwayomiImport.Load(temp.Path)!;
                Check.Equal(3, stored.Total);
                Check.Equal("https://b.test/manga/b", stored.Items[1].RealUrl);
                Check.Equal(at.ToString("O", System.Globalization.CultureInfo.InvariantCulture), stored.CreatedAt);
                var again = SuwayomiImport.Enqueue(jobs, temp.Path, token => { reads++; return SuwayomiImport.ReadAsync(graph, at, token); });
                Check.Equal(JobOutcome.Completed, again.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
                Check.True(tasks.Find(again.Id)!.Message.StartsWith("Already imported 3", StringComparison.Ordinal), tasks.Find(again.Id)!.Message);
                Check.Equal(1, reads);
            }
            finally { jobs.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)); }
        });
    }

    internal static ConnectionTestResult Run(IConnectionType type, ConnectionConfig config, HttpMessageHandler handler)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return type.TestAsync(config, handler, deadline.Token).GetAwaiter().GetResult();
    }

    internal static ConnectionConfig Config(string type, params (string Key, string Value)[] values)
        => new("test-" + type, type, type, true, values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static MetadataClientOptions Options(string baseUrl) => new() { BaseUri = new Uri(baseUrl), Limiter = new TokenBucketLimiter(1000, TimeSpan.FromSeconds(1)) };

    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, (string Name, string Value)[]? headers = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers ?? []) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
