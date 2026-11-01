using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Matching.Mixed;

/// <summary>A source record that is not attached yet (an ingest, discovery or import candidate).</summary>
public sealed record EditionCandidate(string SourceId, long MangaId, string Title, string Genre = "", string Description = "",
    IReadOnlyList<string>? Tags = null, string SourceBaseUrl = "")
{
    internal EditionRecordFacts Facts(EditionPageProfile? pages) =>
        new(SourceId, MangaId, Title, Genre, Description, Tags ?? [], "", SourceBaseUrl, pages);
}

/// <summary>The edition gate's answer. <see cref="Allowed"/> false means the two sides are different editions (or the
/// edition is still unresolved for an automatic attachment); <see cref="Reason"/> is the released reason text
/// ("color-edition-conflict:colored!=monochrome", "media-edition-conflict:mixed", "color-compatible:unknown/unknown", ...);
/// <see cref="Evidence"/> lists every signal both sides contributed.</summary>
public sealed record EditionVerdict(bool Allowed, string Reason, IReadOnlyList<EditionSignal> Evidence)
{
    public bool Conflict => !Allowed;
}

/// <summary>
/// MAT-3 edition separation for matching (8A ingest/automatic attachment, 8C duplicate review and merges): colored and
/// monochrome editions, manga and webtoon formats stay separate, decided from titles, saved page analysis, tags/genres
/// (through the tag synonym map), webtoon-only sources and description phrases. Implemented by <see cref="EditionProfiles"/>.
/// </summary>
public interface IEditionGate
{
    /// <summary>Whether two catalogue entries may be merged (stale or deleted ids are a conflict, as released).</summary>
    EditionVerdict CompareEntries(long leftEntryId, long rightEntryId);
    /// <summary>The same inside the caller's catalogue session (read or write).</summary>
    EditionVerdict CompareEntries(DatabaseSession catalog, long leftEntryId, long rightEntryId);
    /// <summary>Two unattached candidates (import resolution, pairwise edition compatibility).</summary>
    EditionVerdict CompareCandidates(EditionCandidate left, EditionCandidate right);
    /// <summary>Synchronous automatic-attachment gate from saved evidence only (media and known color editions).</summary>
    EditionVerdict GateCandidate(DatabaseSession catalog, long entryId, EditionCandidate candidate);
    /// <summary>The full automatic gate: when only one side has a color edition, the other side's pages are sampled
    /// first (lazy, as released); an edition that stays unresolved blocks the automatic attachment.</summary>
    Task<EditionVerdict> GateCandidateAsync(long entryId, EditionCandidate candidate, CancellationToken cancellationToken);
}
