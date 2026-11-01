using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Metadata.Hydration;

/// <summary>
/// _seed_identity_from_source as an ingest follow-up: when a MangaDex source supplies a record whose URL carries the
/// MangaDex title id, that id is the entry's exact MangaDex identity (confidence 1.0, "source-exact"; a locked link is
/// never replaced). Also remembers the record's cover hint. Every other follow-up goes to <paramref name="inner"/>.
/// Failures never fail the ingest (the write already committed); they go to <see cref="Failed"/>.
/// </summary>
public sealed partial class SourceIdentitySeed(SqliteDatabase database, object sourceWriteGate, IEntryIngestFollowUps? inner = null, CoverHints? covers = null)
    : IEntryIngestFollowUps
{
    private readonly IEntryIngestFollowUps _inner = inner ?? NoEntryIngestFollowUps.Instance;

    /// <summary>After an identity was written (choose metadata, drop the duplicate snapshot).</summary>
    public Action<long>? IdentityAdded { get; init; }
    public Action<long, Exception>? Failed { get; init; }

    [GeneratedRegex(@"(?:mangadex\.org/(?:title|manga)/|/title/)([0-9a-f]{8}-[0-9a-f-]{27,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MangaDexId();

    /// <summary>The MangaDex title id in a record's realUrl or url, or "".</summary>
    public static string MangaDexTitleId(JsonObject manga)
    {
        ArgumentNullException.ThrowIfNull(manga);
        foreach (var key in new[] { "realUrl", "url" })
            if (manga[key] is JsonValue value && value.TryGetValue<string>(out var url) && MangaDexId().Match(url) is { Success: true } match)
                return match.Groups[1].Value;
        return "";
    }

    /// <summary>Writes the identity when the source is MangaDex and the URL carries the id; answers whether a link was written.</summary>
    public bool Seed(long entryId, string sourceId, JsonObject manga)
    {
        ArgumentNullException.ThrowIfNull(manga);
        var id = MangaDexTitleId(manga);
        if (id.Length == 0) return false;
        bool written;
        lock (sourceWriteGate)
            written = database.Write(DatabaseFile.Catalog, s =>
            {
                var source = s.Query("SELECT name,display_name,base_url,lang FROM source_config WHERE source_id=$id", ("$id", sourceId)).FirstOrDefault();
                var name = (source?.Text("name") + " " + source?.Text("display_name") + " " + source?.Text("base_url")).ToLowerInvariant();
                if (!name.Contains("mangadex", StringComparison.Ordinal)) return false;
                if (s.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", entryId)) is null) return false;
                static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
                var title = Text(manga["title"]);
                var status = ThumbnailFingerprints.JsonNumber(manga["status"]);
                if (status == 0) status = MetadataFields.ProviderStatus(Text(manga["status"]));
                var genres = manga["genre"] is JsonArray list ? list.Select(Text) : Text(manga["genre"]).Split(',');
                var candidate = new MetadataCandidate
                {
                    Provider = "mangadex", ExternalId = id, Url = "https://mangadex.org/title/" + id, Title = title,
                    Titles = title.Length > 0 ? [new MetadataTitle(title, source?.Text("lang") ?? "", "primary")] : [],
                    Description = Text(manga["description"]), CoverUrl = Text(manga["thumbnailUrl"]),
                    Status = status switch { 1 => "ongoing", 2 => "completed", 5 => "cancelled", 6 => "hiatus", _ => "unknown" },
                    Authors = Text(manga["author"]) is { Length: > 0 } author ? [author] : [],
                    Artists = Text(manga["artist"]) is { Length: > 0 } artist ? [artist] : [],
                    Genres = genres.Select(genre => genre.Trim()).Where(genre => genre.Length > 0).ToArray(),
                    Raw = (JsonObject)manga.DeepClone(),
                };
                return MetadataIdentityResolver.Upsert(s, entryId, candidate, 1.0, "source-exact",
                    DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture));
            });
        if (written) IdentityAdded?.Invoke(entryId);
        return written;
    }

    public void RecordWritten(long entryId, string sourceId, long sourceMangaId, JsonObject manga)
    {
        try
        {
            covers?.Remember(sourceId, manga);
            Seed(entryId, sourceId, manga);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Failed?.Invoke(entryId, error); }
        _inner.RecordWritten(entryId, sourceId, sourceMangaId, manga);
    }

    public void ChooseMetadata(long entryId) => _inner.ChooseMetadata(entryId);
    public void ScheduleHydration(string sourceId, long sourceMangaId) => _inner.ScheduleHydration(sourceId, sourceMangaId);
    public void ScheduleIdentity(long entryId) => _inner.ScheduleIdentity(entryId);
    public void Invalidate(long entryId) => _inner.Invalidate(entryId);
}
