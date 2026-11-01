# 10A: chapters, serving, quality

## Brief given to the agent

Read /home/claude/phase10-common.md first and follow it.

Your area (10A): chapters, serving and quality. Ownership: src/AutoSourcarr.Core/Catalogue/Chapters/** (existing NativeChapterWriter / ReaderPageService / ChapterResolver etc. — extend), src/AutoSourcarr.Core/Catalogue/Browse/**, src/AutoSourcarr.Core/Catalogue/Gallery/** (extend), src/AutoSourcarr.Core/Catalogue/Serving/** (new if needed), src/AutoSourcarr.Api/Endpoints/{ChaptersV1Endpoints,ServingV1Endpoints}.cs (new), the existing ReaderEndpoints.cs (only to keep it working), tests under tests/AutoSourcarr.Tests/Catalogue/{Chapters,Serving,Browse}.

Port-map groups (docs/port-map.md): "Catalogue/Browse → SourceBrowse / ReaderCatalogue (phase 10, 11 methods)", "Catalogue/Chapters → NativeChapterWriter / ReaderPageService / ChapterResolver (phase 10, 28 methods)", "Catalogue/Gallery → GalleryEvidenceService / GalleryLayout (phase 10, 10 methods)". Routes: from "CatalogueEndpoints — partial (DisplayMetadata exists) (27 routes)" take: GET /chapters/{chapter_id}, POST /candidates/{candidate_id}/quality, /candidates/{candidate_id}/reject, /candidates/{candidate_id}/restore, POST /matrix/chapter-stats, GET /matrix, POST /entries/{entry_id}/source-records/{source_id}/{source_manga_id}/check-chapters, POST /entries/{entry_id}/refresh, /refresh-all; from "CatalogueEndpoints / DisplayMetadataEndpoints / EngineCompatibilityEndpoints — partial (13 routes)": gallery-policies GET/PUT/apply, serving-upgrades GET/PUT/preview/apply, profiles GET/PUT (entry serving profiles). Move them under /api/v1 (path params as {entryId} style). The reader API (ReaderEndpoints, /api/v1/... reader paths and the /api/reader/v1 alias) stays as is — verify it's mapped and its auth unchanged.
Requirements CHAP-1..6 and READ-3 (see common file):
1. CHAP-1: chapter number parsing from any numbering word (Vol, Part, Act, Stage, No., Episode, Chapter, Ch., Ep., 第…話 etc. — check the existing C# parser (grep ChapterNumber / _chapter_key / _format_chapter_number in Core and Python service.py) and extend it); ordering so part 1 precedes part 2 within the same number; table-driven tests with ≥40 real-world chapter names.
2. CHAP-2: season continuation: season 2 chapter 1 follows the last chapter of season 1 and is shown as "Chapter 41: S2 Ch.1: [title]" (find Python `_chapter_season_number` etc.).
3. CHAP-3/CHAP-4: one chapter list per title, one source per chapter, groups share one priority and the group member with the most chapters wins for serving; groups need no WebView of their own.
4. CHAP-5/6: quality score from sample pages (failed samples keep the old score) and opt-in quality upgrades with minimum gain and cutoff (serving-upgrade profiles) — existing C# parts: grep "quality" in Core/Catalogue and Core/Imaging.
5. Also implement IPageImageSource (src/AutoSourcarr.Core/Matching/Mixed/PageColorAnalysis.cs) on top of the reader page service: chapter → page list → PageColorAnalysis.SampleIndexes → fetch → decode (existing RasterImages in Core/Imaging) → downscale ≤256 RGB; tell me the wiring (EditionProfiles takes pageImages in ApplicationRuntime.Matching.cs).
6. READ-3: serving available entries at once and updating when a source has newer data — check the reader browse/entry endpoints use cached data first then schedule refresh; fix if not.
7. UI: move calls for your routes to /api/v1 (app.js and friends; grep the old paths) and update UI tests.
Tests: numbering/order table, season naming, group priority, quality failure keeps score, upgrade min gain/cutoff, IPageImageSource with a fake page source, route access (admin vs guest: GET /chapters/{id} under /api/v1/chapters/ — check AccessPolicy guest prefixes).
Reply ≤800 words with wiring (ApiV1Services props, Map lines, construction, Program.cs registrations).

## Agent's final report (as returned; not independently verified)

I've finished area 10A, but none of the C# has been compiled or run: there's no dotnet SDK here, so I checked APIs by reading the repo. `check-project.sh` ends "Project checks completed." and `ci-pytest.sh` ends "No new Python failures (baseline 27 known)." `node --check` passes on every JS file I changed.

**Files outside my folders that I had to touch:**
- `ChapterResolver`, `ReaderPageService` and `ReaderRepository` live in `src/AutoSourcarr.Core/Reader/`, not `Catalogue/Chapters`. I made two small edits to `ReaderRepository.cs`: the reader display name now comes from `ChapterNumbering.DisplayName`, and `Rank` applies the group rule.
- `ReaderEndpoints.cs`: `MapReaderServing` takes an optional `Action<long>? chaptersRead`, called after a chapter list is served. Routes and auth are unchanged. GET `/api/v1/chapters/{chapter_id}` is still mapped there and stays a guest read.

**What's done**
- **CHAP-1:** new parser in `Catalogue/Chapters/ChapterNumbering.cs`, used by `ChapterNumbers.Key`. It reads numbers from any numbering word, and an explicit part is filed as a decimal (part 2 of chapter 10 becomes 10.2, so part 1 sorts first). A "Part" that comes before the chapter number is treated as an arc, not a part. The existing 28 key-fixture cases stay as they were.
- **CHAP-2:** `SeasonContinuation` runs inside `NativeChapterWriter.Apply`. If season 1 ends at 40, "S2 Ep. 1" is stored as 41 and listed as "Chapter 41: S2 Ch.1: [title]". `ChapterResolver.DisplayName` is unchanged, so the 288-case oracle test still holds.
- **CHAP-3/4:** `ServingGroups`: within a source group, the member whose record has the most chapters for the title serves. Groups get no WebView or record of their own.
- **CHAP-5:** `Catalogue/Serving/CandidateQuality.cs`. A failed sample keeps the old score and the old sample, and every attempt is recorded. The 0.4.5 perceptual hashes (phash) are not computed; those fields stay empty.
- **CHAP-6:** `ServingUpgrades.cs` (cutoff, minimum gain, cooldown; preview then apply).
- **Page sampling:** `PageReading.cs` has `ReaderCandidatePages`, `MagickPageRasterDecoder`, and `ReaderPageImages : IPageImageSource`.
- **Candidates, refresh, gallery:** `ChapterCandidateReview.cs` (reject/restore), `ChapterRefresh.cs` (check-chapters, whole-work refresh job, stale check), and `Gallery/GalleryLayout.cs` (gallery apply job).
- **READ-3:** they weren't using cached data first, so I fixed it. `Browse/ReaderBrowse.cs` is a full `IReaderCatalogue`. Popular/Latest serve the saved feed pages and only queue a crawl when old; a missing page answers 503 "preparing". Search uses the local catalogue. Opening a work queues a chapter refresh for records not checked recently.
- **Endpoints:** `Api/Endpoints/ChaptersV1Endpoints.cs` and `Api/Endpoints/ServingV1Endpoints.cs`. Every change records the caller in the decision log.
- **UI:** calls moved to `/api/v1` in `app.js`, `gallery-policy.js`, `instance-tools.js`, `manage-entry.js` and `reimagined-controls.js`. Matching updates in `test_v086_ui.py` and `ui_v0326_response_ownership.js`.
- **Tests** (route access is part of `ServingTests.cs`):
  - `tests/AutoSourcarr.Tests/Catalogue/Chapters/{ChapterWorld,ChapterNumberingTests}.cs` (51-name table)
  - `tests/AutoSourcarr.Tests/Catalogue/Serving/ServingTests.cs`
  - `tests/AutoSourcarr.Tests/Catalogue/Browse/ReaderBrowseTests.cs`

**Wiring**

`ApiV1Services`:
```csharp
public ChaptersV1Services? Chapters { get; init; }
public ServingV1Services? Serving { get; init; }
```
`MapApiV1`:
```csharp
ChaptersV1Endpoints.Map(group, services, json, services.Chapters);
ServingV1Endpoints.Map(group, services, json, services.Serving);
```
`CatalogueRuntime`: expose `internal ReaderRepository Repository => _repository!; internal ReaderPageService Pages => _pages!; internal ServingProfileRepository Profiles => _profiles!; internal IReaderCatalogueRead ReaderRead => _catalogue!; internal ReaderBrowse? Browse { get; set; }`. In `MapEndpoints`, when `Browse` is set use `endpoints.MapReaderCatalogue(Browse)` instead of `MapReaderCatalogueRead(_catalogue)`, and `MapReaderServing(_repository!, _pages!, io, Browse.ChaptersRead)`. You can drop `MapServingProfiles`; the UI now calls `/api/v1/profiles`.

Construction (in `OpenMatching` or a new partial):
```csharp
var candidatePages = new ReaderCandidatePages(database, writeGate, new SuwayomiReaderPageSource(owner.SourceRuntime.Client), catalogue.Pages);
var editions = new EditionProfiles(database, () => {/*as now*/}, new ReaderPageImages(database, candidatePages, new MagickPageRasterDecoder()));
var refresh = new ChapterRefresh(database, writeGate, catalogue.Chapters,
    (id, d, c, t) => owner.SourceRuntime.Client.FetchMangaAndChaptersAsync(id, d, c, t), () => TimeSpan.FromSeconds(owner.Settings.RefreshSourceTimeout));
