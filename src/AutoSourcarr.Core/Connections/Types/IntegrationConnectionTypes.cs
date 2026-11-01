namespace AutoSourcarr.Core.Connections.Types;

/// <summary>
/// Phase 7 integration connection types (metadata providers, Komf, MalwareBazaar, Suwayomi) for registration in
/// ConnectionTypes next to the solver types. Order is the Connections menu order.
/// </summary>
public static class IntegrationConnectionTypes
{
    public static IReadOnlyList<IConnectionType> All { get; } =
    [
        new SuwayomiConnectionType(),
        new MangaDexConnectionType(),
        new AniListConnectionType(),
        new MangaUpdatesConnectionType(),
        new KomfConnectionType(),
        new MalwareBazaarConnectionType(),
    ];
}
