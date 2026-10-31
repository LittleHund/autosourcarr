using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Reader;

/// <summary>
/// The concrete Sources/Entry reader projection. Full feed preparation and Refresh
/// require their real owners and are deliberately absent from this read contract.
/// </summary>
public sealed class ReaderCatalogue : IReaderCatalogueRead
{
    private readonly object _sourceWriteGate;
    private readonly BoundedWorkScheduler _io;
    private readonly CatalogueQueries _queries;
    private readonly DisplayMetadata _display;

    /// <param name="sourceWriteGate">The same instance gate used by source, manual, display, merge and split writers.</param>
    /// <param name="covers">The actual per-instance cache owner. A made-up cache-presence callback is not accepted.</param>
    public ReaderCatalogue(SqliteDatabase database, object sourceWriteGate, CoverCache covers, BoundedWorkScheduler io)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate);
        ArgumentNullException.ThrowIfNull(covers); ArgumentNullException.ThrowIfNull(io);
        _sourceWriteGate = sourceWriteGate; _io = io;
        _queries = new(database, sourceWriteGate, entryId => covers.IsCached("entry-" + entryId.ToString(CultureInfo.InvariantCulture)));
        _display = new(database, sourceWriteGate);
    }

    public Task<JsonObject> SourcesAsync(CancellationToken cancellationToken) => _io.RunAsync(() =>
    {
        lock (_sourceWriteGate)
            return new JsonObject { ["items"] = new JsonArray(_queries.Sources().Select(row => (JsonNode)row).ToArray()) };
    }, cancellationToken);

    public Task<JsonObject> EntryAsync(long entryId, CancellationToken cancellationToken) => _io.RunAsync(() =>
    {
        lock (_sourceWriteGate)
        {
            var entry = _display.Overlay(_queries.Entry(entryId));
            // main.py's reader route computes this after the display overlay.
            // Native completeness/provenance remain the original query result.
            entry["description_for_extension"] = CataloguePresentation.DescriptionForExtension(entry);
            return entry;
        }
    }, cancellationToken);
}