catalogue.Browse = new ReaderBrowse(database, writeGate, catalogue.ReaderRead, catalogue.Repository, refresh, owner.Io,
    () => new ReaderFreshness(TimeSpan.FromSeconds(owner.Settings.BrowseCacheRefreshSeconds), TimeSpan.FromSeconds(owner.Settings.MetadataTtlSeconds), owner.Settings.BrowsePageSize))
{ Jobs = owner.Jobs, HydrateDetails = catalogue.Hydration.Schedule,
  ScheduleFeedRefresh = () => { owner.Jobs.Enqueue(DiscoveryV1Endpoints.FeedRefreshJob, "", async j => await discovery.RefreshFeedsAsync((a,b,m) => j.Report(a,b,m), j.CancellationToken), origin: "automatic", title: "Refresh browse feeds"); return true; } };
Chapters = new ChaptersV1Services { Database = database, Queries = new CatalogueQueries(database, writeGate, _ => false),
  Review = new ChapterCandidateReview(database, writeGate, catalogue.Repository.ChapterDetails, catalogue.Repository.ReconcileVisibility, decisions),
  Quality = new CandidateQuality(database, writeGate, candidatePages, new MagickPageRasterDecoder(), () => new QualityOptions(owner.Settings.QualityAnalysisConcurrency, TimeSpan.FromSeconds(owner.Settings.QualitySampleTimeout), TimeSpan.FromSeconds(owner.Settings.QualityOverallTimeout)), decisions),
  Refresh = refresh, Subscription = library.CachedSubbedPayload, ClearCovers = keys => catalogue.Covers.ClearKeys(keys),
  HydrateDetails = catalogue.Hydration.Schedule, ScheduleIdentity = id => _ = identity.ResolveAsync(id, true, CancellationToken.None) /* adjust to the real signature */ };
