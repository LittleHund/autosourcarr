using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Metadata;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Tests.Matching;

namespace AutoSourcarr.Tests.Catalogue.Metadata;

/// <summary>Phase 9: metadata provider identity as matching evidence, with a fake provider client.</summary>
public static class MetadataIdentityTests
{
    internal sealed class FakeProvider(string provider, Func<string, IReadOnlyList<MetadataCandidate>> search) : IMetadataProviderClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Provider { get; } = provider;
        public MetadataRateStatus RateStatus { get; } = new(null, null, null, null);
        public Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string title, int limit = 6, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(search(title));
        }
        public Task<MetadataCandidate?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<MetadataCandidate?>(null);
        public void Dispose() { }
    }

    private sealed class Allow : IAutomaticMatchGate
    {
        public AutomaticMatchDecision Decide(JsonObject left, JsonObject right) =>
            new(true, "exact native title", new JsonObject { ["eligible"] = true, ["reason"] = "exact native title" });
    }

    private static MetadataCandidate Candidate(string provider, string id, string title, string author, IReadOnlyDictionary<string, string>? links = null) => new()
    {
        Provider = provider, ExternalId = id, Title = title, Titles = [new MetadataTitle(title, "en", "primary"), new MetadataTitle(title + " Alt", "ja", "alias")],
        Authors = [author], Description = "Coco wants to become a witch.", Genres = ["Fantasy"], Status = "ongoing", Year = 2016,
        Links = links ?? new Dictionary<string, string>(),
    };

    public static void Register(TestSuite suite)
    {
        suite.Add("catalogue metadata identity: strict native evidence, crossrefs, titles and the provider cache", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            MatchingFixtures.Source(database, "1", "Alpha", "en");
            var entry = MatchingFixtures.Entry(database, "Witch Hat Atelier", "Kamome Shirahama");
            MatchingFixtures.Record(database, entry, "1", 11, "Witch Hat Atelier", "Kamome Shirahama");
            var other = MatchingFixtures.Entry(database, "Witch Hat Atelier Kitchen", "Hiromi Sato");
            MatchingFixtures.Record(database, other, "1", 12, "Witch Hat Atelier Kitchen", "Hiromi Sato");

            var anilist = new FakeProvider("anilist", query => query.Contains("Kitchen", StringComparison.Ordinal)
                ? [Candidate("anilist", "200", "Witch Hat Atelier Kitchen", "Xu Yu")]
                : [Candidate("anilist", "100", "Witch Hat Atelier", "Kamome Shirahama", new Dictionary<string, string> { ["mangaupdates"] = "mu-7", ["unknown"] = "x" }),
                   Candidate("anilist", "101", "Witch Hat", "Kamome Shirahama")]);
            var failing = new FakeProvider("mangadex", _ => throw new MetadataProviderException("mangadex", "rate limited"));
            var titles = new List<(long, string, string)>(); var changed = new List<long>();
            var settings = new AutoSourcarrSettings { DataDir = temp.Path };
            var resolver = new MetadataIdentityResolver(database, new object(), () => settings, () => [anilist, failing])
            {
                AddTitle = (id, title, _, kind, _) => titles.Add((id, title, kind)),
                IdentitiesChanged = changed.Add,
            };

            var result = resolver.ResolveAsync(entry, force: false, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(1, result["matched"]!.GetValue<int>());
            Check.True(result["provider_errors"]!["mangadex"]!.GetValue<string>().Contains("rate limited", StringComparison.Ordinal));
            var links = database.Read(DatabaseFile.Catalog, session => session.Query("SELECT provider,external_id,match_type,year,author FROM identity_links WHERE entry_id=$e ORDER BY provider", ("$e", entry)));
            Check.Equal("anilist:100,mangaupdates:mu-7", string.Join(',', links.Select(row => row.Text("provider") + ":" + row.Text("external_id"))));
            Check.True(links[0].Text("match_type").StartsWith("metadata:title", StringComparison.Ordinal), links[0].Text("match_type"));
            Check.Equal(2016L, links[0].Integer("year"));
            Check.Equal("crossref:anilist", links[1].Text("match_type"));
            Check.True(titles.Contains((entry, "Witch Hat Atelier", "identity-primary")) && titles.Contains((entry, "Witch Hat Atelier Alt", "identity-alias")));
            Check.Equal(entry, changed.Single());

            // The answer is cached under the released key: a second resolution makes no provider call; a failure is never cached.
            var calls = anilist.Calls;
            resolver.ResolveAsync(entry, force: false, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal(calls, anilist.Calls);
            var cached = database.Read(DatabaseFile.Catalog, session => session.Query("SELECT provider,query_key FROM metadata_query_cache"));
            Check.True(cached.All(row => row.Text("provider") == "anilist") && cached.Any(row => row.Text("query_key") == MetadataIdentityResolver.QueryKey("Witch Hat Atelier", true, 6)));

            // A disagreeing author refuses the provider record; IDuplicateHydration answers how many entries gained identities.
            IDuplicateHydration hydration = resolver;
            Check.Equal(0, hydration.ResolveIdentitiesAsync([entry, other], CancellationToken.None).GetAwaiter().GetResult());
            Check.True(database.Read(DatabaseFile.Catalog, session => session.Scalar("SELECT 1 FROM identity_links WHERE entry_id=$e", ("$e", other))) is null);
            Check.Equal(0, hydration.FingerprintThumbnailsAsync(10, CancellationToken.None).GetAwaiter().GetResult());
        });

        suite.Add("catalogue metadata identity: released query key, scoring and the identity gate", () =>
        {
            Check.Equal("v85:5299b61a6b075067672cb913dd0b636750cb7d766709271d5161965a2a612ac4", MetadataIdentityResolver.QueryKey("Witch  Hat Atelier", true, 6));
            Check.Equal("v85:1ed28c1490427901ab696d1fe5dc5c354f931b2d9f4ab58d46ba64ed0345a20b", MetadataIdentityResolver.QueryKey("とんがり帽子のアトリエ", true, 6));
            Check.Equal("", MetadataIdentityResolver.QueryKey("   ", true, 6));

            var native = new JsonObject { ["titles"] = new JsonArray("Witch Hat Atelier"), ["descriptions"] = new JsonArray(), ["author"] = "Kamome Shirahama" };
            Check.True(MetadataIdentityResolver.CandidateScore(native, Candidate("anilist", "1", "Witch Hat Atelier", "Shirahama Kamome"), "", 0.94, 0.82).Score >= 0.94);
            Check.Equal(0.0, MetadataIdentityResolver.CandidateScore(native, Candidate("anilist", "1", "Witch Hat Atelier", "Xu Yu"), "", 0.94, 0.82).Score);
            Check.Equal(0.0, MetadataIdentityResolver.CandidateScore(native, Candidate("anilist", "1", "Witch Hat Atelier Kitchen", "Kamome Shirahama"), "", 0.94, 0.82).Score);
            // A title-only entry accepts only an exact title.
            Check.Equal(1.0, MetadataIdentityResolver.CandidateScore(new JsonObject(), Candidate("anilist", "1", "Witch Hat Atelier", ""), "Witch Hat Atelier", 0.94, 0.82).Score);

            JsonObject Snapshot(params (string Provider, string Id)[] ids) => new()
            {
                ["identity_details"] = new JsonArray(ids.Select(pair => (JsonNode)new JsonObject { ["provider"] = pair.Provider, ["external_id"] = pair.Id }).ToArray()),
            };
            var gate = new MetadataIdentityGate(new Allow());
            var conflict = gate.Decide(Snapshot(("anilist", "1"), ("mangadex", "a")), Snapshot(("anilist", "2"), ("mangadex", "a")));
            Check.True(!conflict.Eligible && conflict.Reason == "different anilist identities (1 / 2)", conflict.Reason);
            Check.True(gate.Decide(Snapshot(("anilist", "1")), Snapshot(("anilist", "1"), ("mangaupdates", "9"))).Eligible);
            Check.True(gate.Decide(Snapshot(("anilist", "1")), Snapshot(("mangadex", "z"))).Eligible);
            Check.True(gate.Decide(Snapshot(), Snapshot(("anilist", "1"))).Eligible);
        });

        suite.Add("catalogue metadata identity: ingest follow-ups route identity, choice and hydration", () =>
        {
            var chosen = new List<long>(); var hydrated = new List<(string, long)>();
            var followUps = new MetadataIngestFollowUps(null) { Choose = chosen.Add, Hydrate = (source, manga) => hydrated.Add((source, manga)) };
            followUps.ChooseMetadata(4); followUps.ScheduleHydration("1", 11); followUps.ScheduleIdentity(4); followUps.Invalidate(4);
            followUps.RecordWritten(4, "1", 11, new JsonObject());
            Check.Equal(4L, chosen.Single());
            Check.Equal(("1", 11L), hydrated.Single());
            IEntryIngestFollowUps none = new MetadataIngestFollowUps(null);
            none.ChooseMetadata(1);
        });
    }
}
