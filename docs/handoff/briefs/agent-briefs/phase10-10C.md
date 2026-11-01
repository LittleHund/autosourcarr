# 10C: imaging, hydration, tags, translation

## Brief given to the agent

Read /home/claude/phase10-common.md first and follow it.

Your area (10C): imaging, metadata hydration, identity, tags, translation and the remaining catalogue routes. Ownership: src/AutoSourcarr.Core/Imaging/** (extend), src/AutoSourcarr.Core/Catalogue/Metadata/Hydration/** (new; existing Records/NativeMetadataHydration.cs may be extended), src/AutoSourcarr.Core/Catalogue/Tags/** (extend), src/AutoSourcarr.Core/Translation/** (new), src/AutoSourcarr.Api/Endpoints/{CatalogueV1Endpoints,TagsV1Endpoints}.cs (new), tests under tests/AutoSourcarr.Tests/{Imaging,Catalogue/Hydration,Catalogue/Tags,Translation}.

Port-map groups (docs/port-map.md): "Imaging → CoverCache / CoverRecovery / ThumbnailFingerprint (phase 10, 27 methods)", "Catalogue/Metadata → MetadataHydration (NativeMetadataHydration exists) (phase 10, 23 methods)", "Catalogue/Metadata → IdentityResolver (phase 10, 11 methods)" — phase 9 already ported much identity work in src/AutoSourcarr.Core/Catalogue/Metadata/MetadataIdentity.cs: port only what's missing (compare method lists), "Catalogue/Tags → NativeTagStore (phase 10, 1 methods)", translation.py → Translation/TranslationClient (LibreTranslate-compatible; settings TRANSLATION_API_URL/KEY exist; MAT-20 machine translations help search only, never displayed as the title), TagEndpoints (5 routes: /tags, /tags/suggestions, /tags/reviews, /tags/review, /tags/reindex), and the remaining CatalogueEndpoints routes: GET /entries/{entry_id}/tags, POST /entries/{entry_id}/refresh-covers, GET /browse, GET /entries, POST /entries/summaries, GET /entries/{entry_id} (admin view — careful: the guest reader /api/v1/entries/{entry_id} exists in ReaderEndpoints; use a distinct admin path like /api/v1/catalogue/entries/{entryId} if they clash, and tell me), GET /entries/{entry_id}/history, POST /entries/{entry_id}/refresh-metadata, GET /metadata-providers, POST /entries/{entry_id}/identity/refresh, POST /entries/{entry_id}/discover-additional (per entry; phase 9 CatalogueDiscovery has the catalogue-wide job: reuse), GET /research-improvements/status. Check which of these already exist in C# under /api/v1 (grep the paths in src/AutoSourcarr.Api) before adding.
Also carried items: identity Fingerprints hook (MetadataIdentityResolver.Fingerprints) and the import exact-thumbnail tie-break (src/AutoSourcarr.Core/Imports: `_import_thumbnail_fingerprints*`, `_import_exact_thumbnail_entry_ids*` — the import service has a hook or TODO; grep "thumbnail" in Core/Imports), machine translation in import/discovery enrichment (`_expanded_search_names` translation queries; EntryIngest in src/AutoSourcarr.Core/Matching/Ingest), the gallery identity path if simple.
Health: ApplicationRuntime.HealthAsync translation block says "not checked … phase 10": give me code for a cached background translation check (never blocks health).
UI: move the calls for your routes to /api/v1; update UI tests.
Tests per area with fakes. Reply ≤800 words with wiring (ApiV1Services props, Map lines, construction in ApplicationRuntime/OpenMatching, Program.cs registrations).

## Agent's final report

(Not preserved in the session files. Check the agent's actual output in git: `git show 3ed0b4e --stat` for phase 10.)