var policies = new GalleryPolicyRepository(database, writeGate);
Serving = new ServingV1Services { GalleryPolicies = policies, GalleryLayout = new GalleryLayout(database, policies, catalogue.ReconcileGallery),
  Upgrades = new ServingUpgrades(database, writeGate, catalogue.Repository, decisions), Profiles = catalogue.Profiles, Decisions = decisions, AdminLock = owner._admin };
```
- Another agent's `CatalogueV1Services.Browse` can be set to `catalogue.Browse!.BrowseAsync`.
- Add `/api/v1/matrix` to `RequestPolicy` Polls, and `/api/v1/matrix/chapter-stats` to NonInteractive and NoMutationTask.
- Regenerate `openapi.json` afterwards.

`tests/AutoSourcarr.Tests/Program.cs`:
```csharp
AutoSourcarr.Tests.Catalogue.Chapters.ChapterNumberingTests.Register(suite);
AutoSourcarr.Tests.Catalogue.Serving.ServingTests.Register(suite);
AutoSourcarr.Tests.Catalogue.Browse.ReaderBrowseTests.Register(suite);
```

**Routes:** all the requested routes are under `/api/v1` and admin-only, except two that already exist as reader routes:
- **GET `/chapters/{id}`:** the reader route covers it (the 0.4.5 admin handler was identical); I didn't map a duplicate.
- **POST `/entries/{id}/refresh`:** comes from the reader `MapReaderCatalogue` once you switch to it. A second mapping would collide on the same path. If that switch doesn't happen, POST refresh isn't mapped.

**Port-map methods**
- **Ported:** most Chapters, Browse and Gallery methods, either in the new classes or in classes that already existed.
- **Left with matching / phase 8 `EntryIngest`:** `_candidate_entries*`, `_local_match_candidates`, `_variant_candidate_score`.
- **Not ported:** `_forget_candidate_page_info`, `_remember_page_info` and `page_private_url` (Python in-memory caches); `_gallery_reconciliation_entry_ids` (startup sweep).
- **Partial:**
  - **Live federated browse:** per-request source fetches for filtered or query feeds aren't done; only saved default pages are served.
  - **Gallery apply:** separates records but does not merge newly compatible works; that's left to automatic duplicate merging.

**Couldn't verify:**
- The Magick.NET 15 pixel calls (`Resize(uint, uint)`, `GetPixels().ToByteArray(PixelMapping.RGB)`) only exist in the decoder.
- The new C# tests have never been run.

Nothing new to save to memory.
