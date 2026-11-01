## 0.5.0 (in progress)

C#/.NET conversion (ARC-11); notes are added per conversion phase.

- Phase 7 (Connections): connections are saved in `config.json` under a new top-level
  `"connections"` key, with secrets encrypted by `config.key`. 0.4.x refuses a
  `config.json` with keys it does not know, so before rolling back to 0.4.x remove the
  `"connections"` key (keep a copy to restore later). The first connection you add
  shows this note in the app.
- Phase 10 (Chapters and reader): new catalogue tables for the CBZ library (`library_series`,
  `download_queue`, `library_downloads`, `download_history`, `library_state`; migration
  2026110192) and the setting `LIBRARY_DIR`. 0.4.x ignores these tables. The reader extension
  repository is served at `/api/reader/repo/` without a key (GET/HEAD only); the reader API at
  `/api/reader/v1` keeps its key and Authelia rule. Add the repo path to Authelia/NPM when you
  deploy 0.5.0 (`docs/reverse-proxy/`). Limits: the reader extension is not yet built into the
  image (phase 13); match-on-ingest can still attach two works that share an exact title and
  have no other evidence (open owner decision).
- Phase 10 recheck: `POST /api/v1/maintenance/reset-database` and `/full-reset` now need the
  `preview_token` from `GET` on the same path (the UI fetches it); a reset stops and drains
  background work first. The 0.4.5 startup jobs run again (merge safety repair, startup
  maintenance, startup dedup and the other `*_ON_STARTUP` settings, reset-hint replay). The
  source host answers a local pacing overflow as a deferral instead of a source error.

## Unreleased

- Reader extension sources moved from `extension/module/src/eu/kanade/tachiyomi/extension/all/autosourcarr/`
  to `extension/module/src/autosourcarr/`. The Kotlin package, the APK package name and
  the source ID are unchanged; `build-in-container.sh` still places the files at the
  conventional package path inside the Keiyoushi checkout. Project checks and tests
  follow the new path. Remove the old `src/eu` folders (project and `autosourcarr/bundled`)
  after updating a project directory in place.

## 0.4.5 - 2026-10-31

Interim step before the C#/.NET conversion (requirement ARC-11).

- Removed the Suwayomi host port from every Compose file (`docker-compose.yml`,
  `docker-compose.truenas.yml`, `docker-compose.two-instances.yml`, the cluster
  example). Suwayomi ran with `AUTH_MODE: none`, so its published port let anyone
  on the LAN reach it without Authelia. It is now reachable only inside the compose
  network; AutoSourcarr keeps using `http://suwayomi:30058`. `check-project.sh`
  fails if a Suwayomi service publishes a port again.
- Entries got "Open in WebView": in the library/matrix entry inspector and the
  Browse entry view, a header button opens the primary source record's cached
  page in the shared source WebView, and every source match line has its own
  button. A missing address falls back to the source's base URL. Covered by
  `tests/ui_v045_entry_webview.js`.
- Version 0.4.5 in all copies, static asset `?v=0.4.5`, extension version code 4005.
- The release no longer ships `config/autosourcarr.env`; only the `.example` templates
  are included. `scripts/init-truenas-config.sh` generates the reader API token per
  instance when it is blank, as before.

Results and limits: `check-project.sh` and the UI suites ran locally on Linux
(cloud workspace, not the NAS). Python tests: see the 0.4.4 baseline of stale
failures; no new failures. No Docker build, APK/JAR build or NAS deployment was
run for this release.

## 0.4.4 - 2026-10-26

- Security: refused cross-site browser writes to `/api/admin/` and `/api/v1/` before
  any route runs. With an empty `ADMIN_TOKEN`, a page in another browser tab could
  previously POST to body-less maintenance routes such as `reset-database`,
  `full-reset`, `reset-manual-groups` and `reset-manual-matches`. A write is refused
  when `Sec-Fetch-Site` is `cross-site` or `Origin` does not match `Host` or the first
  `X-Forwarded-Host`, the rule setup and repair routes already use behind reverse
  proxies. Scripts, the Suwayomi JAR and other clients without browser headers are
  unchanged. Setting `ADMIN_TOKEN` is still recommended when port 8282 is reachable.
- Both SQLite files now carry an AutoSourcarr application ID and schema revision 1.
  Existing 0.4.3 files are stamped in place on first start. A file with a newer
  revision or a foreign application ID is refused with a clear message and left
  unchanged, so an older image can no longer open a newer schema.
- Added the missing `tag_reviews(right_id)` index; deleting a tag no longer scans
  every tag review.
- Catalogue sharing decisions inspect each pending decision once instead of twice.
- Removed 10 unused imports from the app, repair worker and source tools; the
  bundled tool copy is synced.
- Checks (cloud workspace, no Docker or live services): new cross-site and schema
  revision tests; offline schema build with the new index and stamp; Python compile
  of all sources; 33 Node UI suites; bundled-tool sync; Linux text. Full pytest on
  Linux (Python 3.13, pinned packages, pure-Python rapidfuzz): 2,693 passed,
  4 skipped, 40 failed. The same 40 tests fail on unchanged 0.4.3 in the same
  environment, so 0.4.4 adds no failures. They are stale legacy-UI/version/line-count
  assertions, subprocess tests that need an installed virtual environment, retained
  0.8.5 fixtures without the gallery tables, and two outdated error-message checks.
  No Docker build, APK/JAR build or live NAS check was run.
- Includes the 0.4.3 README/documentation update below.

- Documentation (0.4.3 follow-up): updated the README introduction with the owner's supplied description, covering
  manga and similar media, a combined library, Tachiyomi/Mihon source use and
  downloading for Komga/Suwayomi servers. Documentation wording only.
- Shortened the GitHub README into a project overview with features, getting-started
  links, development commands, support and license. Moved current setup and usage
  details into `docs/INSTALLATION.md` and `docs/USER_GUIDE.md`; older release changes
  remain below. Reader security, data-retention, repair-isolation and current build
  requirements remain documented. This documentation edit does not change the app
  version or complete the separately approved C#/.NET/image-only migration.
- Documentation checks: 30 local links/anchors and both existing reader-security
  documentation tests pass. The 599 other public files and 569 experimental files
  are unchanged. Required full pytest and project-checker runs retain 14 Windows
  POSIX collection errors; no new runtime or deployment proof is claimed.

## 0.4.3 - 2026-10-19

- Replaced the header, browser favicon, home-screen icon and reader-extension
  resources with the supplied blue crystal-star/book v6 PNG/ICO files. Selected
  files are copied byte for byte, preserving transparency; no artwork was redrawn
  or resized. Existing sizes, paths, layout and app functionality are retained.
- Aligned backend/image/worker/static asset versions to 0.4.3 and reader fallback
  to 1.4.4003. Canonical extension resources are synced into the maintained bundle.
  Browser icon URLs carry the new version to refresh cached v4 artwork.
- Author self-review: 988 portable tests pass (5 skips, 1 known Windows symlink
  exclusion). Selected PNG/ICO files match the supplied ZIP exactly and decode
  with transparency. The actual desktop sample renders the 64px header asset at
  32px, retains its accessible brand label and uses 0.4.3 browser/home-screen URLs.
  Runtime/UI logic, layout, source identity and all 0.4.2 features are unchanged.
- Available project stages 1, 3–6 and 8–9 pass, including the 64-file bundle,
  syntax, version 0.4.3 → reader 1.4.4003, shell and overlay/env guards. Required
  full commands retain 14 Windows POSIX collection errors. Native JAR validation
  needs absent `unzip`/JDK `jar`; Docker is absent. Native compilation/reader icon
  loading and live NAS checks remain unverified. No deployment, configuration/data
  reset or source/toolchain change ran.
- Upgrade with the complete `autosourcarr-v0.4.3.zip`, preserving active env/config,
  both deployed app DBs, signing identity, profiles, downloads and `/data`. Rebuild
  the existing image and refresh the browser. Rebuild/update the reader extension
  to load its new icon. Original-version encrypted backup restrictions remain.

## 0.4.2 - 2026-10-19

- Added Database sharing with bounded `.ascatalog` export, multi-file preview,
  atomic additive apply, persistent result receipts and review of retained choices
  and incoming versions. Independent row numbers never become shared identities.
  Unique portable entry/decision IDs, exact public source references and scoped
  aliases retain identity across A/B/C round trips, merges and gallery splits.
  No central catalogue or external service is required. Work-only references do
  not merge editions; metadata-only imports cannot bootstrap native matching.
- Included saved manual titles, source matches/groups, Separate decisions, import
  choices/rejections and identity reviews. Locally mapped unambiguous choices can
  apply; conflicts remain retained. Structural changes need explicit review and
  run through Tasks using established split/merge recovery. Existing local choices
  remain authoritative. Incoming confirmations need fresh local review. Reused ID
  conflicts can be added separately with corrected identities retained on re-export.
- Added Gallery matching per source language: combining on/off, explicit provider
  gallery profile and selected artist/circle/parody/language/category/tag criteria.
  The 0.94 automatic title minimum and native edition/animation/member safeguards
  remain. Off separates distinct galleries, including cached/shared groups, while
  retaining chapter pages, served IDs and recovery intents. Manual reset/rebuild
  cannot silently recombine a source whose combining setting is off.
- New Web UI controls use single-owner writes, bounded history/preview pages,
  source-specific drafts, abort/generation fences and stage-ID reconciliation
  after uncertain import outcomes. Public data is allowlisted; accounts, config,
  reading progress, downloads and extension code/binaries remain private.
- Applied the supplied blue crystal-star/book v4 artwork to the header, browser
  favicon, home-screen icon and reader-extension mipmaps without altering pixels.
  The supplied 48/96/192px mipmaps cover density selection; Android scales the
  next-best resource for other densities. Generated bundled assets stay in sync.
- Author self-review: 988 portable tests pass, with 5 skips and 1 known Windows
  symlink-privilege exclusion. This includes 33 new cases covering independent
  databases with colliding local IDs, A/B/C re-export/idempotence, exact source
  remapping, retained conflicts/corrected IDs, all portable decision types,
  malformed/oversized files, actual transaction rollback, stale previews,
  authenticated APIs, reopen/both-DB encrypted restore/reset boundaries,
  gallery criteria/toggles and journal recovery with chapter provenance, visible
  failed/retried layout Tasks, and UI late-file/write/uncertain-result ownership.
- Actual desktop synthetic-browser checks covered sharing tabs/keyboard focus,
  conflicting-version review, source-scoped gallery controls, rejected-save draft
  retention and compact checkbox/layout behavior. Successful additions are tested
  in disposable database/API fixtures; the sample browser refuses operational
  writes. No real source, account, private library or NAS was contacted.
- Available project stages 1, 3–6 and 8–9 pass: Python/JavaScript syntax, maintained
  styles, the 64-file tool bundle, shell/Compose/env/overlay/version guards and
  23 UTF-8/LF/noBOM Linux text files. Both required full commands were attempted:
  14 Windows collection errors remain for `fcntl`, `resource` and
  `socketserver.UnixStreamServer`. Native JAR checks need absent `unzip`/JDK `jar`;
  Docker is absent. Full Linux, image/native compilation/cold import, actual reader
  icon loading, mobile/screen-reader and live NAS/restart/power-loss proof remain
  open. No deployment, host policy, source/toolchain pin or reader auth change ran.
- Upgrade with the complete `autosourcarr-v0.4.2.zip`; retain active env/config,
  both deployed app DBs, signing identity, source profiles, downloads and `/data`.
  Rebuild the existing image and refresh the browser. The existing catalogue gains
  additive tables; the Codex development database remains outside the app/ZIP.

## 0.4.1 - 2026-10-18

- Implemented all four approved 0.4.1 UI proposals. Tasks → Repairs has compact
  Needs input / Running / Completed-ready / Failed-blocked groups, independent
  origin/state filters and a stable selected-task inspector. The existing original
  code, evidence/session, install, Fixed, Retry, Revert and source-input controls
  retain their backend guards. Polling preserves the selected code form/draft.
- Added private Identity review in the sidebar and Manage → IDs. Provenance notes,
  confirm/exclude/needs-review/inherited decisions, revision/evidence checks, shared
  edition warnings and retained history annotate portable lookup only. Original
  provider/matching/source records and numeric reader IDs remain unchanged. Manual
  references need review before confirmation. Merge conflicts and backups retain
  annotations; restoring old review history requires fresh review.
- Manage now has Overview / Serving / Metadata / IDs keyboard tabs. Live forms and
  independent save ownership preserve drafts through tab changes and overview
  refresh. Sources, imports and Wanted show active filters and view-only recovery;
  unobserved/stale/deferred outcomes remain separate from confirmed gaps.
- Kept source repair execution inside the instance and Repair AI setup under
  Sources. No runtime/toolchain pin, host policy, account, deployment or data reset
  change. Updated image/backend/assets/checker/worker to 0.4.1 and reader fallback
  to 1.4.4001. README distinguishes current anchor IDs/local reviews from a proposed
  shared, signed canonical catalogue; no registry or automatic sharing was added.
- Author self-review: 955 portable tests pass in 52.30s, with 5 skips and 1 known
  Windows symlink-privilege exclusion. This includes 12 new 0.4.1 tests for authenticated CAS,
  stale evidence, merge-conflict retention, additive migration/reopen, both-DB
  encrypted backup restore, reset boundaries, keyboard/live-form ownership and
  rapid-selection/late-response draft protection. The initial sample-history
  regression was corrected: general history excludes synthetic repair rows.
- Actual desktop synthetic-browser checks covered all four repair states, selected
  code-input retention through refresh, independent state filtering, dedicated and
  scoped identity review, per-entry drafts/rejected saves, Manage tab keyboard
  focus/draft retention, installed-only Health and import/Wanted filter recovery.
  Rendered compact tables fit their 578px containers without horizontal overflow.
  This is author testing against disposable/sample data, not a live NAS/source test.
- Available project stages 1, 3–6 and 8–9 pass: Python/JavaScript syntax, maintained
  styles, 66-file bundle, 12 shell parses, Compose/env/layout/version guards and 23
  UTF8/LF/noBOM shell/Docker files. Both required full commands were attempted and
  retain 14 Windows collection errors (`fcntl`, `resource`, `UnixStreamServer`).
  Native JAR validation still needs `unzip`/JDK `jar`; Docker is absent. Full Linux,
  image/native compilation/cold import, mobile/screen-reader and live NAS repair,
  restore/restart/power-loss checks remain open. No deployment or host change ran.
- Upgrade with the complete `autosourcarr-v0.4.1.zip`, retaining active env/config,
  both deployed app DBs, signing identity, source profiles, downloads and `/data`.
  Rebuild the existing image and refresh the browser. The additive review tables
  live in the existing catalog; no second runtime or Codex DB was introduced.
  Keep pre-upgrade backups: encrypted restores require their original application
  version/paths; restore with that version first, then upgrade. The old experiment
  and earlier archives are retained; Codex evidence remains outside the app ZIP.

## 0.4.0 - 2026-10-18

- Promote the complete Reimagined test-4 application into the main project and
  remove test branding. Retain the newer library/Wanted/queue/history, encrypted
  backup/restore, P01–P15 controls and LF startup/build guard. The separate test
  directory/archives remain historical. Keep the existing 2.3.2243 engine pins;
  P14 native 2.4 qualification is still pending.
- Remove the tiny import-backup and APK-installer disclosures; keep their forms
  directly visible. Import backup focuses its file input. Discover's single page
  size control is inline. Large filters/settings/evidence remain collapsible.
  Keep Tasks → Repairs and Sources → Repair AI responsibilities. Missing-code
  cards say Code needed, rather than claiming active execution.
- Add ASC Media ID v1 under Manage → Portable media IDs, with Copy ID and exact
  lookup through header Search. Identical catalogue/source anchors have identical
  IDs across installations. Keep work, edition, gallery and exact source scopes
  distinct, including supported NHentai/E-Hentai/Hitomi URL identities. Exclude
  known gallery access-token paths from source IDs. Local/unknown sources and
  unsafe/absent evidence mint no invented IDs. Inferred and locked associations
  are labelled factually; a lock is not represented as a new user confirmation.
  This is a portable cross-reference baseline, not a hosted universal catalogue.
  Different anchors require corroboration; ambiguous lookups return separate
  entries and never merge or bind chapters. Numeric reader/entry IDs, existing
  subscriptions, manual/import/edition decisions and reading state are preserved.
- Extend raw-code discovery to an installed source's declared index or its exact
  package in configured indexes. Recognise Keiyoushi's official source mapping;
  inspect explicit unambiguous same-publisher GitHub Source Code declarations and
  display code locations in repository previews. Local installs retain candidate
  provenance. Retry lookup, select the Keiyoushi project or supply custom code from
  Tasks → Repairs. Retry/version/hash fences, owned serial I/O and a bounded
  short-lived discovery cache prevent polling from launching downloads repeatedly.
- If HEAD removed a module, inspect bounded original-repository commit/parent
  history, prefer matching declared version-code hints, and archive a pinned
  historical candidate when available. Honour explicit commits without silent
  substitution. The public lookup did not recover build files for the supplied
  HentaiManga/NHentai examples; provide code or choose Unavailable reconstruction
  for those unresolved inputs. No unrelated fork, original-binary equivalence,
  compatible build or successful repair is inferred. Existing isolation, website
  ownership checks, cold import, identity/rollback gates and Fixed/Retry/Revert stay.
- Upgrade: keep active env/config, both app databases, signing identity, source
  profiles, downloads and /data. Use the complete main archive in the existing
  project path, rebuild the image and refresh the browser. Keep a consistent
  pre-upgrade data/config backup. Old encrypted backups require their original
  version/paths for restore before upgrading; those checks are not relaxed.
  No deployment, host-policy change, source-engine promotion or data reset occurs.
- Author validation: **943 portable tests passed**, five skipped and one existing
  Windows symlink case excluded, in 64.47s. Available JavaScript syntax/controlled
  behavior, Python/shell/Compose/env/styles, 66-file bundle and layout/overlay/docs
  checks pass. Full required pytest and Bash checker were attempted and retain
  14 Windows collection errors for fcntl/resource/UnixStreamServer. Native JAR
  validation needs unzip/JDK jar; Docker is unavailable. No compiled APK/JAR,
  full Linux suite, native image/CFR/source repair or live NAS success is claimed.
  An additional disposable fixture populated the released 0.3.45 schema definitions:
  original values/IDs/relationships in nine protected tables survived current
  initialization and reopen. Added import fields are compared by original column
  names, not by SELECT-star tuple size. This is not a live data migration proof.
  The actual desktop frontend was inspected with synthetic data: visible import
  form/file focus, Manage IDs and exact ID search. A stale page-title suffix was
  corrected; no mobile/screen-reader certification or real account/data mutation.
  Same authoritative Codex DB/research gate/history retained outside the release.

Next version proposal (0.4.1; not implemented or approved):

1. A compact repair queue with separate Needs input / Running / Blocked states,
   a persistent selected-source inspector and direct next actions. This would
   reduce repeated package/error text while preserving full evidence on demand.
2. A dedicated identity review workspace for provider cross-references and edition
   conflicts. A curated shared catalogue needs separate governance, coverage,
   correction and hosting decisions; the current IDs do not invent that service.
3. Split the long Manage dialog into Overview / Serving / Metadata / IDs with
   stable focus and explicit save ownership; retain keyboard/mobile checks.
4. Clearer active-filter summaries and empty-state recovery across Sources, import
   review and Wanted, without interpreting unknown or deferred observations as gaps.

## 0.3.48 - 2026-10-18

- Reimagined test 4 startup hotfix. The delivered test-3 startup contained CRLF,
  so Linux interpreted the carriage return on line 2 as part of `set -eu` and
  failed before Python. Correct `autosourcarr/start-truenas.sh` and the project
  checker to LF. Their shell behavior, restore-before-app ordering, workers1 and
  no-new-privs remain unchanged. The previous syntax-only checks missed this.
- Add a byte-level UTF-8/LF/no-BOM shell/Docker validator for project sources and
  final ZIP members, fixture regressions and a native POSIX execution regression.
  Run the guard automatically during image build on the copied startup and bundled
  scripts; stage it with the maintained tools. No helper service, host setup,
  permission change, data reset, application/API or core-workflow change is added.
- Rebuild/redeploy the existing test app after replacing source files, preserving
  active envs, both databases, source profiles, signing identity and /data. A simple
  restart of an old image retains its old startup bytes. The main project remains
  unchanged. This does not promote the source engine or remove native/live limits.
- Fresh author validation: 902 portable tests passed, five skipped (including the
  native POSIX execution case), one existing Windows symlink exclusion. The new
  guard first rejected both actual bad files; all 23 shell/Docker source files now
  pass UTF-8/LF/no-BOM checks. Source/bundle and archive fixtures pass, 64 maintained
  tool files are synchronized, and available Python/JS/shell/Compose/layout/version
  checks pass (0.3.48 → 1.4.3048). All 517 main-project files and the existing active
  env remain unchanged; backend application Python is byte-identical to test 3.
  Both required full-suite invocations still stop at 14 Windows POSIX collection
  errors. Native JAR validation needs unzip/JDK jar and Docker is unavailable.
  Windows/MSYS accepted the CRLF prefix, so that is not claimed as native Linux
  reproduction; the reported Linux error and exact shipped bytes are the root-cause
  evidence. No native image build, live TrueNAS restart, data reset or deployment
  was performed here; confirmation after the corrected image is rebuilt is pending.

## 0.3.47 - 2026-10-18

Reimagined test 3 applies the reviewed arr-research P01–P15 work to this separate
test project. The maintained application remains unchanged. Existing subscriptions,
manual titles/matches/groups, rejections, imports, separate works, chapter origins,
raw-first repairs, reader authentication and the 0.94 identity/edition safeguards
remain. No deployment, reset or source-engine pin promotion accompanies this ZIP.

| Proposal | Implemented behavior |
| --- | --- |
| P01 | Full/gap filters test membership in enabled shown source families/custom groups, including chapter-only sources. Unrelated raw counts cannot prove coverage; group changes invalidate cursors. |
| P02 | Complete feed pages are stored before ingestion. Bounded tails and independently delayed failed records survive restarts; a cursor advances only after all retained records succeed. Oversized/incomplete buffers fail closed. |
| P03 | Typed GraphQL object/error/data and critical connection/identity/pageInfo checks reject incomplete snapshots while preserving explicit empty results and string source IDs. |
| P04 | Latest quality-attempt status is separate from measured image quality. Local deferral/timeouts/analysis limits retain the last score and timestamp; received corrupt images remain distinguishable. |
| P05 | Page-first match aggregation where filters permit, keyed page statistics and selected-row display hydration preserve global filters, exact totals and keyset traversal. Aggregate-dependent filters retain their full-query path. |
| P06 | Independent attempt/success/complete clocks, configuration revisions, indexed due state and fair recent/backlog selection replace repeated oldest-row monitoring. Manual checks bypass automatic due waits; browser-idle admission/pacing remain. |
| P07 | Bounded provider/query/policy/configuration-generation request sharing owns cancellation and shutdown. Forced refreshes fence late old cache commits; failures cannot become cached empty results. |
| P08 | Task snapshots conditionally upsert changed records and remove only absent retained IDs. Clear, retention, corrupt-load, interruption/no-replay and final-drain behavior remain. |
| P09 | Local policy previews run the real current matcher twice with frozen draft maps. Original-member, identity, edition and separation gates remain; evidence/version checks bind apply. Applying saves future policy without merging the sample pairs. Late UI responses cannot revive an edited draft. |
| P10 | Manage exposes last complete observation, attempt, next due and incomplete/wait reasons. Freshness & performance reads saved state without probing a source. |
| P11 | Per-field display-only synopsis/artwork/creator locks or preferred attached records preserve omitted values. Explicit clear, revision checks, retained merge conflicts and snapshot rollback never overwrite matching evidence. |
| P12 | Opt-in reviewed future-serving choices enforce quality units 0–100, cutoff, minimum gain, sample age, manual locks and cooldown. Downloaded files/progress and existing origin history are retained. Policies retain conflict/history snapshots. |
| P13 | Bounded per-phase latency samples and owned decoder accounting are visible in the UI. A shared decoder limit covers actual thread lifetime, including repeated cancellation. Pixel memory is labelled as an estimate. |
| P14 | Explicit authenticated checks identify current/target API capabilities, including batch-reorder support. 2.3/2.4 fixtures are covered. Native 2.4.2366 upgrade/startup/download/progress/event/rollback qualification remains pending: Docker/Java tools are unavailable locally. Pins stay at 2.3.2243; authoritative polling remains. |
| P15 | Populated additive schema/reopen tests compare protected values and parent/child relationships; populated encrypted two-database restore and reset-retention cases complement foreign-key checks. No drop/rebuild migration or live reset is introduced. |

New controls: **Manage → Display metadata ownership / Reviewed serving upgrades**;
**Duplicates → Evidence & bulk review → Simulate a matching policy**; and
**Instance → Freshness & performance**. Source repair stays under Activity/Repairs;
Repair AI continues to own provider/account setup.

Same-workload synthetic self-review: at 10,000 works/80,000 source records, median
updated-page time was 663.883 → 53.584 ms (12.39×); 87 compared pages retained the
same pre-existing fields/totals/cursors. At 301 retained task rows/100 flushes,
changed rows were 60,199 → 100 and WAL 9,064,032 → 828,152 bytes. Journal latency
was roughly unchanged (18.823 → 19.548 ms), so no latency improvement is claimed.
Six 3.84 MP/8.35 MB image decodes used at most two actual slots and drained to zero;
logical active input charges peaked at 16.70 MB, RGBA estimate at 30.72 MB. These
are local fixtures, not NAS throughput or isolated native decoder RSS measurements.

Fresh validation (author self-review): 892 portable tests passed, four skipped,
one existing Windows symlink case excluded. JavaScript syntax/controlled behavior,
preview-response races, Python syntax, styles, Compose/env wiring, 63-file bundle
sync, 12 shell scripts and checker stages 8–9 passed. All 517 main-project files
and the retained active env are byte-for-byte unchanged; 19 core repair/matcher/
storage/extension files and 678 pre-existing top-level/class definitions remain
unchanged in the experiment. Both required full-suite invocations were attempted
and stopped at 14 Windows collection errors for fcntl/resource/UnixStreamServer;
this is not a full Linux suite pass. Checker stage 7 validated version mapping
0.3.47 → 1.4.3047, then stopped because unzip/JDK jar is unavailable; Docker is
unavailable at stage 10. No compiled APK/JAR parity, native engine qualification,
power-loss/restart durability, live repair/main-Suwayomi/TrueNAS or production proof
is claimed. Browser checks used the actual frontend with synthetic sample data,
including ownership/upgrade/review/status entry points and a 390×844 narrow view;
the 326 px dialog body had no horizontal overflow. Operational sample writes were
explicitly rejected and no real account/source/application database was opened.

Further improvements found, proposed only: profile and bound the first perceptual
hash initialization (one cold fixture took 18.234 s); measure narrower UNION branch
predicate pushdown for match hydration; and evaluate dirty-ID journal serialization
to reduce remaining CPU cost while preserving retention/clear/drain behavior.

Upgrade only the separate test instance using its existing paths. Retain active
env files, both app databases, signing identity, source profiles and /data; stop
and drain the instance before copying files or taking a filesystem rollback copy.
The new state is additive in the existing catalog; no helper service or host setup
is added. Build this complete project with its existing image workflow. Keep the
test-2 archive plus a consistent pre-upgrade data/config snapshot for rollback;
encrypted in-app restore retains its same-version/path requirement. Downloaded
main-Suwayomi files are outside the app ZIP and remain on their existing mount.

## 0.3.46 - 2026-10-18

- Reimagined test 2: three-way integrate the complete current 0.3.45 public source
  and its raw-first repair lifecycle into the separate test project. Preserve the
  primary source and test-1 history. Keep all original manual/import/source/reader
  controls, the 0.94 title floor, original-member and edition gates, exact source
  rollback bytes and per-instance repair isolation. No primary promotion occurs.
- Add globally filtered, lightweight cursor-paged library projection and honest
  totals; remove the library's 500-row view ceiling. Keep poster/overview/table
  modes and lazy full detail controllers. Direct add/remove membership, per-work
  monitoring and queue controls use the existing authenticated main Suwayomi.
  Removal keeps downloads; uncertain writes are observed once before retry.
- Separate Wanted outcomes. Main chapter IDs and explicit downloaded flags prove
  missing downloads; source-record coverage is separate. Failed, deferred, stale
  refresh or verification observations retain uncertainty. Monitoring waits for
  idle and uses the existing background source admission/pacing before main
  Suwayomi acquisition. It skips downloaded/already queued chapters and bounds batches.
- Persist general Activity in the existing catalog with one coalescing owned
  writer, a 300-finished-task/seven-day bound and paged history. Restarted running
  records become interrupted; cancelled work stays cancelled. Safe retry performs
  an explicit observation only. Corrupt history is retained and reported without
  resetting it or preventing unrelated app startup; existing import/repair recovery remains.
- Add Web UI encrypted backup/export and verified restore preview. Pause/drain
  instance writers and use bounded WAL-aware snapshots of both databases; include
  retained settings/decisions/signing/source state. Use AES-256-GCM and an unsaved
  passphrase, quotas and explicit export/delete controls. Restore is bound to the
  same version/container paths, verified hashes/schema and typed confirmation.
  Restart replays the private journal before DB open and retains old state for
  recovery. No actual production backup, restore or restart is performed here.
- Add revision-checked per-work serving preferences after existing candidate
  identity/rejection gates. Preserve defaults and downloaded chapters. Extend
  existing matching/history evidence with reviewed separate-work batches and
  exact scoped undo; do not invent missing historical evidence or general merge undo.
  Preserve new work preferences/monitoring through catalog merges, retaining
  conflicts for review and clearing observations for recheck. Ordinary rebuilds
  keep dormant stable-ID preferences; Full reset explicitly clears them. Explicit
  membership removal reconciles exact same-work aliases and keeps other sources.
- Enforce single-worker ownership in the launcher. Cleanup attempts every ordered
  owner and preserves a primary startup error/cancellation. Cap distinct covers,
  coalesce requests and apply an absolute loader deadline. Decode streamed buffered
  gzip/deflate assets with a bounded output allocation; pre-buffered transports
  retain decoded-size checks. Keep actual reader image streaming unchanged.
- Separate library, profile, queue, Activity, backup and review responsibilities
  into bounded modules/controllers. Deliver one generated UI stylesheet from
  maintained component sources. Developer core-sync guards live outside the app;
  upstream changes cannot silently overwrite reviewed overlays or private config.
- Upgrade: use this complete isolated test project, rebuild its existing image and
  refresh the browser. Preserve env files, both DBs, signing identity, source profile
  and persistent /data. Versions/assets/fallback are aligned at 0.3.46 / 1.4.3046.
  No new Compose service, deployment, host permission change, data reset, paid model
  invocation or independent reviewer is included.
- Author validation: 834 portable pytest cases passed in 49.27s, with four
  POSIX skips and one existing Windows symlink-privilege exclusion. Twenty-one
  JavaScript syntax checks, 19 controlled Node fixtures, 12 shell syntax checks,
  Python/YAML/env/version checks and 63 maintained bundle files passed. Separate
  checker stages 8-9 pass. A synthetic 5,000-work projection fixture returned the
  same first 60 work IDs: 106.083 ms median for full summaries before slicing,
  16.442 ms for the light paged projection (seven repetitions). This is local
  database/representation evidence, not production network/NAS latency.
- Actual rendered author checks used the synthetic preview at 1440x900 and
  390x844: complete/paged library, three views, distinct Wanted states, source/title
  controls, retained scope after reload, per-work/retained profile forms, queue,
  native backup/restore dialog, audit, Activity history and the 0.3.45 repair entry
  point. Mobile keyboard wraps from System to menu to Manga; closed navigation is
  inert, document width stays inside the viewport, and the native tool dialog fits.
  Screenshots and raw checks remain outside the app/archive. These are self-review,
  controlled upstream/native boundaries and fixture checks, not independent review.
- Retained initial failures: pre-buffered HTTP transport needed its decoded-size
  compatibility path; legacy tests expected layered CSS and cleanup-error masking;
  worker version metadata needed alignment. Browser checks found a sample history
  catch-all shadowing Activity and tool buttons inheriting the legacy Browse
  handler. Focused failing reproductions, fixes and affected checks passed. Native
  repair/account ownership now explicitly gates snapshots before any tools stop.
  Publication codes and distinct source/additional-record counts were checked
  against existing core contracts and corrected with focused regressions.
- Preservation: primary 0.3.45 public source is unchanged; 27 protected paths and
  824 existing definitions in main/service/Suwayomi remain structurally unchanged.
  Only declared service methods change for profiles/merge/reset preservation.
  Reader/authentication, matching/pacing/isolation/signing and retained env bytes
  are preserved; no latest public upstream file was removed.
- Limits: required full pytest and Bash gate were attempted and stop at 14 native
  Windows collection prerequisites (fcntl/resource/UnixStreamServer). The explicit
  suite-skip auxiliary checker reaches missing unzip/JDK jar at stage 7; Docker is
  unavailable. No full Linux/image gate, compiled APK/JAR, native ownership/restore
  restart/power-loss proof, real provider/source repair, live main-Suwayomi/TrueNAS
  integration or screen-reader audit is established. Native/image/live gates remain
  open before promoting this test onto a production instance.

## 0.3.45 - 2026-10-17

- Add source-code retention to normal source install/update and archived installs,
  with restart backfill, explicit task ownership/drain, one acquisition lane,
  immutable commits, bounded private download caches and deduplicated code objects.
  Use the source's declared original ecosystem; known Keiyoushi indexes map to its
  real source repository. Package names alone never establish provenance. Public
  raws use the persistent /data/sources/raw folder, which may be mounted from the
  same raw-only host directory in two instances. Installed sources/profiles,
  credentials, feedback and working/revert journals remain private.
- Add Tasks → Repairs → Source code archive. Missing code waits for an original
  project ZIP/repository or an explicit Unavailable choice, without occupying an
  AI/build slot. Bind uploads/resume to package, installed version/hash and request
  generation. Preserve private admission IDs/intents and reconcile uncertain RPC
  writes without replaying AI work. Keep all repairs in Tasks, AI setup/account in
  Sources → Repair AI, and Health installed-only.
- Attempt complete installed-class/resource reconstruction with image-bundled,
  SHA256-checked CFR 0.152 and its MIT notice. Keep derived code private/labelled;
  decompilation does not recover lost comments or guarantee compilation. Raw-only
  archival can retain derived code without asserting source loading or working
  proof. Repair separately cold-imports the exact saved binary, using actual
  bytecode candidates where loading metadata is absent, and requires matching
  source IDs/names/languages/full website addresses. Preserve strict APK conversion
  comparisons and every original rollback byte; no module-name identity guesses.
- Preserve compatible Gradle builds, including a single release APK converted in
  a fresh private source engine. Add isolated Java overlay builds for reconstructed
  code, retaining unchanged installed classes/resources. Modified reconstructed
  JARs drop obsolete publisher signature
  files while original rollback files remain exact. Do not claim original signing
  identity for a local modification. Keep model edits bounded
  to existing approved source files; no build/dependency/signing migration. Code
  exports include typed public provenance/recipe/overlay information for deliberate
  transfer to another instance, where input is private/user-provided and revalidated.
  Exclude project agent configuration, private files and compiled source packages
  from editable/shared raws. Image imports reuse the baked tool bundle.
- Check public website/API observations after acquiring code; research matching
  original-maintainer files at a pinned commit and collect bounded public-search
  leads. Search hits and availability/TLS alone grant no address authority. Preserve
  verification/rate-limit/transport uncertainty, pacing, response/deadline/resource
  limits, public pinned egress and mandatory namespaces. A corresponding maintainer
  reference plus first-party evidence can corroborate a move; ambiguous changes
  require exact-candidate review. Permit only a controller-checked literal build
  baseUrl correction; independently validate source content and restarted addresses.
- Newly requested installed-source repairs have automatic-install intent after
  compile/content/hash/identity/address gates. Old jobs remain history and are not
  replayed or automatically installed. Use per-source Fixed, Doesn’t work / Retry
  and Revert controls. Fixed promotes current code/binary to last functioning;
  Revert before a later fix undoes that promotion and restores prior code/binary/
  confirmation. Retry records feedback and makes one explicit new attempt from
  retained code. Preserve anchors through failed retries and reject stale actions.
  Integrate stopped-engine journals so interrupted installs restore bytes/history
  together and interrupted explicit Revert completes the same target. Keep current
  controls through Clear task history and the worker's bounded summary. Show user
  confirmation dates separately from sampled health and installation dates.
  Corrupt/unavailable code-store or history initialization disables those repair
  features without resetting their files or preventing unrelated app startup;
  unsafe interrupted live promotion still requires recovery before source startup.
- Upgrade: overlay the complete project, retain active env files, both app DBs,
  signing identity, /data and source profiles, rebuild the existing image, then
  refresh the browser. No service, Docker socket, deployment, data reset or host
  permission change is added. Optional shared raw storage is the only extra mount
  if sharing is desired. The supplied 2026-10-17 TrueNAS log is a pre-init AppArmor
  application failure after successful image builds; it does not establish a
  Python crash or the original stop trigger. Current host/kernel/profile state is
  unverified; the existing host policy still has to load before container startup.
- Author validation: 782 portable pytest cases passed in 46.95s, four POSIX cases
  skipped and one existing Windows symlink-privilege case deselected. Seventeen
  JavaScript syntax checks and 17 controlled Node fixtures passed, including archive
  browsing without an AI account. Eleven shell checks, Python/YAML/env/version
  checks and 63 maintained bundle files passed; independent checker stages 8-9 pass.
  Required full pytest/checker were attempted and stop at 14 Windows collection
  prerequisites (fcntl/resource/UnixStreamServer). The explicit-skip auxiliary
  checker reaches stage 7 and needs unavailable unzip/JDK jar; Docker is unavailable.
  Author preservation review found no removed public files, 21 critical paths and
  172 unrelated/critical function ASTs unchanged; active private-file metadata is
  unchanged. A real anonymous GitHub SHA lookup and pinned Keiyoushi source capture
  retained 628 selected files and exported a portable 1.65 MB code ZIP. Its binding
  version was synthetic; no installed-binary match or website success is asserted.
  The initial wrong en/mangafire smoke module correctly requested input; the actual
  all/mangafire module succeeded using the same private 80 MB repository cache.
  Initial portable failure mocked the replaced orphan-admission seam; the fixture
  was adapted and exact original-APK forwarding was independently exercised at the
  new boundary. Initial UI reader fixture needed the bundled Python environment;
  a route fixture needed FastAPI's supported OpenAPI view; a missing pytest path
  and two stale generated worker copies were corrected before final checks.
  Native JVM/Android/image builds, real decompilation/conversion/class loading,
  rendered-browser/accessibility and successful live TrueNAS/source repair remain
  unverified. Controlled native/AI/compiler seams are not live proof. These are
  author checks, not independent review. Code/requests/history/cancellation/recovery
  contracts were exercised with confined files and fake native/provider boundaries.
  Research receipts and separate results stay outside the app ZIP; update the same
  sibling knowledge DB and preserve the separate Reimagined index namespace.

## Reimagined test 1 - 2026-10-17

- Complete isolated experimental copy in `autosourcarr reimagined`, based on the
  current 0.3.44 source. Apply Sonarr v5-style navigation, header, typography,
  neutral surfaces, compact toolbars, rounded posters, full-width entry details
  and mobile navigation. Keep all original workspace destinations and backend
  workflows; adapt their presentation to manga/source management.
- Add Posters / Overview / Table library views, saved-catalog command search with
  `/`, a Wanted view of confirmed source-record chapter gaps, and statistics from
  the existing health snapshot. Keep unknown counts distinct from zero, retain
  all manual/import/chapter decisions and preserve reader API authentication.
- Keep all production Python, SQLite, scheduler, source, repair/account and reader
  implementations unchanged. Existing request/mutation ownership, lazy bootstrap,
  typed reset confirmation, independent Repairs filters and safe view-state
  restoration remain. New presentation hooks sit around existing controllers.
- Bundle local open-license fonts and a loopback-only synthetic preview. Run
  `Preview-Reimagined.ps1` on this Codex Windows machine, or
  `python scripts/preview-reimagined.py`. Sample observations/artwork are labeled;
  unsupported operational mutations return an explicit 409 and change no real
  configuration, account, library or source. No additional Compose service is added.
- Author checks: 682 portable pytest cases passed, including six preview HTTP/data/
  isolation checks; four POSIX skips and one pre-existing Windows symlink-privilege
  exclusion remain. Five additional manual-action compatibility cases passed.
  Eighteen JavaScript syntax checks and 17 controlled Node fixtures passed,
  including the new route/unknown-count checks. Available checker stages 1 and 3-6
  plus separate 8-9 passed, with 11 shell syntax checks and maintained bundles aligned.
  A label-specific old navigation guard was updated for the experimental SVG/text
  sidebar; core destination and preservation guards remain.
- Actual rendered author checks used synthetic observations at 1440x900 and
  390x844: library views and filtering, entry/lazy chapter details, keyboard search,
  rapid query changes, empty-filter recovery, Wanted, source Health restoration,
  Activity/Repairs, appearance and mobile focus/navigation. Fixed stale actionable
  search results during the debounce, root-source routing overriding saved Health,
  inaccurate filtered-empty guidance and the light-header button cascade. Initial
  preview cover-path/gap/date fixtures and checker assertions were corrected and
  affected checks repeated. Screenshots and raw development outputs stay outside
  this project and its archive. This is self-review, not independent review.
- Limits: the required full pytest and Bash gate stop at 14 Windows collection
  errors for fcntl/resource/UnixStreamServer. The explicit-skip auxiliary checker
  reaches stage 7, where unzip/JDK jar are unavailable; Docker is unavailable.
  No full Linux/image gate, compiled APK/JAR, live Suwayomi/TrueNAS, real account,
  successful source repair or screen-reader audit is claimed. The sample preview
  proves selected UI flows against fixtures, not live integration behavior.
- Delivery: complete experiment archive in the existing sibling archive directory;
  original application and copied active env files are checked for preservation.
  The same authoritative development knowledge DB retains the original research,
  consultation history and a separately identified experiment. No live data reset,
  installation, deployment, host policy change or paid AI operation occurred.
- Test/upgrade: backend/root/asset version remains 0.3.44, extension fallback
  1.4.3044. This is separately labeled Reimagined test 1, not a primary release
  replacement. Keep the experiment's folder separate and retain each configured
  instance's env files, databases, source profile, signing identity and repair data.

## 0.3.44 - 2026-10-12

- Separate Repairs origin filtering from General Tasks, default to All repairs,
  and remove the unfiltered latest-result message above empty panels. Use the
  actual Ongoing / Completed / Failed sections and selected-view task counts.
  Diagnostic UI facts now include the independent repair filter and its presence.
  The supplied 0.3.43 export confirms General was Automatic while all 17 observed
  jobs were manual and blocked; this is the demonstrated visibility root cause.
- Preserve strict APK reconversion comparison. When it differs, independently
  cold-import the exact installed JAR with real retained APK metadata, after
  package/version/provenance/input-hash checks and isolated APK identity checks.
  Accept that separate loading contract only after an empty-runtime JAR import
  matches installed source IDs/names/languages and full website addresses. Retain
  every original JAR payload, the raw rollback file and failed comparison facts.
  Do not infer converter determinism or mark the differing APK conversion verified.
  The reported nine class differences can now reach this separate cold-import
  check; successful import/build/repair on the actual host remains unverified.
  Reference: [pinned Suwayomi package loading](https://github.com/Suwayomi/Suwayomi-Server/blob/v2.3.2243/server/src/main/kotlin/suwayomi/tachidesk/manga/impl/extension/Extension.kt).
- Check bounded public websites and advertised same-origin API addresses before
  the APK gate, using the existing private network namespace and egress policy.
  Preserve pacing, cancellation, response/request/deadline limits, TLS verification
  and uncertainty/CAPTCHA/rate-limit distinctions. Do not forward user credentials
  or cookies or decompress ignored identity responses. A normal contact CAPTCHA
  does not itself mark an otherwise usable page as a verification challenge.
- Let the bounded source repair stage corresponding base/API address updates from
  observed HTTPS redirects or a pinned maintained module. Require unchanged
  package/source/loading identities and independent functional content samples.
  Show changed website origins at installation and bind approval to the reviewed
  candidate hash and recheck the restarted source's reviewed address before recording
  installation success. Unknown destinations stay blocked; candidate installation stays
  manual. No live website address, package or API endpoint was changed locally.
- Add authenticated, bounded Download source inputs to selected job evidence.
  Export only baseline.jar, source.apk and input-hashes.json after regular-file
  and recorded-hash checks. Original binary inputs are unredacted and should be
  shared privately; account/config/cookie/library/conversation files are excluded.
  Diagnostic exports retain only allowlisted, privacy-filtered website/proof facts.
- Author validation: 676 portable pytest cases passed, four POSIX cases skipped
  and one existing Windows symlink-privilege case deselected. Sixteen JavaScript
  syntax checks, 16 controlled Node fixtures, 11 shell syntax checks, 48 maintained
  bundle files, Python/YAML/templates/version checks and checker stages 8–9 passed.
  Required full pytest and checker stop at 14 Windows collection errors for
  fcntl/resource/UnixStreamServer. The explicit-skip auxiliary checker reaches
  stage 7, then needs unavailable unzip/JDK jar; Docker is unavailable. Controlled
  source-engine fixtures verify the new cold-import flow and failure gates; they
  do not execute JVM bytecode. Initial regression/fixture failures were corrected
  and rechecked. Source review also corrected the probe's namespace code path,
  async filter-change review ownership and post-restart address verification.
  Actual rendered browser, Linux/image, compiled APK/JAR, converter semantics and
  successful live source repair remain unverified. These are author checks, not
  independent review. Complete archive and same-DB save integrity are checked
  separately before delivery; private inputs/config are excluded from source indexing.
- Upgrade: overlay the complete archive, rebuild the existing image and refresh
  for 0.3.44 assets. Keep env files, both app databases, source profiles, manual
  decisions, signing identity and repair workspace; extension fallback 1.4.3044.
  Old blocked jobs retain original evidence and are not replayed. Start a new repair
  to use the new validation path. No live deployment, additional host permission,
  paid AI invocation, compiled APK/JAR or successful source repair is claimed.

## 0.3.43 - 2026-10-11

- Show the latest worker repair result at the top of Tasks → Repairs. Count retained
  failed/blocked jobs in its subtab badge and mention blocked repairs in the idle
  task summary. Keep Repair AI account-only and the removed Open Repairs shortcut
  absent. Existing evidence/navigation/request ownership remains intact.
- Include observed worker jobs in diagnostic task rows/counts using the same read-only
  projection as Tasks, without starting tools or probing at export time. Add bounded
  browser task-view facts: selected section/lane, required-element presence and
  observed job counts. No input values or source content are collected there.
- Keep exact APK/JAR payload equality mandatory. A mismatch now retains bounded
  changed/missing/added and code/resource counts plus APK/JAR hashes in job evidence
  and privacy-filtered exports. No raw payload, class name, prompt or credential is
  included in the comparison. Original binary/backup and provenance gates are unchanged.
- The supplied 0.3.42 export shows sandbox readiness available with zero known proc
  overrides. Its latest ManhuaFast job was accepted and blocked after about five
  seconds by an APK/JAR mismatch, before AI. Exact differing entries/converter
  behavior and the user's deployed task DOM were not supplied; do not infer a wrong
  APK or relax rollback proof. No further host permission change was performed.
- Author validation: 638 portable pytest cases passed, four POSIX cases skipped
  and one existing Windows symlink-privilege case deselected. Twenty focused
  projection/privacy/diagnostic cases also passed after prioritizing recent worker
  failures within the diagnostic row budget. Sixteen JavaScript syntax checks,
  15 controlled Node fixtures, 11 shell syntax checks, 45 maintained bundle files,
  Python/YAML/templates/version/UI checks and checker stages 8–9 passed. Required
  full pytest/checker stop at 14 Windows collection errors for fcntl/resource/Unix
  socket support. The explicit-skip auxiliary checker stops at missing unzip/JDK
  jar in stage 7; Docker is unavailable. Actual rendered browser, Linux/image,
  compiled APK/JAR and successful source repair remain unverified. Initial bundle
  sync hit Windows access denial; approved workspace access then synchronized it.
  Author checks are not an independent review.
- Upgrade: overlay the complete archive, rebuild the existing image and refresh for
  0.3.43 assets. Keep env files, both app databases, source profiles, manual decisions,
  signing identity and repair workspace. Extension fallback is 1.4.3043. Existing
  blocked jobs retain their original evidence; new comparison details require a new
  attempt. No live deployment, AI invocation, compiled APK/JAR or successful source
  repair is claimed.

## 0.3.42 - 2026-10-11

- Restrict Sources → Health to installed variants, including installed disabled
  languages. Retain removed sources' last-check/pass/repair dates and recent
  history; show them again on reinstall. Guard cached UI responses and hide old
  selected evidence when its source is removed. Daily eligibility is unchanged.
- Remove the redundant Open Repairs shortcut from Tasks → General. Repairs keeps
  its existing subtab, forms, job actions and evidence; Repair AI remains account
  setup only. No repair/account, library or matching behavior is changed.
- Diagnose the supplied procfs mount failure with the custom AppArmor profile
  already active. Docker proc masks are a possible separate blocker. Add exact,
  conditional `systempaths=unconfined` guidance and effective-mask verification
  to the host guide/download and deployment comments. This option removes Docker
  proc/sys path masks; retain the existing non-root, capability, seccomp and
  AppArmor protections. Keep mandatory private PID/proc/network/user isolation
  and fail closed; no parent-proc bind or privilege fallback is introduced.
  Retain only bounded numeric counts of known proc overrides in diagnostics,
  excluding raw mount tables/host paths. Missing/truncated facts remain unknown.
  References: [Docker rootless procfs](https://github.com/moby/buildkit/blob/master/docs/rootless.md#docker),
  [Compose option handling](https://github.com/docker/compose/blob/main/pkg/compose/create.go)
  and [Linux mount visibility checks](https://github.com/torvalds/linux/blob/master/fs/namespace.c).
- Author validation: 634 portable pytest checks passed, 4 POSIX cases skipped and
  1 existing Windows symlink-privilege case deselected. Installed-only filtering,
  retained history/reinstall, cached evidence removal, task navigation, bounded
  mount diagnostics/privacy and the Web UI host-policy download have controlled
  regressions. Python compilation, YAML/templates, 45 bundle files, 16 JS syntax
  checks, 14 controlled Node fixtures, 11 shell syntax checks, version/UI/auth guards
  and separate checker stages 8–9 passed. Required full pytest/checker stop at
  14 Windows collection errors for fcntl/resource/UnixStreamServer. The explicit-skip
  auxiliary checker stops at stage 7 without unzip/JDK jar; Docker is unavailable.
  Actual Linux/image, compiled APK/JAR, rendered browser and live repair remain
  unverified. The new 0.3.41 diagnostic export confirms the same private-proc mount
  denial, a ready source engine, and resolved earlier backend startup records;
  it does not establish the exact denying mount/policy or successful repair.
- Upgrade: keep active env files, both app databases, source profiles, manual
  decisions, signing identity and repair workspace. Overlay this complete archive,
  rebuild the existing image and refresh for 0.3.42 assets; extension fallback is
  1.4.3042. The proc-specific host option must be applied to the saved deployment
  separately when needed. No live deployment, data reset, compiled APK/JAR or
  independently verified repair success is claimed.

## 0.3.41 - 2026-10-11

- Add Sources → Health with on-demand checks, a daily-check toggle, per-language
  results, checked package versions, last check/pass/confirmed repair installation
  dates and eight recent checks per source. Persist observations in the existing
  catalog database; no additional runtime database or Codex dependency is added.
  Older unknown dates remain unknown. Ready candidates do not count as fixes;
  recorded repairs/rollbacks after a check require a fresh sample. Other extension
  updates/preferences can also require a manual recheck; health is dated evidence.
- Sample catalog/search, metadata/chapters, a cover, page list and one page image.
  Bound source requests, pacing, runtime and image decoding. Daily checks wait for
  browser idle and use reserved background source capacity; manual checks progress
  independently. Serialize variants of the same website, retain per-source daily
  reservations across restarts, coalesce duplicate manual submissions and drain
  owned requests/database/image work on cancellation/reset/shutdown. Preserve source
  preferences, library contents, matching/provenance and existing CAPTCHA gates.
  Empty/unsupported samples, local capacity, timeouts, rate limits and human
  verification remain distinct. Health checks do not use AI or claim every chapter works.
- Move repair forms, original APK evidence, readiness, daily repair controls,
  installation/rollback actions, jobs and sessions into Tasks → Repairs. General
  tasks, including source-health jobs, remain in Tasks → General. Sources → Repair AI
  contains only provider setup, account/login status and details. Preserve account
  operation ownership and clear hidden device codes when leaving Repair AI.
- Record the user's report that manga-cluster starts again after the AppArmor
  startup failure. Correct host guidance to start with the supplied seccomp policy
  and require positive profile-load/registration checks before an AppArmor override.
  A write-time ENOENT can indicate an unloaded profile; it does not prove the proc
  interface is absent. No privileged/unconfined fallback or host modification is
  added. References: [Docker AppArmor](https://docs.docker.com/engine/security/apparmor/)
  and [runc's matching error report](https://github.com/opencontainers/runc/issues/3831).
- Author validation: 625 portable pytest checks passed, 4 POSIX cases skipped and
  1 existing Windows symlink-privilege case deselected. SQLite persistence/upgrade,
  source sampling, idle/manual admission, cancellation/thread drain, authentication,
  layout ownership and account privacy have controlled regressions. The final run
  used the original local development dependencies; an earlier transient native
  Pydantic import block cleared without policy or requirement changes. Python
  compilation, YAML/templates, 45 bundle files, 16 JS syntax checks, 14 controlled
  Node fixtures, 11 shell syntax checks, version/UI/auth guards and separate checker
  stages 8–9 passed. Required full pytest/checker stop at 14 Windows collection
  errors for fcntl/resource/UnixStreamServer. The explicit-skip auxiliary checker
  stops at stage 7 without unzip/JDK jar; Docker is unavailable. Actual Linux/image,
  compiled APK/JAR, rendered browser and live source checks remain unverified.
- Upgrade: preserve active env files, both app databases, source profiles, manual
  choices, signing identity and repair workspace. Overlay the complete archive and
  rebuild the existing image; refresh for 0.3.41 assets. Extension fallback is
  1.4.3041. Health sampling is enabled daily as requested, with a Web UI opt-out;
  AI-assisted daily repair remains a separate opt-in. No live deployment, data
  reset, independently observed repair success or compiled APK/JAR is claimed.

## 0.3.40 - 2026-10-11

- Move source-repair jobs, progress, state, Evidence & session and candidate
  install/discard/cancel controls into Tasks. Use the existing helper health check
  to populate the shared task lists and badge, with no extra integrated-task probe
  or source request. Keep account/setup, original-APK attachment and start controls
  in Sources; Tasks links to them. Wrap long packages/messages within task rows.
- Retain new jobs' release version and manual/automatic origin. Label older records
  with an unknown version rather than attributing them to the running release.
  Retain stale history while disabling stale actions, follow package-promotion
  state, retain review actions after a failed install, show rollbacks accurately,
  and guard evidence responses against selection/navigation races. Clear
  history hides finished repair rows until app restart, preserving on-disk evidence,
  running work and ready candidates awaiting review. Cancellation remains a request
  until worker cleanup finishes.
- The supplied 0.3.39 export confirms namespace denial (exit 1) on two fresh
  beehentai repairs; the pasted nhentai manifest failure is a retained older job.
  The export omitted exact stderr/host checks, so the particular denying policy is
  unknown. Retain bounded, redacted sandbox stderr and allowlisted namespace,
  AppArmor, seccomp and no-new-privileges facts in diagnostics, including a tools
  check captured before the next helper heartbeat. Keep prompts, conversations,
  patches, package bodies and credentials excluded.
- Reject known isolation failures before manual package/archive preparation;
  automatic checks defer before upstream I/O or daily reservation. Also reject
  known failures at private-worker admission, preserving the execution-time
  sandbox check for unknown/legacy readiness. Do not weaken isolation, replay old
  jobs, add services, modify host policy or change the working account launcher.
  Docker/the host must permit namespace creation; a process cannot grant itself
  those inherited permissions. Existing supplied policies and the exact checks
  guide host setup. References: [Docker seccomp](https://docs.docker.com/engine/security/seccomp/)
  and [Docker AppArmor](https://docs.docker.com/engine/security/apparmor/).
- Author validation: 604 portable pytest checks passed, 4 POSIX cases skipped and
  1 existing Windows symlink-privilege case deselected. Python compilation,
  Compose/template checks, 45 maintained bundle files, 15 JavaScript syntax checks,
  13 controlled Node fixtures, 11 shell syntax checks, source/version/UI/auth guards
  and separate checker stages 8–9 passed. The required full suite and required
  checker stop at 14 Windows collection errors for fcntl/resource/UnixStreamServer.
  The auxiliary checker explicitly skips that suite and stops at stage 7 because
  unzip/JDK jar is absent. Docker is unavailable. Tests use temporary storage and
  controlled HTTP/DOM boundaries; actual Linux namespaces/process cleanup, image
  and APK/JAR builds, rendered browser behavior and successful live repair remain
  unverified. Initial stale UI fixtures and route-location guard failures were
  repaired and rechecked; separate development transcripts remain outside the ZIP.
- Upgrade: preserve active env files, both app databases, source profiles, manual
  decisions, signing identity and repair workspace. Overlay the complete archive,
  rebuild this instance and refresh to load 0.3.40 assets; extension fallback is
  1.4.3040. Use Tasks → Repair tools → Check repair tools, then start a new repair
  only after the full isolation check passes. An application archive alone cannot
  fix a denying host policy. No deployment or successful live repair is claimed.

## 0.3.39 - 2026-10-11

- Record the user's live confirmation that 0.3.38 ChatGPT login worked. Preserve
  account limits, private runtime, supervision and credential handling. The current
  orphan-repair evidence is a different failure: blocked during checking-backup,
  with no AI conversation or patch. The generic manifest rejection hid the format
  mismatch; the user's particular installed bytes are not available locally.
- Support APK-derived installed JARs that lack AndroidManifest.xml. The pinned
  Suwayomi conversion copies bytecode and assets without retaining that manifest,
  while its direct JAR importer requires one. Use a retained original APK only
  after digest/package/version/source checks and an isolated reconversion whose
  complete uncompressed JAR payload matches the saved installed binary. Add the
  decoded manifest only to a private validation envelope; preserve the raw backup
  and its original hash for promotion/rollback checks. Native JAR inputs retain
  their direct path. References: [pinned Suwayomi installer/converter](https://github.com/Suwayomi/Suwayomi-Server/blob/v2.3.2243/server/src/main/kotlin/suwayomi/tachidesk/manga/impl/extension/Extension.kt)
  and [Android binary resource structures](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/libs/androidfw/include/androidfw/ResourceTypes.h).
- Keep metadata provenance explicit: an existing completed installation receipt,
  or an original APK explicitly confirmed by the user, supplies loading metadata.
  Equal converted payloads do not independently recover metadata discarded from
  the installed JAR. Evidence reports that basis and the APK's package/entrypoint/
  library contract. Candidate identity comparisons remain strict, and actual
  import/functional checks remain isolated. Missing, altered, ambiguous, wrong-
  version, wrong-source or nonmatching inputs stop before a build or AI repair.
- Add **Sources → Source repairs → Start a source repair → Original APK for repair**.
  Attaching an explicitly confirmed original APK stores evidence only; installed
  code, source settings and library data remain in place. Bound input to 32 MiB,
  validate its package, retain it by digest and select it for a new repair. Changing
  the source or file clears confirmation. Bind saved confirmation to the selected
  installed version and raw JAR hash so a later update cannot reuse stale consent.
  Preserve a newer file selection while
  an earlier request completes; never automatically replay an uncertain attachment.
- Decode bounded plain UTF8/UTF16 and Android binary XML, including typed library
  versions. Reject duplicate identity/ZIP entries, entities, malformed chunks,
  invalid widths, nonfinite values and excessive decoding/tree expansion. Retain
  one generated manifest from the public Suwayomi extension1.4.29 as a small offline
  regression fixture; its executable APK remains outside the project. No downloaded
  source code or APK was executed or installed during local validation.
- Include allowlisted observed repair-job state, bounded history, conversion hashes,
  metadata provenance and orphan preparation failures in the existing redacted
  diagnostic download. Log worker terminal failures even without opening its queue.
  Keep private prompts, conversations, patches, auth and package bodies out of the
  export; collect no new upstream probes at download time. Use HTTPException.detail
  for useful pre-job errors. Existing log rotation/retention/privacy controls remain.
- Upgrade: preserve existing env files, both databases, signing identity, source
  profiles and the retained source-maintenance upload/backup folders. Rebuild the
  existing image, start a new orphan repair, and attach its confirmed original APK
  through the Web UI only if needed. Old blocked jobs remain evidence; validated
  candidates still wait for explicit installation. No new service, host step,
  deployment, live reset or personal account operation is performed here.
- Validation (author self-review): **586 portable tests pass**, with four native
  POSIX filesystem/durability gates skipped and one existing Windows symlink case
  excluded for WinError 1314. This includes 53 new repair regressions: actual ZIP/
  manifest bytes, private HTTP admission, evidence-only APK attachment, provenance,
  mismatched input refusal and redacted diagnostics. Synthetic engines/builds test
  the actual runner state machine; they do not execute source code or prove Linux
  conversion. The offline 1980-byte public APK manifest decodes successfully.
- Python compilation, **14 JavaScript syntax checks**, **12 controlled-DOM/VM Node
  fixtures**, YAML/template/version/auth/UI guards, 11 canonical shell syntax checks
  and **45-file** maintained bundle parity pass. The account lifetime/control and
  existing install/rollback/recovery/downstream candidate checks match 0.3.38 by
  structural review; account policy, active env, matching, reset component, Compose
  and security files remain byte-identical. No independent review is claimed.
- Required full pytest still stops at **14 Linux API collection errors** (`fcntl`,
  `resource`, UnixStreamServer). The full GNU Bash5.2/MSYS checker stops at that
  backend gate. With pytest explicitly skipped in a separate invocation, available
  sections pass through compatibility checks, then artifact validation stops for
  missing `unzip`/JDK `jar`. Separate sections8–9 pass; Docker is unavailable.
  These partial results do not establish a full Linux release-gate pass.
- Deliver `autosourcarr-v0.3.39.zip` in the existing archive and update the same
  Codex database with sanitized source, user-confirmed login success, repair evidence,
  verification and unresolved gates. Preserve earlier records/revisions and old
  evidence pins. Actual image/isolated conversion, rendered browser and live repair
  remain unverified; the user's particular source has not been repaired on the NAS
  by this task. The separate reader-builder exit18 remains unresolved.

## 0.3.38 - 2026-10-10

- Complete the requested four substantive research hours: 2026-10-10 15:15:43
  through 19:16:23 UTC (**4 h 00 m 40 s**). Record official language/runtime
  sources, reproducible defect evidence and scoped conclusions in the existing
  Codex development database. Production edits began after that research period.
  The user confirmed that only ChatGPT sign-in remained broken; preserve the
  successful 0.3.37 manual-reset confirmation.
- Correct trusted account startup policy: inherit both process-limit values
  instead of imposing RLIMIT_NPROC=1024 on the shared application user. Linux
  includes threads in that limit; the supplied inner-launch receipt records
  OSError 11 before a device code, but does not identify the exhausted host quota.
  Default the private account runtime to two Tokio workers. Keep memory/file
  limits, process ownership/draining, credential commit safeguards and isolated
  source-repair limits. Do not weaken source repair or classify arbitrary outer
  async-spawn failures as proven no-child failures.
  Reference: [Docker process-limit semantics](https://docs.docker.com/reference/cli/docker/container/run/#ulimit).
- Add a bounded optional resource frame to the private failure receipt, retaining
  legacy receipt support. Account error details and redacted diagnostic downloads
  expose only validated decimal limits/counters and explicit unknown/unlimited
  markers. Probe failure preserves the original errno and drain receipt. No raw
  paths, credentials, account output or cgroup membership are exported; limits
  outside the visible cgroup remain unknown.
- Drain previously acquired tasks if later gather startup fails; await blocking
  read-lane shutdown through cancellation; close connections if SQLite setup
  fails. Remove unused database locks and context arguments while retaining
  relocation coordination, nested transactions and savepoints. Resolve a browser
  API error only after its response body successfully decodes.
- Skip title-group pair scans when the blocked Separate-rule set is empty.
  Identical synthetic 1500-tag workloads take **184.1 ms → 4.2 ms** median in
  local Python measurements, with identical results. Workloads containing Separate
  rules show no useful improvement; these figures are not whole-app/NAS timings.
- Stage a new Android signing pair under an exclusive lock and publish it only
  after successful generation/sync. Preserve complete, ambiguous or incomplete
  existing state; never silently replace an identity. Explicitly propagate nested
  keytool/sign/verify failures, use command-local password environment values and
  read the fingerprint from one checked signer report. Keep the existing Android
  identity, RSA/JKS parameters, reader authentication and per-instance workspace.
- Repair the existing Codex database's open_findings view to include every
  nonterminal state, including partial findings. Migrate the recognized legacy
  view transactionally; preserve record IDs, history, evidence and unknown manual
  view definitions. This database remains outside the application and release ZIP.
- Align version markers/assets and repair-worker status. Extend the checker with
  the new UI fixtures and collision-free synthetic artifact files, and honor the
  user's required active-env retention. Generated tool copies follow canonical
  source; no installation, deployment, personal login or data reset is performed.
- Validation (author self-review): **533 portable regression tests pass**, one
  Linux/POSIX atomic-directory test is skipped, and one existing symlink fixture
  is excluded for this Windows host's WinError 1314 limitation. Python compilation,
  **14 JavaScript syntax checks**, **11 controlled-DOM/VM Node fixtures**, YAML
  and template checks, 11 canonical shell syntax checks, version/authentication/UI
  guards and **44-file** canonical/generated-tool parity pass. Retained active
  configuration, deployment YAML, security policies, matching code and reset
  confirmation are byte-identical to 0.3.37. Signing fixtures use synthetic tools;
  Windows substitutes flock/directory-fsync seams and does not prove Linux durability.
- Required full pytest stops at **14 collection errors** for unavailable Linux
  `fcntl`, `resource` and UnixStreamServer APIs. The full project checker, run via
  bundled GNU Bash 5.2/MSYS and pinned Python dependencies, stops at that backend
  gate. A separate invocation explicitly skipping pytest passes available sections
  through shell/compatibility checks, then stops because neither `unzip` nor JDK
  `jar` is installed. Separately executed sections 8–9 pass; Docker validation is
  skipped because Docker is absent. These partial checks are not a full gate pass.
- Complete source archive: `autosourcarr-v0.3.38.zip` in the existing archive;
  update the same development database with sanitized source and reviewed evidence,
  preserving old revisions/stale evidence. Upgrade: retain the destination's env
  files, both app databases, source profiles, manual choices and signing identity;
  rebuild the existing AutoSourcarr image, then retry Sign in with ChatGPT in the
  Web UI. If it fails, download the redacted diagnostics with resource evidence.
  Linux supervision/resource behavior, Docker/native Codex build, compiled APK/JAR,
  actual rendered browser and live TrueNAS/ChatGPT sign-in remain unverified.
  The separate supplied builder exit18 has no confirmed deployed root cause;
  signing repairs do not establish that every reader-build problem is fixed.

## 0.3.37 - 2026-10-10

- Replace the clipped native Reset all manual actions prompt with a modal whose
  complete preview scrolls independently of the required **RESET ALL MANUAL
  ACTIONS** phrase and labeled input. Enable Reset only after an exact match;
  preserve cancel/Escape, focus recovery and the reviewed-revision server check.
  No production manual choices were reset while preparing this update.
- Start trusted account operations with the fixed image-owned native Codex binary,
  avoiding the unnecessary Node wrapper under inherited resource limits. Resolve
  the pinned npm package's published Linux executable layout, validate ELF/platform,
  and require its version command to pass the existing resource launcher during
  image build. Keep account process ownership, private workspace/environment,
  inherited limits, child drain receipts and credential commit/rollback checks.
  Source repair still requires its isolated execution path and fails closed.
- Preserve a local account launch failure's stage/OS error through the supervisor's
  private receipt pipe. Expose only bounded numeric/enum fields in both account
  screens and redacted support logs; export the validated image Codex version.
  Raw account output, argv, private paths and credentials remain private. The
  supplied 0.3.36 exit-70 failure occurred before a device code; its deployed cause
  remains unconfirmed. This update removes a potential wrapper startup failure and
  makes a remaining local failure identifiable; it does not certify live coupling.
  Official references: [device sign-in](https://learn.chatgpt.com/docs/auth) and
  [pinned Codex launcher layout](https://github.com/openai/codex/blob/rust-v0.157.0/codex-cli/bin/codex.js).
- Fix UI diagnostics reporting a hardcoded 0.3.26 with a 0.3.36 backend: report the
  loaded app.js asset version and advance all current release assets together.
- Remove five unused UI helpers, their dead preload flag, six unused private
  backend methods and sixteen unused imports. Remove the obsolete singleton-owner
  matching shortcut and replace its stale checker guard with the active owner-review
  rule. Consolidate exact candidate SQL and six source-view snapshot copies;
  preserve raw/canonical candidate ordering, limits, whitespace behavior, active
  exclusions and independent matrix snapshots. Exercise the current readable-color
  routine instead of testing an unreachable legacy accent helper.
- Exact duplicate files are maintained Docker-context copies, aligned VERSION
  markers and package initializers; retain these required files. Keep release/test
  output outside the app and all ZIPs in the existing archive. Update the same
  development knowledge database, preserving record IDs and history.
- Validation (author self-review): **437 focused tests pass**, with one existing
  symlink test excluded for this host's established WinError 1314 limitation.
  The two new title/alias parity cases also passed against the previous
  implementation. **14 JavaScript syntax checks**, **nine project-gate Node
  fixtures**, the current-accent/workspace fixture, Python compilation, five
  read-only portable checker sections, 42-file maintained-bundle parity and
  AST/configuration change-scope checks pass. All 43 duplicate groups have a
  required build/version/package role; the audited unused candidates are gone.
- Required full pytest stops at **14 collection errors** for unavailable Linux
  `fcntl`, `resource` and UnixStreamServer APIs. `bash scripts/check-project.sh`
  cannot run because Bash is absent. Linux account process/receipt integration,
  Docker image/native-tool build, APK/JAR compilation, live TrueNAS/upstream,
  actual rendered browser and personal sign-in remain unverified. The supplied
  exit-70 cause remains open; this is a locally tested source candidate.

## 0.3.36 - 2026-10-10

- Add **Status → Download diagnostic logs**, also available in Errors & diagnostics.
  One text file combines retained app errors/warnings, integrated source-engine and
  supervised helper output, compiler failures, whitelisted account error metadata,
  browser errors, versions, cached health and task/import/service state. Collection
  stays local, requires admin access and does not probe/retry upstream services.
- Scrub configured credentials and common sensitive formats before memory/disk
  retention and during export; mask endpoint hosts, query values, private paths,
  account identifiers and structured search/title fields. Exclude raw settings,
  databases, request bodies, cookies, account/signing files and private-key blocks.
  Test split process chunks, oversized key headers, CLI signing/password arguments,
  disk failure, hardlinks, Unicode and a real local child process. Output is plain
  text and the authenticated snapshot uses no-store caching.
- Keep three rotating 512 KiB files under the instance's existing data directory,
  with bounded pending/memory tails and drained offloaded writes at shutdown. Logs
  remain downloadable after restart; disk failure preserves the in-memory tail.
  Retain fatal startup/cleanup errors and drain the log writer even when another
  resource cleanup fails. Export the actual framework/HTTP package versions.
  Bound the email scan to avoid a quadratic dotted-line scan: the same 65,536-byte
  synthetic line changes **3.461 → 0.036 seconds** with unchanged output in a single
  component stress sample. A regression budget protects collection/download.
  Report omissions/storage errors; avoid unchanged helper-health polling noise.
  Host/proxy/other-container logs and output from before collection are unavailable.
  Arbitrary third-party text is scrubbed for known values/common formats; complete
  anonymity is not established. No extra Compose fields, mounts or services.
- Resolve tracked Reject cleanup starvation by using manual capacity for verified
  library cleanup, then releasing it before paced automatic alternative discovery.
  Keep durable rejection, verified-add ownership and supersession/cancellation gates.
- Repair duplicate shortlist recall after manual anchor separations with at most
  four alternative anchors, bounded to O(5n) candidate work per bucket. Preserve
  not-duplicate decisions, the 0.94 floor and all-original-member direct proof.
- Add matching expression indexes for preferred, active match and active variant
  title lookups. A same-database synthetic 50,000-entry/50,000-match/50,000-variant
  workload (five samples, actual service methods) keeps result IDs identical:
  exact candidate **16.902 → 0.656 ms**, exact manga **16.420 → 0.638 ms**;
  missing candidate **92.416 → 61.048 ms**, missing manga **109.247 → 62.124 ms**.
  Used SQLite pages increase **25,272,320 → 29,569,024 bytes**; a 1,000-row insert
  batch in each table increases **13.131 → 16.085 ms**. These are component tests;
  wildcard fallback remains, and no NAS/end-to-end performance claim is made.
- Reject malformed UTF-8 backup URLs with a clear decode error instead of treating
  different corrupt bytes as the same replacement-normalized binding. Valid
  multilingual identities and the existing display-text replacement policy remain.
- Add **Sources → Repositories → Apply saved repositories** using the existing
  owned source-browser maintenance path. Restart only the integrated source process;
  preserve sign-ins, browser data, preferences, extensions and library databases.
  Remove obsolete helper/Compose setup copy and point translation failures to
  Settings → Services. External-mode restart remains with the external engine.
- Make the existing Node UI-test helper pass scripts on stdin with an explicit
  project working directory; this avoids Windows command-line length failures.
  Return platform-independent relative paths from browser-data maintenance.
- Reconcile the historical Starlette advisory against the already-current app and
  repair-worker pins (FastAPI 0.142.0 / Starlette 1.6.0). The four cited affected
  ranges no longer include the source pins; this does not verify deployed packages
  or replace the remaining image/runtime, signing, reader DTO, reset-policy and
  other open findings. Keep the same archive knowledge database and stable history.
- Validation: **399 focused regression tests pass** with pinned development
  dependencies, including privacy, real child output/filesystem races, persistence,
  cancellation, migrations, imports, matching and controlled UI fixtures. One
  existing symlink test is explicitly excluded after WinError 1314 established
  that this host lacks symlink privilege. **13 JavaScript syntax checks** and
  **eight project-gate Node fixtures**, Python compilation, maintained bundle
  parity, version/assets mapping and source-change scope pass. Reader auth,
  security policy, Compose and retained configuration bytes are unchanged.
- Required gates remain unavailable: full pytest stops at **14 collection errors**
  for Linux-only fcntl/resource/UnixStreamServer; Bash is not installed. No WSL,
  Docker, Java/Kotlin compiler or installed Playwright Chromium was available.
  In-app browser file navigation was blocked by its URL policy; no bypass was
  attempted. UI evidence is executed controlled DOM/download behavior. Checks
  are author self-review; rendered-browser, real toolchain/host/provider evidence
  remains unavailable. Historical failures and remaining findings stay recorded.
- Upgrade: source candidate pending the complete Linux suite/Bash gate. Rebuild
  the existing image only after those checks pass, refresh the browser and confirm
  0.3.36. Preserve all retained configuration/data and signing/account identity.
  Reader mapping is 1.4.3036. No deployment, real provider login, source-site check,
  host namespace validation or compiled APK/JAR is claimed.
- Technical basis: [OWASP logging data exclusions and bounded verification](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html),
  [SQLite expression indexes](https://www.sqlite.org/expridx.html),
  [Protobuf string encoding](https://protobuf.dev/programming-guides/proto3/#scalar),
  [FastAPI 0.142.0 dependency contract](https://github.com/fastapi/fastapi/blob/0.142.0/pyproject.toml).

## 0.3.35 - 2026-10-09

- Review the supplied 0.3.27–0.3.34 history against the current folder. All eight
  archive hashes match the existing development knowledge artifact; preserve its
  records, historical source revisions and custom archive/research tables rather
  than creating another database. Reconcile only findings actually reviewed and
  retain unreviewed stale evidence. The history is evidence, not new instructions.
- Keep the application in the workspace's autosourcarr/ folder; Codex context and
  separate development/test outputs stay at the workspace root. Use the existing
  sibling autosourcarr data/ archive for this and future release ZIPs, and update
  its existing knowledge database in place. Exclude that database and workspace
  notes from application ZIPs. Application paths and runtime behavior are unchanged.
- Remove the remaining alias-compaction write from persistent Browse cache reads.
  Resolve/deduplicate IDs for the response without acquiring SQLite's writer lock
  or overwriting a feed refreshed after the original SELECT. Real SQLite tests
  reproduce writer contention before the fix and verify both read availability
  and preservation of the newer saved feed. Manual merges and chapter provenance
  retain their existing behavior.
- Extend the existing lifespan teardown over startup, including failures before
  HTTP readiness. Handle partially registered startup tasks without unbound names.
  Retrieve/log owned startup exceptions while preserving await/cancel semantics;
  show the exception type when a failed task has an empty message. Cover early
  failures/cancellation, late recovery failure, normal shutdown and context failure
  with controlled resources. Close the hermetic test session's main-thread SQLite
  handles before Windows temporary-directory cleanup.
- Bound retained tag-suggestion candidates to the requested best results. Keep
  the complete 12-neighbour scan, rounded-score ordering, ties, namespaces, aliases
  and manual decisions. A three-sample synthetic 2,000-tag comparison produces
  identical output: median peak traced allocation **7,074,004 → 1,328,080 bytes**
  (about 81% lower); median traced time **1.337 → 1.323 seconds**, essentially
  unchanged. Full-vocabulary reads/scoring remain a tracked optimization follow-up.
- Propagate Docker's native exit status in the Windows reader-build wrapper and
  suppress its success messages on failure. Native PowerShell tests substitute a
  local docker.cmd returning 0/17; no Docker build or third-party call occurs.
- Speed up the Codex source index by splitting Python lines once per file; add
  `stats --summary` for bounded context retrieval. Five-sample same-input medians:
  service.py **1.403 → 0.505 seconds**, main.py **0.065 → 0.037 seconds**, with
  identical symbol/route/import output. These are local indexing measurements,
  not application or deployment benchmarks. The knowledge SQLite file remains
  outside the image build context and has no app service, route or dependency.
- Validation: fresh affected self-review passes **190 tests**, **13 JavaScript
  syntax checks** and **eight project-gate Node fixtures**. Canonical/bundled tool
  synchronization, version/assets mapping, source-change scope, knowledge integrity,
  foreign keys/search consistency and archive contents are checked separately.
  Compose and host isolation policy bytes are unchanged.
- Failed/unavailable gates: the required full suite cannot collect Linux-only
  fcntl/resource/UnixStreamServer modules on native Windows. An initial bounded
  diagnostic run recorded **315 passed, 16 failed and 14 collection errors**,
  stopping at its 30-failure limit; failures included missing Bash/Linux directory
  primitives. That run predates final integration and is not a passed candidate
  gate. `bash scripts/check-project.sh` fails because Bash is unavailable; WSL
  and Docker are not installed. Async tests stall inside this host sandbox and
  pass outside it. No independent review, rendered-browser run, real provider
  sign-in, TrueNAS deployment, repair namespace check or APK/JAR build is claimed.
- Upgrade: this is a **source candidate pending the complete Linux gates**. Run
  the pinned backend suite and Bash project checker on Linux before deployment.
  Then overlay the complete archive, rebuild the existing image and confirm
  0.3.35. Keep Compose, all retained configuration/data, accounts, source profiles,
  manual decisions, presets and signing keys. The supplied config/autosourcarr.env
  is preserved; no source-host.env or live app databases were supplied, and no
  substitutes were created. The reported no-Compose source-execution host blocker
  and actual successful live sign-in remain open/unverified. No deployment occurred.
- Technical basis: [Python task exceptions and cancellation](https://docs.python.org/3.12/library/asyncio-task.html),
  [SQLite WAL reader/writer behavior](https://www.sqlite.org/wal.html),
  [stable bounded selection](https://docs.python.org/3.12/library/heapq.html#heapq.nsmallest),
  [PowerShell native exit status](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_automatic_variables#lastexitcode).

## 0.3.34 - 2026-10-07

- Correct the misleading immediate sign-in failure message. Previously every
  unrecognized CLI exit suggested disabled device-code login. Now report observed
  request/authorization phase, exit/HTTP status and fixed safe categories for
  denial, endpoint, rate/server errors, DNS/TLS/network, invalid responses, tool
  startup, resources and configuration. Keep unknown failures explicitly unknown.
  HTTP 401/403 or a missing endpoint does not establish an account setting problem.
- Open copyable Account error details in both setup and Source repairs. Whitelist
  only five diagnostic fields; never export raw CLI/log text, tokens or paths.
  Clear old details at a new action, lost connection or provider change. Preserve
  popup/foreground races, existing code visibility and operation-scoped controls.
- Classify bounded regular Codex login logs from this operation's private home,
  using no-follow directory/file descriptors and rejecting FIFOs/symlinks. Drain
  the owned readonly log thread before cleanup/unlock on cancellation. Account
  commands, limits, credentials/staging, cleanup receipts and source gates stay
  intact. All Compose/example files remain byte-for-byte unchanged.
- Upgrade: overlay the complete archive, rebuild/redeploy your existing image,
  refresh and confirm 0.3.34. Leave Compose and retained data/configuration as-is.
  If no code appears, use Copy error details; that observed report is needed to
  diagnose the running host. No deployment was performed here.
- Validation: the fresh complete project gate passed with **2,131 backend tests
  passed, five environment-dependent skips and two upstream warnings**. All 35
  new diagnostic cases pass. An independent read-only review passes 108 affected
  cases and separately the final 35 new cases (overlapping counts), without a
  material issue. Nine real Node/Codex loopback-only cases cover code capture,
  HTTP failures, malformed responses and TLS/network failure with owned cleanup.
  The first full gate exposed an older reader UI harness missing the shared
  login script; its page dependency was corrected and the full gate rerun passed.
  All seven Compose/example files, original source inventory and file modes are
  preserved. No live browser, provider account or deployment was tested.
- Limits: the exact deployed failure and successful user sign-in remain unverified.
  Real Node24/Codex0.159.2 through the production supervisor returns a mock code
  and safe HTTP400/401/403/404/429/503, malformed-response and TLS/network failures
  with owned loopback-only transport. No real account or external provider request.
  These versions differ from image Node22/Codex0.157.0. Sampled /proc accounting
  uses a fixture because this executor exposes different PID namespace views;
  supervision, inherited limits, code parsing and cleanup receipts are real.
  Native pinned image/build/boot, TrueNAS, real provider authorization/billing,
  APK/JAR builds and no-Compose source repair execution remain unverified/open.

## 0.3.33 - 2026-10-07

- Remove the unnecessary namespace prerequisite for trusted ChatGPT account
  setup. Fixed bundled Codex login/status/logout run inside the current app with
  fresh private working/home directories and a clean allowlisted environment.
  Copy only bounded validated ChatGPT auth.json for status/sign-out; never load
  saved Codex configuration, hooks, MCP settings or source instructions.
- Preserve device-code popups, complete-code parsing, deadlines, cancellation,
  staging, saved-account protection and locks. Supervise actual account children
  using no-new-privileges, inherited resource limits, subreaping, parent-death
  signals, a ready handshake and a private cleanup receipt. Keep leader identity
  until owned group signals finish. A missing receipt keeps credentials/lock
  retained; pending sign-out deletion survives cleanup retry. Safely reject
  recursive/malformed credentials. Do not run arbitrary commands in this path.
- Correct UI/provider messages: account setup and all API checks work while
  source execution is blocked. Put optional host permissions inside a collapsed
  repair-only section. Keep the full source isolation gate for every provider.
- Upgrade: overlay the complete archive, rebuild/redeploy the existing image,
  refresh and confirm 0.3.33. Keep existing Compose unchanged. Sign in directly
  from setup or Source repairs; Check repair tools need not succeed first.
  Preserve ports/mounts/env/state/presets/source profiles/accounts/signing keys.
- Remaining requirement: no-Compose source repair execution is still unresolved
  on the reported host. QEMU's official TCG security model cannot supply that
  boundary alone; Landlock requires host support and rootless gVisor requires
  user namespaces. A confined internal VM needs implementation and actual
  runtime/toolchain/negative isolation verification before shipping. No unsafe
  fallback, helper service, Docker socket or host permission grant is introduced.
- Validation: the fresh complete project gate passed with **2,096 backend
  tests passed, five environment-dependent skips and two upstream warnings**.
  All 18 new cases and 123 affected root cases pass. An independent read-only
  review passes 144 account/UI/provider cases without a remaining material issue.
  Real supervised children confirm inherited limits/no-new-privileges, ready and
  cleanup receipts, adopted-child draining, parent-death cleanup, repeated spawn
  cancellation and safe credentials/commit/retry. All six Compose/example files
  remain byte-for-byte unchanged. The real installed Codex0.159.2 receives a
  device code and a bounded stalled-code error through production InternalControl
  and owned loopback-only CONNECT/TLS fixtures, without probing namespaces or
  using any real account. That native version differs from image pin0.157.0.
- Limits: actual TrueNAS, real provider sign-in/billing, image build/boot, native
  pinned Codex0.157.0 and native APK/JAR builds remain unverified. The local
  account fixtures use actual supervision/limits and replace only sampled /proc
  accounting because this executor mounts /proc in a different PID namespace.
  Controlled code/API tests do not prove source isolation or resolve the no-Compose
  source-repair requirement on the deployed host. No live deployment occurred.

## 0.3.32 - 2026-10-06

- Research all seven supported AI connections against official host/provider
  requirements. Correct the user's exact Bubblewrap “No permissions” classifier
  miss: show namespace-denied, explain its effect on every provider, open actionable
  diagnostics and identify the reported Docker default AppArmor selection. Report
  optional host AppArmor restriction values without treating missing values as off.
- Make the reviewed host security files available through an authenticated Web UI
  download, including the license and one shared Compose example/load command.
  The image bundles these fixed files automatically. Applying host-enforced policy
  remains a host action; the report cannot identify the actual denying layer from
  Seccomp=2 or a positive namespace limit. Keep ABI 3 socket compatibility and the
  mandatory full repair sandbox test; no helper services or privilege fallback.
- Replace text-only API readiness with an inert tool call and random-result
  continuation for Responses, Messages and Chat Completions. Make at most two
  bounded model requests; no source files are accessed. Retain provider signatures
  and reasoning unchanged. Require completed, supported replies and the real token;
  reject plain answers, unknown tools, malformed JSON and partial conversations.
  Retain the response-owned stream and close each response so a trickling body
  cannot outlive the shared connection-check deadline. API checks remain separate
  from sandbox readiness and real source compilation/functional validation.
- Enforce OpenRouter require_parameters for tool requests, preserve safe provider
  error categories including HTTP 402 billing failures, support optional Claude
  multi-workspace ID/header, and remove the misleading implicit Linux Ollama
  hostname from new setup presets. Link official setup guidance for each provider.
  Existing saved server addresses, API keys and ChatGPT account state are retained.
- Upgrade: overlay the complete archive, rebuild/redeploy the current image,
  refresh and confirm 0.3.32. Keep actual Compose, ports, mounts, env files,
  databases, source profiles, presets, credentials and signing keys. Download the
  host policies from Source repairs if needed, load/select them on the host, then
  Save/Redeploy and recheck. Each AutoSourcarr instance keeps its own settings.
- Validation: the fresh complete project gate passed: **2,078 backend tests
  passed, 5 environment-dependent skips and 2 upstream deprecation warnings**.
  All 60 new regressions pass, including every API provider/protocol through the
  production relay over controlled HTTP/TCP fixtures, refusal of unsupported tools,
  the shared deadline, real Gemini/Claude continuation structures, authenticated
  policy export and blocked sandbox paths for all seven providers. Independent
  read-only reviews passed 97 provider cases (one legacy Unix-socket skip) and
  77 host/export cases. Canonical policy bytes remain unchanged from 0.3.31.
  Controlled fixtures do not establish live TrueNAS policy loading, actual repair
  namespaces, real provider authentication/billing or native APK/JAR build success.

## 0.3.31 - 2026-10-06

- Enforce the permanent requirement that every feature is internal wherever
  technically possible. The image now owns its default non-root UID/GID 568.
  Integrated startup enables no-new-privileges before Python starts, inherited
  by threads and managed tools; failure prevents application startup. Existing
  explicit Compose user/protection settings remain compatible.
- Consolidate the host security options into one shared block in all integrated
  deployment templates. Each AutoSourcarr service uses one reference. Main
  Suwayomi services, ports, mounts, environments and retained data are unchanged.
  AppArmor selection remains commented until the host loads the supplied profile.
- Clarify that the user's reported namespace denial blocks Codex before sign-in.
  The app cannot grant outer seccomp/AppArmor or host namespace permissions.
  Provide the minimal shared configuration and host profile-loading step in
  TRUENAS.md. Missing internal protection now points to the normal image startup.
- Upgrade: overlay the full archive and rebuild/redeploy using the normal image
  start command; refresh and confirm 0.3.31. Keep actual ports/mounts, env files,
  databases, source profiles, presets, credentials and signing keys. Existing
  Compose settings remain compatible; remove redundant identity/protection keys
  only when adopting the new image. The host policies must still be applied for
  the reported namespace blocker. No live deployment is performed here.
- Validation: the fresh complete project gate passed with **2,018 backend tests
  passed, five environment-dependent skips and two upstream deprecation warnings**.
  All nine new regressions pass within 57 affected cases. An independent read-only
  review passed 69 targeted cases and found no material regression; both reviews
  establish exact expanded Compose preservation apart from the two internalized
  defaults. Real setpriv fixtures verify exec/PID ownership and descendant
  inheritance; a failing protection utility prevents application startup.
  The local kernel already has NoNewPrivs=1: a fresh 0→1 transition, Docker image
  boot, TrueNAS/AppArmor load, actual repair namespaces, real OpenAI login and
  native APK/JAR builds remain unverified. No live deployment is claimed.

## 0.3.30 - 2026-10-06

- Retain Source repairs sign-in errors when the OpenAI popup hides the app before
  its POST completes. Keep mutation ownership separate from read visibility;
  later idle reads and hide/return cycles cannot silently restore Ready. An
  uncertain failure still cancels only its own operation UUID. Explicit refresh
  or a new account action clears the retained error.
- Bound the initial code request to 45 seconds in both worker modes. A stalled
  native request now reports that OpenAI did not return a code; receiving a code
  preserves the normal authorization wait. Reset code readiness for each attempt
  and drain owned command/output/watchdog cleanup before releasing the account
  lock. Failed/cancelled attempts retain the previous saved account.
- Upgrade: overlay the complete source archive, rebuild/redeploy the existing
  image, refresh the browser and confirm 0.3.30 in Status. Existing config, runtime
  databases, source profiles, saved credentials, editable matching presets,
  signing keys and builder locks remain retained. No Compose change is required.
- Validation: the fresh complete project gate passed with **2,009 backend tests
  passed, five environment-dependent skips and two upstream deprecation warnings**.
  All 17 new sign-in regressions and 76 affected login cases pass. Coverage includes
  the production browser API with controlled DOM/fetch, authenticated worker/app
  routes, both owned child modes, same-UUID cancellation, successive attempts and
  held cleanup. An independent read-only UI review found no remaining blocker in
  the corrected scope. A native Codex 0.159.2 check through an owned loopback
  CONNECT/TLS mock passes the unchanged strict code parser and stalled-request
  deadline; no real OpenAI traffic or credentials were used. This native version
  differs from the image pin 0.157.0 and bypasses namespace/tool preflight. Real
  OpenAI sign-in, rendered browser, Docker/TrueNAS deployment and native APK/JAR
  builds remain unverified; no live deployment is claimed.

## 0.3.29 - 2026-10-06

- Add persistent custom matching presets with create, rename, edit, copy and delete
  in Sources → Automatic matching rules. Save draft as a preset starts from either
  direction. Built-ins offer Save as new. Preset edits do not change applied rules;
  choose a preset and Save each direction independently. Store validated templates
  in the retained catalogue, with transactional revision checks and restart/reset
  preservation. Keep the 94% title floor, identity/edition and manual safeguards.
- Fix device-code loss after bounded output rotates and reject incomplete code
  prefixes from split pipe reads. Retain a captured complete code only for its
  active owned login. Expose remaining expiry time so browser clock skew cannot
  suppress it. Existing credential staging, cancellation and cleanup locks remain.
- Refresh both account screens immediately on returning from the OpenAI tab.
  Source account polling no longer waits for source inventory; hidden/obsolete
  reads are cancelled and fenced. Wizard reads have independent response ownership;
  an accepted action’s delayed GET cannot hold Cancel or revive cancelled codes.
- Upgrade: overlay the full source archive, rebuild/redeploy the existing image
  and refresh the browser; retain config, runtime databases, source profiles,
  saved credentials, reader signing keys and builder locks. No Compose change is
  required. Existing reader artifacts remain compatible.
- Validation: the fresh complete project gate passed with **1,992 backend tests
  passed, five environment-dependent skips and two upstream deprecation warnings**.
  All 41 new release cases, existing account/matching/reader suites, UI handlers,
  syntax, bundle parity, versions, Compose/env and packaging guards pass. New
  coverage uses authenticated worker/app routes, real owned child output/drainage,
  actual browser API with controlled DOM/fetch, and real SQLite restart/reset,
  rollback and concurrent-write checks. Read-only independent reviewers reproduced
  the faults and found no remaining material blocker in the corrected scope.
  Real OpenAI account sign-in, rendered browser, Docker/TrueNAS deployment and
  native APK/JAR builds remain unverified; no live deployment is claimed.

## 0.3.28 - 2026-10-06

- Add five source/group matching presets: high title similarity, titles + creators,
  titles + summaries, titles + informative tags, and strict metadata. Apply to one
  direction’s draft, review and Save independently with existing revision checks.
  Missing required evidence blocks matching. Inherit/Disabled stay explicit;
  manual decisions, identity/edition checks and the 94% title floor are preserved.
- Open the official OpenAI device sign-in page synchronously from an explicit
  login click in Setup and Source repairs. Show progress and a fallback link before
  the code arrives, then code/expiry and completion or actionable errors. Prevent
  delayed polls from hiding a new code or reviving a cancelled one. Scope Cancel
  to the displayed operation; background polling never opens account pages.
- Stage device login in an empty owned credential home because pinned Codex
  0.157.0 clears/revokes existing credentials before sign-in. Preserve the saved
  account on failures/cancellation, validate bounded regular ChatGPT credentials
  and atomically save only after confirmed process cleanup. Retain the account
  lock and discard pending promotion when cleanup is uncertain. Keep a single
  deadline over integrated command execution and output drainage. Public errors
  never expose raw subprocess output or credentials.
- Upgrade: overlay the complete source archive and rebuild/redeploy the existing
  image, retaining config, runtime databases, source profiles, saved credentials,
  signing keys and builder locks. Existing reader artifacts remain compatible.
- Validation: the fresh complete project gate passed with **1,951 backend tests
  passed, five environment-dependent skips and two upstream deprecation warnings**.
  All 50 new release cases, existing Setup/reader/ownership suites, matching draft
  handlers, syntax, bundle parity, versions, Compose/env and packaging guards pass.
  Read-only independent UI and backend reviews reproduced the earlier faults and
  verified the corrected flows. Controlled DOM/HTTP and owned executable fixtures
  cover both account UIs and both account binding modes.
  Real OpenAI account sign-in, rendered browser, Docker/TrueNAS deployment and
  native APK/JAR builds remain unverified; no live deployment is claimed.

## 0.3.27 - 2026-10-06

- Share one side-effect-free reader address/key contract between the builder,
  wizard draft checks and running-instance Status. Show actionable build advice
  in Reader extensions and Review, with a targeted reader setup entry from Status.
  Check retained masked keys without returning them; draft checks never save.
  Finish remains available for internal/development configurations. Invalid builds
  fail before queuing or starting a child process; save/restart changed settings
  before building. Preserve build cancellation and existing downloads.
- Retry unavailable integrated loopback repair readiness after 60 seconds with a
  five-second deadline. Persist this short gate without consuming the daily
  source-check window. Reserve the full daily window before source traffic and
  keep weekly protection after uncertain repair submissions. Remote compatibility
  helpers retain their original ordering. Ready repairs still await explicit review.
- Offload reader image-route SQLite reads and chapter-index writes with owned
  cancellation drainage. Serialize mapping publication with source decisions and
  recheck current native ownership after upstream headers, before streaming.
  Close owned upstream responses on failed publication or cancellation. Retain
  cached-map served-candidate audits, source pacing and inactive historical access
  where the native record still belongs to that work.
- Identical local synthetic reader workload: under a 150ms storage hold, maximum
  5ms loop-tick intervals changed from 150.618→6.037ms for cached image admission
  and 179.684→5.566ms for index writes. Storage-bound request times remain roughly
  150/180ms. Warm image median changed from 0.091→0.357ms with the additional
  ownership/closure work. Cached-map audit writes were already offloaded in 0.3.26;
  no improvement is claimed for them. These measurements do not establish live NAS
  or whole-page latency.
- Classify typed HTTPX PoolTimeout as local capacity and preserve website health;
  Connect/Read/Write timeouts still count as upstream timeouts. Keep unresolved
  chapter mapping retryable after pool saturation. Preserve raw exception context.
  Skip cache publication only for all-empty locally deferred feeds; healthy empty,
  website-failure/challenge and nonempty partial feeds keep their existing behavior.
- Upgrade: overlay this complete project and rebuild/redeploy the existing image.
  Preserve retained config, both runtime databases, source profiles, reader signing
  keys and builder selection locks. Existing reader artifacts remain compatible.
  No new services, mounts or data reset. The same development knowledge database
  is updated as a separate artifact.
- Deferred: JAR-only build selection and Suwayomi engine adoption still need
  separate implementation/acceptance. Retain v2.3.2243. Further suggestion caching,
  top-k and title expression indexes remain measurement-led follow-up work.
- Validation: the fresh complete project gate passed with **1,901 backend tests
  passed, five environment-dependent skips and two upstream deprecation warnings**
  under the pinned toolchain. All 28 new reader-preflight UI scenarios, existing
  setup/ownership suites, bundle parity, Compose/env, version, shell and synthetic
  reader validator/staging checks pass. Independent final integration checks pass
  216 cases with one environment skip. Docker/TrueNAS deployment, rendered-browser
  inspection, live source traffic and actual native APK/JAR compilation remain
  unverified; no deployment or newly compiled reader artifact is claimed.

## 0.3.26 - 2026-10-05

- Keep explicit Unmatch duplicates decisions through automatic sweeps, restart and
  normal catalogue rebuilds. Persist exact source-record exclusions and Different
  works reviews; reconcile manual groups while keeping internal edition/Redo splits
  independent. Reset groups/removing a group member no longer recreates that group
  during startup capture. Reset manual matches releases exact affirmative bindings
  without dropping Different works/exclusion decisions or internal split flags.
  Read retained primary/variant rows by named fields.
- Commit merge source bindings, variants, aliases, history and manual decisions in
  one catalogue transaction. Publish aliases/cache invalidation after commit.
  Journal chapter-origin reconciliation for recovery after interrupted merges/splits,
  preserving later source ownership and served/downloaded chapter history. Recompute
  colour consensus when relocating native records. Readers check current native
  source ownership before serving cached pages or publishing an in-flight result.
  Migrations own each file's DDL/backfill transaction, so failed startup and retry
  cannot skip a legacy conversion merely because its new column already exists.
- Recover interrupted import Match/Retry actions on every restart with revision
  checks, independently of one-time migrations. Preserve newer Skip/other decisions
  and runtime-owned work. A missing selected target cannot stop later recovery;
  pause it with instructions to Retry/Match. Fence every foreground main-library
  retry against newer import decisions; observe uncertain issued writes without
  another add or removal. Separate local source admission from execution timeouts;
  local capacity deferrals do not penalize website health. Cancelled/failed metadata
  jobs report their actual aggregate outcome after owned work settles.
- Retain user Search/Chapters choices when a source disappears temporarily. Honour
  saved solver disable intent and runtime readiness separately. Preserve source
  pacing, browser-idle admission, reader authentication and matching safety floors.
- Guard editor reads/saves, reconciliation and rollbacks by opening, source and
  mutation ownership, including canonical entry redirects. Obsolete repair evidence
  cannot replace the selected job or reopen a closed disclosure. Attach diagnostic
  health rejection handling immediately while history loads. Manual actions reads
  invalidate at write start/completion; an old snapshot cannot restore a removed
  group on screen. Failed writes reconcile once with a visible refresh/retry state.
- Accept UTF-8 backup filenames through an ASCII header contract. Keep the request
  deadline through response decoding. Persist a stable import receipt so a lost
  acknowledgement can be checked with a bounded GET; no automatic POST replay.
- Move translation SQLite cache work off the HTTP loop with cancellation drainage
  and synchronized memory-cache access. Add a portable normalized preferred-title
  index with dirty SQL triggers/CAS backfill. Compute only selected policy checks.
  Expand tag aliases only for displayed rows, reserve a suggestion lane and paint
  manual tag decisions independently of slower suggestions.
- Same-workload synthetic local medians: exact lookup (10,000 entries) 6.565→0.044ms;
  title-only policy (20×20 payloads) 32.446→0.421ms; ten-row tag page (400 equivalent
  tags) 41.341→2.465ms. Tag peak allocation 30.85→0.96MB with the same aliases.
  These component measurements do not establish live NAS or whole-page latency.
- Update app/repair-worker to FastAPI 0.142.0 / Starlette 1.6.0 and pin the development test toolchain.
  Image-header path checks use raw ASGI scope paths; retain malformed-Host rejection
  and existing reader/admin/setup authentication boundaries.
- Publish reader APK/JAR as one immutable generation after both pass validation.
  Failure before the shared pointer commit retains previous downloads; a committed
  publication with a later durability warning is reported as committed. Fresh
  workspaces use Keiyoushi e40e1b78f46baf5ef9dc68cb980f82c66766a211; retained
  selection locks stay authoritative and updates remain explicit. Verify exact
  origin/SHA before compilation; include the new publisher/default pin in bundles.
- Add inherited repair process limits (8GiB virtual address space, 1,024 open files,
  512MiB per file, existing-or-lower per-UID task ceiling) and controller-owned
  sampled budgets (4GiB resident memory, 256 processes/threads, 8GiB workspace,
  200,000 files, 512MiB free space). Stop/drain owned work on overrun; namespace and
  egress isolation still fail closed. Show diagnostics in Check repair tools.
  Aggregate budgets can briefly overshoot; they are not hard cgroup reservations.
- Add **Discard validated repair** for an idle ready candidate. Retain its artifact,
  conversation and test evidence; a discarded candidate no longer defers daily
  maintenance and cannot be installed. Serialize discard/install admission, retain
  running cancellation and keep terminal review history across worker restarts.
- Resolve only the saved-config directory during bootstrap, then load managed or
  recovered settings before final validation. Valid saved settings can override an
  invalid stale process/dotenv value; invalid final/unmanaged settings still fail.
- Upgrade: overlay this complete project and rebuild/redeploy the existing image.
  Keep retained config, both runtime databases, source profiles, reader signing keys
  and builder selection locks. No Compose edits, helper services, new mounts or data
  reset. The development knowledge database is a separate updated artifact.
- Deferred: retain Suwayomi v2.3.2243 pending real image/profile/API/Java-agent and
  WebView compatibility checks. Further suggestion caching/top-k and exact SQL
  expression indexes need candidate-heavy workload evidence; the demonstrated
  normalized-title and quadratic alias costs are addressed here. Shared wizard/build
  reader preflight and JAR-only selection remain open: the combined builder requires
  a valid reader token and HTTPS public URL, while setup permits internal/development
  configurations. Maintenance daily reservation before local helper readiness and
  cached-page audit write costs remain separate follow-up work.
- Validation: complete project gate passed with **1,805 backend tests passed, five
  environment-dependent skips and two upstream deprecation warnings** under the
  pinned test toolchain. The shipped response-ownership suite passes 59 scenarios;
  existing UI/setup, Compose/env, bundle parity, version, shell and synthetic reader
  publication/validator checks also pass. New resource checks execute actual Java17
  and Node24 workloads under the inherited limits. Docker is unavailable; actual
  image JVM25/Node22/Codex execution, TrueNAS deployment, rendered-browser behavior,
  live source traffic and native APK/JAR compilation remain unverified. No deployment
  or new compiled reader artifact is claimed.

## 0.3.25 - 2026-10-05

- Include retained pre-snapshot automatic merges in Duplicates → Automatically
  combined. Strict automatic markers identify reason-only records; retain original
  survivor/absorbed IDs, follow later redirects, omit manual/unknown origin and
  deduplicate newer snapshot audits. Missing original details are clearly unavailable.
- Remember the history view, submitted search/page and matching-rule target/open
  state on refresh. Stop hidden duplicate candidate polling, retries and hydration
  while inspecting history. Refresh uses the displayed view. Preserve lazy loading,
  safe normalization and user identity boundaries; obsolete GET/PUT responses cannot
  replace a newly selected owner's rule controls.
- Use reserved catalogue capacity for local history, detail and rule reads. Add a
  compact SQLite history index to avoid repeatedly reading large saved snapshots.
  A synthetic 20,000 modern + 34,000 legacy-row workload retained identical results
  across eight query cases on SQLite 3.45.1/3.53.1. Newest-page time changed from
  roughly 248–295ms to 35–44ms. This is not a production Browse latency guarantee.
- Recheck current native/gallery title/tag/animation and media proof at automatic
  attachment commit, including the stricter variant threshold. Public tag review
  shares the source decision writer gate. Keep known identity refresh and explicit
  manual actions available; no network calls are added inside this gate.
- Automatic imports respect retained source-record Different works decisions even
  before an independent owner is rebuilt. Delayed binding follows the current
  canonical entry and applies its current source rules. Preserve explicit manual
  binding. Restore per-source groups/toggles and both matching-rule directions by
  stable source ID first; ambiguous replacement metadata requires review.
- Prevent delayed catalogue readers from publishing an older source/group cache
  over a newer saved decision. Reserve cache publication by reload generation,
  without making local readers wait behind automatic writers.
- Update the existing development knowledge database with this hour's research,
  original baseline reproductions, final checks, source revisions and unresolved
  proposals. The source archive contains functional tests and this one updating
  changelog, with no added research reports/logs or app runtime database.
- Upgrade: overlay this complete project and rebuild/redeploy the existing image.
  Preserve configuration, both databases, source profiles and signing keys. No
  Compose changes, extra services/mounts, reader URL change or data reset is needed.
- Validation: the complete project check passes with 1,643 backend tests passed,
  five environment-dependent skips and one dependency deprecation warning. Nine
  matching-state and twelve polling scenarios pass, along with existing UI,
  migrations, bundle, version-mapping and archive/build-structure checks. Live
  Docker/TrueNAS, rendered browser, actual APK/JAR compilation and real
  source/provider integration remain unverified. Wider review findings are tracked
  separately; no general claim of solved page latency or all integrations working.

## 0.3.24 - 2026-10-04

- Complete the matching-rule/history request with research-driven safeguards.
  Automatic import-source owner merges now require the complete original-member
  proof and respect saved Different works decisions. Explicit manual merges keep
  their existing behavior. Native automatic attachments respect the 94% title
  minimum and any higher global floor; a copied summary cannot lower it.
- Read retained primary/variant database rows by their own named columns so older
  migration layouts cannot corrupt policy evidence or crash source-record splitting.
  Derive matching-policy identity from the actual owning source, even when incoming
  metadata contains a null or incorrect sourceId.
- Freeze the submitted matching-rule controls until save completes, preserving
  the other direction's draft and restoring original disabled states on failure.
- Add a portable development knowledge updater/search tool and project guidance
  for the separately saved AutoSourcarr-knowledge.sqlite3. Track source hashes,
  requirements, research, historical releases, findings and verification limits.
  Incremental indexing retains old evidence and flags changed source; explicit
  reinspection is required to refresh evidence hashes. Refuse unrelated databases
  and exclude runtime configuration/account state. This is not an app service or
  a replacement for catalog.sqlite / chapter-origins.sqlite.
- Upgrade: overlay this complete project and rebuild/redeploy the existing image.
  Preserve configuration, data, source profiles and signing keys. No Compose edits,
  extra service, mount, reader address change or database reset is required.
- Validation: the complete project gate passes with 1,607 backend tests passed,
  five environment-dependent skips and one dependency deprecation warning.
  New matching and knowledge regressions and the delayed-rule-save UI harness
  pass; version mapping, migrations, bundle synchronization and archive guards pass.
  No live Docker/TrueNAS, actual APK/JAR compilation, production source/provider
  integration or rendered-browser verification is claimed. Broader research gaps
  remain explicitly open in the development knowledge database.

## 0.3.23 - 2026-10-04

- Add Duplicates → Automatically combined: paginated/searchable automatic merge
  history with original entries, saved reason and decision evidence. Follow later
  merges to inspect the current entry or explicitly review/split its source records.
  Keep re-evaluated events visible and explain missing historical evidence.
- Record future automatic merge audits atomically with the core catalogue change,
  including automatic import-source owner merges. Correct survivor/snapshot mapping,
  normalize reversed older snapshots, tolerate malformed old nested data, and store
  the actual approving original-member proof rather than recomputing an old decision.
  Large proof lists retain the first 64 pairs with an explicit truncation marker.
- Add Web UI rules for each source/language variant and custom source group, with
  independent Same source and Different sources controls. Choose inheritance,
  Disabled, or required native title/summary/author-artist/tag checks and thresholds.
  All chosen checks and both sides' rules must pass; missing evidence requires review.
  Existing canonical tag synonyms and manual equivalences apply to tag overlap.
- Preserve built-in safety floors, edition/identity checks, trusted native anchors,
  non-transitive merges and explicit manual choices. Rules are additional constraints
  for future automatic decisions and never retroactively split existing entries.
  Defaults remain unchanged with no saved custom rules. Preserve/remap policies
  during source preference restoration and remove policies for deleted groups.
- Recheck delayed imports/variants under the matching write gate, enforce every
  affected owner source, serialize ingestion against rule edits and reject stale
  configuration revisions. Keep per-source native policy evidence separate from
  trusted matching anchors. History and rule panels load locally on demand without
  adding requests to catalogue boot or contacting upstream sources.
- Upgrade: overlay this complete project and rebuild/redeploy the existing app image.
  Keep config, databases, source profiles and keys. No Compose edits, worker tokens,
  extra services, database reset or reader-address changes are required.
- Validation: full project gate passes with 1,592 backend tests passed and five
  environment-dependent skips (30 new backend cases). New regressions cover source
  and group inheritance, independent directions, both-side checks, missing evidence,
  gallery preservation, import/ingestion races, current rules over legacy lineage,
  history durability and malformed data, actual saved proof, auth and stale writes.
  JavaScript behavior checks cover lazy loading, inheritance, validation, independent
  drafts, history paging, safe rendering, working inspection links and stale responses.
  Legacy-schema migration preserves historical events and starts with no custom rules.
  Python/JavaScript/shell syntax, bundled-tool parity, Compose shape, release alignment
  and synthetic APK/JAR packaging checks pass. Docker and a browser binary are absent:
  live Docker/TrueNAS, rendered-browser QA and actual APK/JAR compilation remain
  unverified. Existing subscriptions, configuration and source profiles were not reset.

## 0.3.22 - 2026-10-04

- Remove obsolete source-repair Compose generation, worker URL/token forms and
  hard-coded normal/private instance selections. Each instance already owns its
  repair process, account, provider configuration and history; no Compose discovery
  or manual instance list is needed. Configure repair AI opens the existing Repair
  and AI setup step directly, preserving account login and draft cancellation.
- Detect the integrated image using its image marker or bundled server, ignoring
  stale external-mode environment settings. Missing bundled tools report a failure
  instead of reconnecting an old external worker. Hide unused worker fields in the
  integrated settings editor while retaining their file values for rollback.
- Retire the legacy connection-write and Compose-preview HTTP endpoints with 410,
  before consuming submitted secrets or changing state. Keep CLI-only compatibility
  helpers and existing retained files; remove their obsolete web setup instructions.
- Report actual internal-service failure/startup state. Clear stale account and
  ready controls after connection loss, hide repair submission and disable automatic
  checking until the controller returns. Keep Configure repair AI available.
- Show simple readiness guidance before the isolation check. Distinguish missing
  tools and check timeout from an actual host restriction. Preserve the sandbox,
  identity/functional validation, human installation and source pacing gates.
- Upgrade: overlay the complete project and rebuild/redeploy the normal app image.
  Keep config, data, source profiles and signing keys. Routine AI configuration
  requires no Compose edits, extra services, worker token or instance selection.
  Actual host namespace restrictions remain an administrator concern; the app
  cannot grant itself host permissions and does not bypass isolation.
- Validation: full project gate passes with 1,562 backend tests passed and five
  environment-dependent skips. Eight new cases plus updated UI/web regressions
  cover retired mutation routes without secret consumption, bundled-image mode
  detection despite stale settings or missing tools, actual process failure state,
  absent deployment forms, preserved legacy file values, unchecked isolation and
  direct AI-wizard navigation/cancellation. JavaScript checks also cover status
  requests failing after readiness and cleanup controls becoming unavailable.
  Python/JavaScript/shell syntax, bundled-tool parity, Compose shape, release
  alignment and synthetic APK/JAR packaging checks pass. Docker is unavailable:
  live Docker/TrueNAS, rendered-browser QA, real provider account sign-in, source
  repair and actual APK/JAR compilation remain unverified here.

## 0.3.21 - 2026-09-30

- Add Manual actions → Reset all manual actions with decision counts, typed
  confirmation and a revision-checked preview. Pause/drain app writers before
  clearing manual names, matches/groups, separate-entry rules, duplicate/mixed
  reviews, tag decisions, import rejections/learning and chapter rejections.
  Preserve current library identities, completed merges/splits, subscriptions,
  added/existing imports, source settings and audit history. Return skipped imports
  to Unresolved, recompute suggestions and preserve cached feed IDs for fast views.
  Restore rejected chapter candidates only to their current owning entries.
- Put ChatGPT/Codex device sign-in directly in the Repair AI setup step. Keep
  configuration as a draft until Finish; explicit account actions are immediate.
  Operation IDs make wizard cancellation target only its own pending sign-in,
  including across tabs, delayed responses and cancelled requests.
- Add OpenAI, Claude, Gemini, OpenRouter, Ollama and Custom provider selections,
  address/protocol presets, account links and exact model/key fields. Custom accepts
  Responses, Chat Completions or Anthropic Messages. Require explicit replacement
  or removal before changing a provider with a saved secret. Add saved-endpoint
  connection checks in Source repairs as well as setup.
- Add bounded native Messages/Chat Completions file-editing clients in the existing
  isolated repair environment. Keep keys in the controller, pin the selected host,
  model and API routes, preserve tool history and restrict edits to the approved
  source module. Retain public-only website access, build/functional checks and
  explicit installation. API choices require separate API credentials; they do
  not reuse another provider's chat subscription or browser cookies.
- Replace the generic repair-isolation failure with bounded sandbox stderr,
  namespace/AppArmor/seccomp indicators and specific next steps in both setup and
  Source repairs. Keep repair disabled when isolation is unavailable. Host policy
  changes still require administrator/Docker configuration; no privileged or
  unconfined fallback is introduced. TRUENAS.md includes the per-service settings
  for the reported deployment and explains when the named AppArmor profile applies.
- Upgrade: overlay the complete project and rebuild/redeploy existing services.
  Retain all live configuration and data. No additional permanent container is
  needed. Use setup again for AI choices and account actions. If isolation is
  blocked, follow the actual diagnostic before changing host configuration.
- Validation: full project gate passes with 1,554 backend tests passed and five
  environment-dependent skips. The 21 new backend cases cover reset scope/stale
  previews/exception rollback, preserved feeds and import suggestions, provider
  configuration, native model tool round trips, source-file scope, bounds, account
  operation ownership and isolation-failure classification. Production relay
  handlers were exercised against local TCP model fixtures; these are not live
  provider tests. JavaScript behavior checks cover inline sign-in, cancellation,
  provider presets and private key drafts. Syntax, bundled tools, Compose shape,
  release versions and synthetic APK/JAR packaging checks pass. Exception rollback
  across both databases is tested; WAL does not guarantee cross-database atomicity
  after power loss. Live Docker/TrueNAS, rendered-browser QA, actual account sign-in
  and model interoperability, isolated Unix relay mounting, real source repairs
  and actual APK/JAR compilation remain unverified in this environment.

## 0.3.20 - 2026-09-29

- Expand setup with repair AI, account login/checks, optional main-Suwayomi HTTP
  Basic credentials, Comic Vine and translation keys, app access and companion-app
  browser links. Keep advanced fields collapsed. Separate browser URLs from Docker
  API addresses; source-specific accounts remain in source settings/WebView.
- Finish configuration before account actions. Continue the guide through tool
  preparation, ChatGPT device sign-in/status/sign-out or a custom model check;
  resume this stage after restart. Guard pending account actions against closing,
  cancel owned unfinished logins, and discard stale polling responses.
- Add local/custom Responses-compatible repair AI through the bundled Codex editor.
  An isolated edit-only relay pins one configured server and model, forwards only
  Responses routes, discards client headers and rejects redirects. Provider keys
  stay in the controller; custom jobs do not receive ChatGPT auth. Build/validation
  sandboxes retain public-only egress and no model capability. Preserve compile,
  identity, functional source validation and explicit installation gates.
- Preserve hidden credentials unless explicitly replaced/removed. Reject silently
  forwarding a saved credential to a changed endpoint. Keep password whitespace;
  validate provider values before saving. Rotate stale browser admin tokens through
  reauthentication, and honor the wizard’s longer save timeout.
- Honor separate LibreTranslate endpoints in integrated mode through an explicit
  connection selector; keep built-in/legacy behaviour as the default. Keep new
  helper configuration per instance with private file permissions.
- Upgrade: overlay this complete project and rebuild/redeploy the existing app.
  No new Compose services, mounts or environment settings are needed. Run setup
  again, save, restart when prompted, then refresh to continue account setup.
- Validation: full project gate passes: 1,533 tests passed, five environment-dependent
  skips. Twenty new backend cases cover credential changes, configured endpoints,
  origin-limited HTTP authentication, translation modes and custom AI relay controls.
  The production HTTP relay handler was exercised against a local model fixture over
  TCP, including header isolation, fixed model/destination, redirect rejection and
  interrupted upstream cleanup. The Unix transport check is skipped because this
  executor prohibits Unix sockets. The existing four environment-dependent skips
  remain. Production JavaScript behavior checks cover secret removal, custom AI
  drafts, login cancellation/stale polling, navigation and admin-token rotation.
  Syntax, bundle, version, Compose-shape and synthetic APK/JAR packaging gates pass.
  Live Docker/TrueNAS startup, rendered-browser QA, actual account sign-in, pinned
  Codex/model interoperability, isolated Unix relay mounting, real source repairs
  and actual APK/JAR compilation remain unverified in this environment.

## 0.3.19 - 2026-09-29

- Fix the integrated image build: request Google's published SDK package
  `platforms;android-37.0` instead of the nonexistent `platforms;android-37`.
  Retain API35/36 compatibility and the valid `build-tools;37.0.0` package.
  Check the API37 android.jar and apksigner after installation.
- The supplied latest TrueNAS failures happen at Dockerfile SDK installation,
  before either AutoSourcarr container exists. The older env-file and registry-pull
  errors are separate historical failures, not evidence of the current blocker.
- Upgrade: overlay this complete project and Save/Redeploy the existing TrueNAS
  app. Both instances share the corrected build context. Keep the current Compose,
  databases, config, source profiles, signing keys and build caches. No new service,
  database migration or reader API change is required.
- Validation: reproduce exit 1 / package-not-found with the base image's exact
  SDK command-line tools (11076708). Installing the corrected API37.0 platform and
  build tools succeeds (exit 0), with a nonempty android.jar and runnable apksigner.
  All six requested packages were checked against Google's stable repository.
  Backend tests: 1,513 passed, four environment-dependent skips. The remaining
  project gate, including bundle, version, UI and shell checks, passes. Full Docker
  image build/startup and APK/JAR compilation remain unverified: no Docker here.

## 0.3.18 - 2026-09-28

- Add an eight-step first-visit setup wizard, prefilled from current configuration,
  with short field explanations, collapsed advanced controls and feature guides.
  Cover library connections, repositories, matching, reader endpoints and keys,
  bounded performance limits, translation and built-in tool locations.
- Add Settings → Run setup again. Rerun drafts cancel through the button, Escape
  or backdrop. Finish alone commits changed fields; stale revisions are rejected.
  Recover interrupted multi-file saves and bootstrap fresh env files. Keep the
  completion marker across version upgrades and private-instance settings separate.
- Read setup-managed keys from canonical retained env files before clients start,
  overriding stale Compose values. Keep later ordinary Settings edits authoritative.
  Use the effective internal URL for private reader builds. Offer an in-app restart
  in integrated containers and explain reader rebuild requirements.
- Redact existing reader keys; allow secure draft-only key generation. Keep all
  wizard drafts out of browser storage. A completed-install refresh does not wait
  for the setup check; the first check uses a short deadline with a retry route.
- After Finish, run a bounded scan for old generated release notes and orphaned
  app bytecode. Report removals and preserve unknown files and all retained data,
  profiles, signing keys, artifacts and backups. Unmounted host files are outside
  the scan. Cleanup failures never undo completion or erase configuration.
- Validation: complete project gate passed with 1,513 tests passed and four
  environment-dependent skips. The 28 new backend tests cover setup creation,
  changed-only writes, revision conflicts, durable recovery, token validation,
  cleanup scope and one-shot restart scheduling. Production wizard JavaScript
  passes seven behavior scenarios and syntax checks; final secret-DOM clearing
  was rechecked with the same UI scenarios. Live Docker restart, TrueNAS deployment,
  rendered-browser QA and real APK/JAR builds remain unverified.

## 0.3.17 - 2026-09-28

- Bundle source repair/Codex, reader APK/JAR builder, LibreTranslate and supported
  challenge handling inside each AutoSourcarr image. Default deployments contain
  only AutoSourcarr + main Suwayomi (four services for two instances). No Docker
  socket, external worker token entry or helper Compose is required. Existing
  config/data/source/signing mounts remain authoritative; tools use /data/tools.
- Add Settings → Services and Status build/cancel/download controls. Hide legacy
  worker/solver connection setup in integrated mode; group advanced compatibility
  settings separately and collapse storage maintenance. Keep user settings drafts
  through tab switches; guard edits against delayed configuration responses.
- Restore the last tab, safe filters/pages/subtabs, selected import/duplicate,
  scroll and detail-section expansion on refresh in the same browser tab. Do not
  store secrets, unsaved configuration, confirmations or open inspectors. Restore
  source selections after inventory arrives and load only the requested page;
  late preferences and responses cannot switch the user's newer navigation.
- Start supervised helpers without waiting on the HTTP startup path. Load language
  models lazily, reuse caches, coalesce repeated build clicks and use bounded
  background processes/threads. Limit Gradle to two workers and bounded JVM memory;
  repair jobs remain serialized per instance. Keep matching/source pacing and
  manual/automatic work separation. No numerical live speedup is claimed.
- Keep repair validation isolated with mandatory rootless namespaces, read-only
  tools, per-job work/test profiles and public-only proxy egress. Retain exact
  source identity, patch-scope and functional metadata/chapter/page validation
  gates. Refuse repair if isolation or prior cleanup cannot be established.
  Supply narrow seccomp and named AppArmor profiles; host policy may need a
  one-time administrator setup. No unconfined/privileged fallback is used.
- Retain per-instance signing/checkouts; publish version-bound JAR/APK downloads
  with hash/size checks. Existing published installations rebuild after version
  upgrades; first setup and changed URLs use Status → Build extensions. Old
  external repair history/account state is preserved in place, not automatically
  imported. Sign in independently from each app's repair panel.
- Fix helper shutdown/pipe drain ordering, repeated cancellation and account
  admission after uncertain teardown. Preserve delayed proxy responses after
  request half-close. Add Linux descendant reaping for detached browser/build
  children, with process identity checks. Export safe service stage/exit state
  with diagnostics, without raw tool/account output.
- Validation: full project gate passed; 1,485 tests passed, four skipped (two
  optional JVM checks and two real-/proc lifecycle checks unavailable here).
  Production JavaScript passed syntax and 12 navigation/settings behavior scenarios.
  New behavior checks cover real UI state
  and edit races, isolated workspaces/credentials, cancellation ownership, proxy
  response completion, process-tree scoping and download integrity. Two process
  lifecycle tests require real Linux /proc PIDs; this executor virtualizes them.
  AppArmor syntax passed an offline parser. Docker image build/startup, actual
  namespace enforcement, account login, live source validation, complete APK/JAR
  builds and rendered-browser QA remain unverified in this workspace.
- Upgrade: overlay this complete project, keep retained mounts and add the current
  integrated/security settings documented at the top of TRUENAS.md. Rebuild and
  redeploy the app image(s), then refresh. The first x86-64 image build is larger
  because it bundles SDK/JDK/browser/language tools. Existing compatible readers
  remain usable; no port/database migration is required. No live deployment made.

## 0.3.16 - 2026-09-28

- Add an actual Set up repair worker button beside the Source repairs status.
  The previous instruction to generate Compose was plain text; the form was nested
  in two disclosures. The new button opens both sections and focuses the Compose
  field immediately, even when no worker is deployed or the status request is slow.
- Give setup disclosure headings full-width click targets and use a single-column
  account panel instead of the source-extension icon grid. Keep theme colors.
- Show missing Compose/host-path/service-selection feedback next to Generate and
  focus the missing field. The host path examples remain clearly labelled examples.
  Show generation progress, prevent duplicate submissions, retain failed input and
  restore the button after a failed request. No worker is required to generate.
- Preserve backend Compose safeguards, credentials, both instance mounts/ports,
  library data and all repair/account controls. Preview still never deploys.
- Validation: 1,461 tests passed, two optional JVM checks skipped. The remaining
  project checks passed after version alignment; 67 focused checks passed again.
  Four new deterministic UI checks fail on the previous implementation and pass
  with the fix. Independent review passed the eight affected UI checks. Actual
  rendered mouse hit testing, Docker/TrueNAS deployment and full APK/JAR builds
  remain unverified; this environment has no browser or Docker executable.
- Upgrade: overlay the complete project, Save/Redeploy both AutoSourcarr app
  containers and refresh the browser. Existing 0.3.15 repair workers and reader
  extensions remain compatible; no worker or APK/JAR rebuild is required for this
  UI correction. Keep all retained configuration/data/source profiles/signing keys.
  First worker installation still needs the generated Compose saved in TrueNAS.

## 0.3.15 - 2026-09-28

- Add Sources → Source repairs → Setup and account for tested/persistent worker
  connection, tool preparation, official ChatGPT device-code sign-in, saved-login
  checks, cancellation and sign-out. Existing repair/evidence/install/rollback and
  daily-watch controls remain in the same tab. No routine terminal commands needed.
- Add a private Compose preview for first installation using the complete existing
  Custom App configuration. Preserve selected app ports, mounts and network aliases,
  unrelated services, and existing managed worker token/data path. Reject overlapping
  live mounts, invalid paths, duplicate keys and YAML aliases. Preview never deploys.
  A one-time TrueNAS Save/Redeploy is still required; the app cannot grant itself
  Docker access. Only the private controller has the Docker socket.
- Bundle the narrow pinned Codex tool build context with the controller; prepare
  missing tool images through an owned background action. Serialize preparation and
  account changes against queued/running repairs and the shared account lock.
  Bound commands, code lifetime and retained output. Drain idempotent concurrent
  cancellation/shutdown; uncertain container removal holds the lock and offers
  Retry account cleanup in the UI once Docker is available again.
- Keep credentials on the worker. Return only the official sign-in URL/code and
  understandable status, never raw account command output. Clear code on expiry,
  cancellation, completion or tab exit; resume active login after refresh. New admin
  setup routes reject cross-site mutation, use no-store and suppress client exports.
- Test connection before commit; save restricted-permission config and activate it
  as one cancellation-safe operation. Failed checks leave the existing connection
  intact. UI-saved connection overrides Compose after restart; tokens are not echoed.
- Validation: full project check passed: 1,457 tests passed, two optional JVM
  checks skipped. Added 27 backend, real-subprocess/fake-Docker and browser-event
  checks for setup, account controls, cancellation/cleanup, connection persistence,
  secret handling and UI flows. Corrected an older deadline test to observe actual
  committed database writes rather than cancelled coroutine returns; it still
  checks all eight entries and no repeated ingestion. Independent review closed
  cleanup and connection-commit races. Live Docker/TrueNAS, real account sign-in,
  rendered-browser QA and full APK/JAR builds remain unverified.
- Upgrade: update/redeploy app and optional repair controller from this complete
  project, then use Setup and account → Prepare repair tools. Existing reader
  extensions remain compatible; no APK/JAR rebuild or port change is required.
  Keep retained config/data, repair auth/jobs, source profiles and signing keys.
  Domain migration still requires ownership review. No live deployment performed.

## 0.3.14 - 2026-09-28

- Fix repeated reader "still preparing" failures caused by restarting cold ingestion
  from the first title after its 60-second work limit. Retain the acquired source
  batch, cursor and scores across bounded attempts; do not refetch completed work.
  Preserve source errors, ranking, filters and pagination. A pending page is not a
  successful empty feed. Reset drains admitted writes before clearing checkpoints.
- Read known source records in batches instead of rewriting their titles/tags/media
  for every reader browse. Use current stored metadata and retain force-live refresh
  for updates. Exclude deleted/inactive records and recheck exact ownership after
  resumed work, preserving manual moves, titles and identities. New-record matching
  and source request pacing/concurrency limits remain unchanged.
- Bound retained reader checkpoints to the live-job limit, expire idle checkpoints,
  preserve progress on capacity deferral and prevent stale callbacks deleting a new
  job's state. These checkpoints are in memory; completed feed caches remain durable.
- Add repeated --service options to the TrueNAS repair setup helper for the normal
  and private AutoSourcarr in one Custom App. The worker/account and job history are
  shared; original app mounts, ports, network membership and unrelated services stay
  intact. Both app containers still use internal 8282.
- Enforce existing Kotlin repair file layout (no added/deleted files) and direct
  repairs toward minimal API-path/parser fixes with existing pagination, caching,
  batching and pacing. Domain migration still requires a separately reviewed change;
  the worker does not silently follow a redirect or choose a similar-site mirror.
- Reproduction: the old reader repeated the same initial records and never saved a
  page under a scaled work deadline. The regression now completes with one upstream
  fetch. In a controlled 30-source × 20-card known-record fixture, reader local work
  fell from 0.9633s to 0.01579s; the same 30 mocked upstream calls were made. These
  measurements do not establish live Suwayomi/source/network latency.
- Validation: full project check passed with 1,430 tests passed and two optional
  JVM checks skipped. Added eight reader regressions and five dual-instance setup
  cases; strengthened repair file-layout checks. The timeout-loop regression fails
  on the exact 0.3.13 baseline. Independent reader review closed callback ownership
  and capacity-deferral retention cases. Docker, full APK/JAR compilation, live
  Suwayomi/website repair jobs and rendered-browser checks remain unverified.
- Upgrade: overlay this complete project and Save/Redeploy both AutoSourcarr apps.
  Existing 0.3.12/0.3.13 reader JARs/APKs remain compatible; no reader rebuild or port
  change is needed. Preserve all retained config/data/source profiles and signing
  keys. For optional repairs, use the updated setup commands in README, preview
  then apply, and sign in on your server. No live deployment was performed here.

## 0.3.13 - 2026-09-27

- Manually uploaded source APKs now appear in Sources → Discover → Installed orphan.
  Recover classifications from earlier confirmed successful upload receipts; ignore
  failed, uncertain and restored attempts. Locally installed engine packages with no
  repository/download origin are also recognized. Keep exact package/source IDs.
- Add Mark as orphaned for installed packages whose repository is no longer configured.
  Existing archived/obsolete packages remain in the orphan groups. Unknown settings,
  unavailable inventory and repository failures are not evidence of removal. Require
  admin access and fresh installed inventory for classification actions.
- Add Return to Installed to undo a label. Persist choices across restart, package
  updates and repository-history refreshes, separately from archive provenance. New
  manual uploads mark the package again. An uninstalled local package has no invented
  archive or automatic reinstall; its original APK can be uploaded explicitly. If
  a configured repository still offers it, preserve the normal Not installed → Install
  action. Classification accepts valid native package namespaces without granting
  them Keiyoushi archive/repair provenance.
- Installed orphan cards retain source settings, available updates and Uninstall.
  Keep them out of duplicate regular Discover listings while retaining the complete
  inventory for language-specific settings. Refresh both views after uploads and
  other source mutations; discard older inventory responses after classification.
- Validation: full project check passed; 1,418 tests passed and one optional JVM
  transport test skipped. Includes 24 new backend/UI checks for classification,
  restart persistence, failed uploads, undo, reinstall visibility and stale responses.
  Updated the existing install-response test for additive classification fields.
  JavaScript and shell syntax, Compose/env wiring and release packaging guards pass.
  Docker, live Suwayomi and rendered-browser verification were not available.
- Upgrade: overlay the complete source, Save/Redeploy both AutoSourcarr apps and
  refresh the browser. Keep each instance's retained config/data/source profiles and
  signing keys. No Compose change or extension rebuild is needed for this feature;
  the reader transport remains unchanged from 0.3.12. Both AutoSourcarr instances
  use internal port 8282: the private reader URL is http://private-autosourcarr:8282.
  All notes stay in this changelog.

## 0.3.12 - 2026-09-27

- Added Source settings to installed extension cards in Sources → Discover, including
  installed updates and orphaned packages. Resolve the installed language variants
  directly from the engine inventory; opening the separate Installed tab first is
  unnecessary. Reuse the preference editor, draft protection and exact string IDs.
- Fixed reader Popular/Latest/filter preparation HTTP 503: the previous six-second
  response budget expired alongside the default six-second source batch, before
  result assembly. Allow eight seconds; the updated reader handles only explicitly
  marked own-origin preparation responses before HttpSource rejects them, with at
  most two follow-ups of the same coalesced job and 1–3 second pauses. Preserve
  cancellation, native call deadlines and ordinary upstream error behavior.
- Blank reader filter searches now browse Popular with the selected source/count/
  sort/page options. Apply source membership before local shortlisting and cached
  page limits, including active additional records; prevent unrelated local titles
  and previously polluted saved feeds from leaking through the selected-source filter.
  An empty language selection returns empty without querying source websites.
- Reproduced a default five-source cold batch: 0.3.11 returned 503 at 6.00 seconds
  while the feed completed at 6.09 seconds. The corrected backend returns that feed
  on the first call in the same controlled workload; subsequent cached reads reuse
  results. This is a fixture measurement, not a live latency guarantee. Existing
  website pacing, concurrency limits and matching quality safeguards remain in force.
- The private Suwayomi log shows successful startup on 30059, not a fatal crash.
  The previously supplied 30062:30058 mapping therefore misses its listener. Restore
  private-suwayomi BIND_PORT=30058, retain 30062:30058, and use the private app's
  MAIN_SUWAYOMI_URL=http://private-suwayomi:30058. If 30059 is intentional, change
  the mapping target and internal URL together instead. These are instructions for
  the existing deployment; no live configuration was modified.
- Validation: 1,394 tests passed (21 new), one older optional JVM test skipped;
  the complete project gate passed. The production Kotlin preparation interceptor
  was separately compiled and executed against real OkHttp/local HTTP, covering
  retries, response closure, URL scope, cancellation and whole-call deadlines.
  The default batch timing and both selected-source leakage regressions fail on
  the exact 0.3.11 baseline. Independent review found and closed cached match-count
  filtering after page truncation. Docker is unavailable: full APK/JAR compilation,
  live Suwayomi/TrueNAS/source-site checks and rendered-browser QA remain unverified.
  An incomplete earlier runner result was discarded; the final gate reached its
  complete test summary and all project-check stages.
- Upgrade: overlay the complete source, Save/Redeploy both app containers, refresh
  the UI, then rerun the working paired builder and reinstall each new reader JAR
  in its corresponding main Suwayomi. The reader transport fix needs the rebuilt
  extensions. Preserve both configurations, databases, source profiles, identities
  and signing keys. No new service or source-host container is required.

## 0.3.11 - 2026-09-27

- Added source preferences under Sources → Installed, including per-language
  selection, switches, checkboxes, text, lists and multiple selections. Engine
  screen revisions prevent saving to a changed setting; unsaved drafts remain in
  memory, and sensitive values are excluded from shared error diagnostics.
- Added drag/drop and file selection for source APKs under Sources → Discover.
  Installations are sequential, size/archive/package checked and handled by the
  native integrated Suwayomi installer. Existing integrated packages are backed up;
  a disconnected browser cannot release the maintenance lock mid-install. Uncertain
  results require inventory refresh instead of automatic retry. Source website
  functionality is not implied by successful installation.
- Corrected guidance for private-autosourcarr:8283 connection refusal: the supplied
  30061:8282 mapping needs AUTOSOURCARR_INTERNAL_URL=http://private-autosourcarr:8282,
  then rebuild/reinstall the private JAR. Builder preflight warns about unusual
  known-service ports without overwriting deliberate custom URLs.
- Moved cached-feed metadata planning and source snapshot writes off the HTTP event
  loop. Reader requests coalesce, preserve cache ranking/filter/pagination and bound
  cold-work admission; a six-second response budget returns a clear retryable
  catalog-preparing result rather than waiting for a client socket timeout. Existing
  source request/concurrency limits and matching rules remain in force.
- Controlled same-feed benchmark with an injected 200 ms metadata-planning delay:
  median response 200.907 → 0.715 ms, median maximum event-loop delay 195.869 → 0.762 ms
  (three runs). This isolates local scheduling, not live TrueNAS/network latency.
- Validation: 1,373 tests passed (56 new), one optional actual-JVM check skipped;
  the complete project gate passed, including syntax, Compose, version mapping and
  artifact-validator fixtures. Docker was unavailable. Live Suwayomi/TrueNAS, actual
  APK/JAR compilation, source-site access and rendered-browser verification remain
  unverified. Independent review found and closed a reader queue admission gap.
- Upgrade: overlay the complete source, Save/Redeploy both existing app containers,
  refresh browser assets, correct the private retained internal URL and rebuild/reinstall
  both versioned reader JARs. No new Compose service or port is required; preserve all
  existing configs, databases, source profiles, package identities and signing keys.

## 0.3.10 - 2026-09-27

- Treat separate uploads in a confirmed gallery work/creator collection as separate, stable chapters. Recognize explicit Chapter/ch./Chap./Part/pt./Episode/ep. numbers; corresponding numbered uploads and conventional multi-chapter sources can supply alternatives at the same chapter. Preserve native chapter numbering and specials. Unnumbered uploads retain their own source/gallery/chapter identity; identical titles alone do not merge unrelated creators or assume identical cross-site pages. Animated galleries remain separate and carry an `(animated)` suffix.
- Add a deferred local upgrade for cached gallery metadata and collapsed chapters. Preserve manual groups, locked matches, rejected candidates, existing source IDs, page caches and serving history. Split automatic static/animated mixtures only after rechecking current ownership and manual decisions; keep the served virtual ID with its actual gallery candidate and restore native occupants to their proper number. Concurrent hydration cannot overwrite newer metadata with the migration's snapshot. Existing shared maintenance serialization now uses cancellation-draining workers at asynchronous source-write call sites, preserving event-loop responsiveness and reset ownership.
- Entry details display before chapters load. Order collapsible sections as Alternate titles, Tags, Metadata, Source matches, Chapters and Previously served. Load chapters/history on demand with local retry and stale-response protection; preserve drafts, selection/focus and section choices during refresh. Explicit matrix entry selection reveals the inspector when scrolled far down. Move Orphaned and Installed orphan into persistent collapsible groups inside Sources → Discover.
- Add saved-source filters to Browse, Subbed and Match Matrix, including active additional records. Apply membership/search before pagination; retain full string source IDs and authoritative empty Subbed snapshots. Filtered catalogue reads do not contact websites. Build and encode entry details on the existing dedicated catalogue read lane. Coalesce repeated duplicate snapshot requests, back off pending polls and stop hidden-view polling. Cache only bounded cover-presence hints to avoid repeatedly checking 500 files on a NAS; actual image delivery still validates file freshness and owned changes invalidate hints immediately.
- Harden the existing sequential extension builder: explicit paired mode requires both retained config mounts and checks both before downloads/compilation. Keep each instance's endpoints, reader token, signing state and Status APK publication separate; fail incomplete/missing private config clearly. Preserve last good outputs on failure and remove intermediate signed/idsig files. Paired artifacts are under `extension/dist-instances/autosourcarr/` and `extension/dist-instances/private-autosourcarr/`, with `-private-` in private filenames. The old `extension/dist/` is single-instance output. `--check` is read-only. README and `docker-compose.builder.yml` give the exact mount/env/command changes; the private host config path must be the user's existing `/config` mount.
- The supplied diagnostic reports a private main-library connection to `http://private-suwayomi:30062`. With the supplied `30062:30058` mapping, its app configuration needs `MAIN_SUWAYOMI_URL=http://private-suwayomi:30058`; host/browser port 30062 stays unchanged. Backend/UI version disagreement also came from the stale UI version constant, now aligned. Captured request timings do not reproduce the full reported eight-second delay, so no production latency guarantee is made.
- Controlled performance comparison: five warm passes over 500 covers with an injected 1 ms filesystem stat cost reduced median hint work from 550.2 ms (2,500 total stats) to 0.30 ms (zero repeated stats). This isolates filesystem overhead, not end-to-end browser/server performance. Existing indexes filtered a generated 50,000-entry catalogue in about 3 ms; an additional index offered too little benefit to justify a migration and was not added. Source pacing, concurrency limits and the 0.94 matching floor remain unchanged; no Redis or second database is required.
- Validation: **1,317 tests pass**, including 86 new cases beyond 0.3.9; one optional actual-JVM contract test is skipped. The complete project gate passes for syntax, version alignment, Compose/env wiring, UI/API retention and extension validator fixtures; its Docker stage explicitly skips because Docker is unavailable. All 134 previous main API routes and reader Kotlin source files are retained. Focused checks cover chapter provenance/migration, stale/manual writers, cancellation, source-filter paging, catalogue worker isolation, bounded cover hints, UI section races and paired builder configuration/publication. UI checks use a controlled DOM; rendered-browser QA is unavailable after the browser binary download failed. Docker/TrueNAS deployment, native APK/JAR compilation, live gallery retrieval and repaired-source validation remain unverified. No deployment was performed.
- Upgrade: overlay this complete source project and Save/Redeploy normally while retaining each instance's separate configuration, databases, integrated source profile and signing keys. Refresh the UI. Merge the updated builder service into the existing stack, replace its private config placeholder, then run its configuration check and build. Real deployment-specific APK/JAR files must be generated by that SDK builder; this ZIP does not contain compiled binaries. The optional repair worker image tags and extension fallback align with 0.3.10 / 1.4.3010. Evidence stays in this updating changelog; no review folders, raw logs or benchmark files ship.

## 0.3.9 - 2026-09-27

- Update the existing extension builder itself for sequential normal/private JAR and APK builds. A shared filesystem lock covers checkout, Gradle and outputs; each instance uses its own explicit env, internal/public URL, reader token, signing identity and Status APK publication. Retain previous valid artifacts on failures; set publication ownership before switching the current download. Single-instance use is preserved. Provide a small builder Compose example with two retained config mounts. APK package identity remains the same, so both APKs cannot coexist in one Android profile.
- Add source-native metadata profiles for E-Hentai/ExHentai, IMHentai and Luscious. Parse recognized creator/circle prefixes, language/category/franchise decorations and source tags while retaining original titles and field provenance. Source metadata remains usable when external catalogues have no record; descriptions are never invented. Collections require manual work-level review. Historical display titles update on ordinary hydration rather than rewriting manual labels.
- Add a namespaced tag catalogue and Manual actions → Tags: conservative known equivalence, spelling suggestions, independently searched Same/Separate/Clear controls and persistent review history. Gallery work matching requires the existing strong title floor plus informative tag support; creator/franchise/part/edition conflicts remain blockers. Apply the gate to local ingestion, variants, source discovery, imports, provider enrichment and duplicate merges. Tags never merge works by themselves. Manual tag separation overrides automatic synonyms and contradictory transitive groups are rejected.
- Persist tag/source provenance in the existing SQLite catalogue. Deferred local reindexing does not visit websites or gate startup, reports progress and drains admitted writes on cancellation. Index links follow active source ownership through merges/unbindings; normal resets keep tag decisions, full resets clear them. Preserve hydrated tags on thin feed cards, avoid rewriting unchanged tags, and restrict counts to the visible tag page. Read tag decisions in one SQL snapshot and prevent older reader transactions from poisoning shared equivalence caches.
- Add Sources → Orphaned and Installed orphan with original historical package provenance, explicit archived installation and the existing functional repair workflow. Ship 47 original historical package records from three available snapshots, with 46 removed release binaries distinguished from the surviving archive. Read current protobuf and historical JSON indexes; reject obsolete upgrade-notice indexes. Bound history scanning and keep cached results available during upstream failures. Recover unavailable binaries through the original source module and isolated validation rather than substituting unofficial websites. Installation/repair states distinguish available, unverified, blocked and validated work.
- Validation: **1,231 tests pass**, including 95 new cases beyond 0.3.8; one optional actual-JVM contract test is skipped. The complete project gate passes for syntax, version alignment, existing Compose/env wiring, retained UI/API paths and extension validator fixtures. Controlled SDK fixtures verify sequential builds, independent credentials/signing, shared locking, failed-publication preservation and UID/GID ownership. Real two-thread SQLite tests cover stale cache snapshots; tests exercise active-binding tags, retained decisions, cancellation, source-history parsing, recovery identity, uncertain installation, replacement freshness and UI response races. All 127 preceding main API routes and reader Kotlin source files are retained. Original GitHub history/asset metadata was read live; the surviving 49,608-byte Comiplex JAR's SHA256 and package identity were actually checked, exposing and fixing valid-XML-comment rejection in both validators. The other 46 archived release binaries were confirmed removed through the original release API. No third-party binaries ship. Docker, APK/JAR compilation, actual repaired website retrieval, account login, TrueNAS deployment and rendered-browser QA remain unverified; the UI tests use a controlled DOM environment.
- Upgrade: overlay the complete project and Save/Redeploy the existing app; preserve both instances' separate config/data/source-engine mounts and signing keys. Refresh the UI for new assets. Update the existing SDK builder with the second config mount/environment variable and run it once to build both instances. Source code repair requires the optional private worker and account setup described in README; existing workers should be updated to the 0.3.9 images. Extension fallback is 1.4.3009. No deployment, real reader APK/JAR compilation, live gallery retrieval or live repaired-source validation is claimed. All release notes remain in this single changelog; no review bundle or raw logs ship.

## 0.3.8 - 2026-09-26

- Add optional private source repairs using official Codex CLI 0.157.0 and the user's ChatGPT login. One private account/session stream runs at a time; device login stays on the user's host and credentials persist outside the application/project. Sources → Source repairs shows bounded job history, source patches, saved session IDs and independently collected retrieval evidence. Login/usage/sandbox failures stop without automatic unsafe fallback. No ChatGPT website automation or ordinary sidebar conversation is created.
- Back up installed binaries before repair and, when configured, before normal update/uninstall operations. Check the same Keiyoushi module first at a recorded commit; recover recently removed module code from bounded history or accept an explicit maintained commit. Reject unrelated replacements, changed package/source identity, incompatible loading metadata and unverified domain migrations. Allow one scoped Kotlin repair when maintained code fails; preserve build structure and dependencies. Catalog absence is a hint, not proof of retirement. Optional daily monitoring admits at most one new job per day and one per source per week; installation remains explicit.
- Separate credential-bearing Codex, Android builds and disposable Suwayomi validation. Only the trusted private controller holds the Docker socket. Job containers use an internal network with public-only HTTP/HTTPS egress; no app, live source profile, main library or socket mount is passed to them. Functional checks reject GraphQL partial errors, wrong search results, missing metadata/chapters/pages, invalid image bodies and truncated images. Record two work samples where available, old/recent chapters and first/middle/last images; CAPTCHA/rate limits/transport failures stop as blocked/inconclusive. Compilation is never proof of operation.
- Install only a recently validated, hash-matching candidate against the unchanged baseline. Drain/restart the integrated engine and replace only its compatible JAR, retaining its database/preferences/version metadata. Persist a recovery journal and restore interrupted/failed promotion without overwriting a newer independent package. Retain raw backups even when older APK-converted JARs lack an importable manifest; those repairs are blocked, not labelled verified working. No retroactive claim that a broken installed package was previously functional.
- Add optional Compose overlay, private setup/login helper and narrow TrueNAS Custom App configuration helper. Default deployments remain unchanged and source repair never gates startup/catalog reads. Keep config, both databases, source profile and signing keys. See README for one-time setup, resource requirements, limits and account sign-in; no deployment or account login is performed from this release preparation.
- Validation: **1,136 tests pass**, including 31 new cases; the complete project gate passes. One optional actual-JVM contract test is skipped. Docker, live Codex login, native source builds, actual website retrieval, TrueNAS deployment and rendered-browser checks are unavailable here and remain unverified. Controlled tests exercise source-image retrieval, partial-GraphQL rejection, CAPTCHA stops, wrong search results, old/recent chapter samples, private/mixed-DNS proxy rejection, full image decoding, manifest/scope protection, promotion/cancellation/crash rollback, daily admission limits and retained deployment configuration. No new reader APK/JAR is claimed; the extension fallback is 1.4.3008. All release evidence stays in this updating changelog.

## 0.3.7 - 2026-09-23

- Fix import lookups reporting local capacity waits as source failures. Bound import admission and shared source/verification admission separately from website execution, preserve deferrals through batch collection, release capacity on cancellation, and avoid website cooldowns for uncontacted sources. Collective reference-lookup deadlines remain retryable deferrals. Genuine admitted timeouts still feed source health; request limits, concurrency settings and matching safeguards are retained.
- Source-engine health now probes GraphQL plus the actual about API, retains each probe's timing/status/nonempty cause and stack locations, and rejects HTML/login/error responses. A working fallback reports degraded GraphQL health instead of hiding it. Known local-runtime deferrals do not produce another misleading website outage. Completed manual import decisions resolve only that item's obsolete discovery warning; replaced/failed decisions do not clear unrelated issues.
- Error downloads retain the version already present in older exports and add backend/UI build versions, per-error versions and process IDs, server/browser/snapshot capture times, package versions, error causes and stack locations without locals, timeout/admission phases, bounded task context, current affected import states and source cooldowns. A new authenticated local diagnostics endpoint uses cached dependency checks instead of launching network probes. Partial export failures retain available evidence and identify missing sections. Common credential forms are redacted; settings files, request bodies, cookies and authentication headers are not collected. Retention/truncation limits are explicit. Browser API failures record duration, deadline and server correlation IDs where available.
- Keep the app error page understandable: short explanations for health failures and import timeouts, with technical context collapsed. Grouped messages now carry the newest record's matching context/trace/version, while downloads retain every available original record. No theme/layout change and no raw review or test logs in the ZIP.
- Diagnosis: the supplied 2026-09-23 export identifies **0.3.5**, with one blank source-health failure and retained older import timeouts. Controlled transports reproduce the lost health causes; held local capacity reproduces false source cooldowns. The export cannot establish the cause of every historical timeout or prove a live server recovery. Browser/server timestamps use separate clocks and must not be assumed synchronized.
- Validation: **1,105 tests pass**, including 22 new cases; the complete project gate passes. Coverage includes controlled health transport failures, HTML/auth rejection, both local admission stages, actual execution deadlines, cancellation, manual priority, stale import-warning resolution, authenticated local export, partial failures, version mismatches, grouped context, stack retention and credential redaction. One optional actual-JVM contract test is skipped because its external runtime JAR is unavailable; Docker validation is unavailable. Native rendered-browser checks, live Suwayomi/TrueNAS and APK/JAR builds remain unverified; no deployment performed.
- Upgrade: overlay the complete project while retaining configuration, both databases, source data and signing keys; Save/Redeploy normally, then refresh the browser. Status should show 0.3.7; extension fallback is 1.4.3007. Retains the 0.3.6 Mixed entries and import-review fixes. No additional source-host or solver service is required for this release.

## 0.3.6 - 2026-09-20

- Add **Mixed entries** beside Duplicates to inspect one catalog entry containing potentially different works. Compare active primary and additional source records, native titles, covers, authors, summaries and explicit edition conflicts, including locked records and entries with no merge history. Needs review / Reviewed / All multi-record entries, search and pagination use cached catalog data; no live source searches or automatic splits occur. Open Match Matrix leads directly to the existing entry inspector and source correction controls.
- **Keep together** dismisses only the exact reviewed evidence; Undo review restores it and changed evidence reopens review. Missing metadata, untranslated summaries and unlabelled color editions are not treated as conflicts. Contradictory English title/summary evidence is advisory. Very large entries show a bounded evidence sample and retain all source records for inspection. Ordinary reset retains review decisions; Full reset clears them. Existing subscriptions, manual titles/bindings, source limits and automatic matching thresholds are unchanged.
- Fix manual import decisions remaining in Unresolved: wait for queue acknowledgement, correlate polls with the committed decision revision, discard responses belonging to replaced actions, and invalidate older paginated/run requests while retaining the current page. Completion fetches the current filtered page and counts. Match, Retry, Reject and Skip share these protections. Ambiguous POST transport failures reconcile server state instead of falsely rolling back a potentially accepted decision.
- Reuse existing full-width comparison layout, primary/secondary/tertiary theme roles and configurable cover sizes in the new tab. Source metadata is escaped; superseded search/detail responses are discarded. Explicit SQL columns preserve compatibility with retained databases whose variant columns were appended by migration. Compact assessments are cached by evidence fingerprint without caching stale record membership or review decisions.
- Validation: **1,083 tests pass**, including 27 new cases; the full project gate passes. The pre-acknowledgement polling regression also fails against the original 0.3.5 code for the expected reason. Coverage includes delayed Match/Retry/Reject/Skip, stale pages and replacement actions, uncertain POST outcomes, persisted queue revisions, native source evidence, retained column order, review/reset behavior, API authorization, escaping, thumbnail roles and stale detail responses. One optional actual-JVM contract test is skipped because its external runtime JAR is unavailable; Docker validation is unavailable. Native rendered-browser, live Suwayomi/TrueNAS and APK/JAR builds remain unverified; the managed browser does not permit the local preview. No deployment performed.
- Local synthetic benchmark: 10,000 entries with four source records each, English metadata and 276-character summaries, first page of All multi-record entries. Repeated full scans average 889 ms with the compact assessment cache versus 2,324 ms before it (61.7% less time); cold scan is about 2.6 s and one detail is under 1 ms. These are development-fixture measurements, not a guarantee for the deployed database.
- Upgrade: overlay the complete project while retaining configuration, databases, source data and signing keys; Save/Redeploy normally, then refresh the browser. Status should show 0.3.6; extension fallback is 1.4.3006. Includes the 0.3.4 matrix layout and 0.3.5 installation fixes. No extra source-host or solver is required for this change. Evidence stays in this updating changelog; no raw logs or review bundles ship.

## 0.3.5 - 2026-09-18

- Fix source-extension installation using stale package locations: a confirmed `updateExtension` download HTTP 404 triggers one catalog refresh (15-second refresh limit) and at most one new install attempt. Normal catalog reads remain local. Missing/obsolete entries, failed/incomplete refreshes and repeated 404s stop with repository-specific guidance. Already installed entries are accepted without another mutation. Successful installs require the requested package and `isInstalled=true`; source reconciliation stays in the background.
- Keep non-404 errors, CAPTCHA/rate limits, engine transport failures and update/uninstall actions outside this recovery. Cancellation never starts a subsequent retry. Repository recovery failures use HTTP 502 instead of implying that the integrated engine is down; runtime restart deferrals retain their retry header. After failed installation the browser refreshes its local extension inventory so removed entries are no longer advertised by the old view.
- Diagnosis: the supplied 0.3.3 15:57 export shows WitchScans installation HTTP 404 four times with a ready integrated engine and FlareSolverr. The exact failing package URL is absent, so stale catalog vs transient upstream availability cannot be conclusively distinguished. The pinned [Suwayomi installer](https://github.com/Suwayomi/Suwayomi-Server/blob/v2.3.2243/server/src/main/kotlin/suwayomi/tachidesk/manga/impl/extension/Extension.kt) uses stored package URLs, and its catalog refresh updates them. Both current WitchScans 1.6.34 APK/JAR locations from the [official catalog](https://github.com/keiyoushi/extensions/blob/repo/index.json) returned HTTP 200 to read-only HEAD checks here. No package was installed on a live system. The export's separate Browse timeout is not fixed or explained by this installation change.
- Validation: **1,056 tests pass**, one optional actual-JVM contract skipped; the complete project gate passes (Docker unavailable). Twenty-four added regressions cover real client/GraphQL response handling via controlled HTTP transport, bounded recovery, no-retry paths, malformed success, cancellation, API outcomes and browser inventory refresh. Ten regression cases failed against the previous implementation before the fix. Live installation on TrueNAS, Docker image building and actual APK/JAR compilation remain unverified.
- Upgrade: overlay the complete project while retaining configuration, databases, source data and signing keys; Save/Redeploy normally, then refresh the browser. Status should show 0.3.5; extension fallback is 1.4.3005. Includes the 0.3.4 Match Matrix fix. No extra solver or source-host container is needed for this fix. Evidence stays in this changelog; no review bundles or raw logs are added.

## 0.3.4 - 2026-09-18

- Fix Match Matrix entry cards collapsing when possible duplicates arrive: the duplicate-review button was a second item in a horizontal flex row and squeezed the entry card until its title/counts overlapped the cover. Stack the card and duplicate notice vertically, keep both within the entry column, and wrap long candidate names. Preserve separate entry/duplicate/delete actions, custom cover sizes, color roles, metadata loading and scroll retention. No backend or matching changes.
- Validation: **1,032 tests pass**, with one optional actual-JVM contract skipped; the complete project gate passes (Docker unavailable). A fixture executes the production renderer with normal, duplicate and manual/long-title entries and confirms the three actions remain independent. Styles were inspected with both existing density/cover-size rules; rendered-browser geometry remains unverified because the managed browser blocks local preview/file URLs. No browser-policy workaround was used. No live deployment or new APK/JAR build is claimed.
- Upgrade: overlay the complete project while retaining configuration, databases, source data and signing keys; Save/Redeploy normally, then refresh the browser to load the versioned stylesheet. Status should show 0.3.4; extension fallback is 1.4.3004. This updating changelog contains the release notes; no screenshot/verification bundles are added.

## 0.3.3 - 2026-09-13

- Fix repeated FlareSolverr health failures reported in the 22:33 export: separate the 2-second connection limit from a 5-second response limit, with an 8-second overall background bound. Report the sanitized destination, connection/response/protocol/HTTP failure and relevant corrective action. A missing enabled solver URL is now an actionable configuration failure. Preserve URL, session, fallback preference, intended enablement and source request limits; a solver outage is not evidence that every website needs CAPTCHA.
- Persist failed health-check backoff through 1, 2, 5, 15, 30 and 60 minutes, then hourly. Manual **Check automatic handlers** bypasses the delay; changed destinations are checked immediately after configuration is applied. Reused cooldowns do not inflate error counts. Catalog resets retain solver intent/backoff alongside existing human-attention pauses. Successful health checks re-enable the configured handler and resolve the warning.
- Solver-only retries reuse confirmed MangaFire preferences. Source reconciliation and manual checks still revalidate them, including extension changes arriving during an active setup. Transient source-preference failures retain their separate one-minute recovery schedule. Status/Source browser expose connection diagnostics and the next check; **Solver connection settings** opens the existing editor without discarding drafts. Repeated healthy language-variant messages are combined.
- Validation: **1,032 tests pass**, including 25 new cases; the complete project gate passes. One optional actual-JVM contract test is skipped because its external runtime JAR is unavailable. JavaScript behavior checks cover diagnostics, grouped messages and preserved settings drafts; existing color-role/contrast checks also pass. Local HTTP testing confirms a healthy 2.15-second response now succeeds. A controlled two-hour outage produces 7 probes instead of 121, with one visible warning and immediate manual recovery. Live FlareSolverr reachability/solving, TrueNAS Docker build, native WebView and APK/JAR compilation remain unverified. The supplied log omits the configured solver address, so its underlying service/network fault cannot be established from the export alone. Not deployed.
- Upgrade: overlay the complete project while retaining existing configuration, both databases, source data and signing keys; Save/Redeploy normally and refresh versioned UI assets. Status should show 0.3.3; extension fallback is 1.4.3003. If the solver remains unavailable, use Settings → Source browser → Solver connection settings to check the running FlareSolverr address and Docker network, then save/redeploy and check again. The integrated source engine still requires no separate source-host container. No extra solver is installed or retained address silently replaced. Evidence remains in this changelog.

## 0.3.2 - 2026-09-13

- Settings → **Source browser** adds known Chrome/Firefox identities, this-browser/custom UA input, apply and reset. Preferences persist in `/config/source-browser.json`; actions return promptly and own a source-process restart. The pinned JVM has no public default-UA API: an automatically compiled Java adapter sets the actual NetworkHelper initial flow and CefSettings WebView identity. Engine defaults remain untouched until a custom identity is applied. Explicit extension headers and solver/cookie identity pairing retain their own behavior; runtime hook status is visible.
- **Clear source cookies and WebView data** invalidates source WebView sessions, drains the owned JVM/process group, confirms port 4567 is no longer accepting connections, and removes only `settings/cookie_store.xml` and `cache/kcef`. It resets only the configured engine's named FlareSolverr session, reporting partial cleanup when unreachable. Preserve databases, downloads, extensions, other settings/cache and KCEF binaries. Cancellation drains work; failures recover the source process. Intentional restart deferrals do not count as website failures.
- Reuse existing supported MangaFire CAPTCHA handling and configured FlareSolverr; expose their status/check action alongside browser recovery controls. Do not install a duplicate solver, claim universal CAPTCHA solving, clear human-attention pauses on reset, or change hourly retry/source request limits.
- Theme **primary** now controls exact border/action hue; **secondary** controls card fills across tabs; **Dual accents adds tertiary** exclusively for evidence panels. Existing primary-colored cards and old secondary evidence migrate once into the new roles. All three colors have independent pickers/HEX fields with derived readable text. Theme, cover size, density and identity customization sections collapse and persist per user. Source-browser polling preserves unsaved UA drafts.
- Validation: **1,008 pytest tests pass** (23 new cases), including the optional actual-JVM contract, and the full project gate passes. Actual official checksum-verified v2.3.2243 NetworkHelper/CefSettings classes execute with Java 21 `-Xverify:all` for custom UA and default reset. Eleven-tab DOM checks cover color roles, migration, persisted collapse state, browser draft/selection/reset controls and retained behavior. Native Chromium/WebView startup, live CAPTCHA solving, TrueNAS Docker build and reader APK/JAR compilation remain unverified; the managed browser cannot reach the local preview. Not deployed.
- Upgrade: overlay the complete project while retaining data/config/signing keys, then Save/Redeploy normally. The Docker build compiles the tiny JVM adapter automatically using an official JDK builder; no manual adapter build or extra running service is needed. Refresh to load versioned UI assets. Extension fallback is 1.4.3002. Evidence stays in this changelog; no raw logs, review bundles or downloaded research binaries ship.

## 0.3.1 - 2026-09-13

- Add **Needs attention** with one retained item per website, occurrence count, next retry time, full technical details, authenticated shared-session WebView and an explicit verification retry. Confirmed MangaFire WAF/shape CAPTCHA and failed Cloudflare verification pause website work across language variants. Plain HTTP403/521, rate limits, setup-schema failures and solver health timeouts do not become invented CAPTCHA diagnoses. Preserve the extension's existing supported first attempt and request limits; no new solver or cookie manipulation.
- Retry paused verification once per hour even without an open browser. Reserve one owned probe before network work, persist its deadline across restart and catalog reset, recheck queued requests after admission, drain probes during maintenance/shutdown, and keep a failed/cancelled/incomplete probe paused. A newer challenge cannot be cleared by an older success. Known paused image routes are gated too; other sources and unrelated CDN images remain usable. Repeated local deferrals do not create duplicate reader/browser/import error records. Error exports include assistance details.
- Fix the supplied 0.3.0 export's MangaFire setup failure: pinned Suwayomi v2.3.2243 uses `LongString`, not `Long`, for source IDs. Keep full 64-bit IDs as strings. Validate the real client operations against an independently derived pinned schema and confirm the preference after updating it. Transient source-preference failures retry even when Cloudflare setup is already ready; unsupported switches do not loop. Solver health timeouts now identify the two-second `/health` probe without claiming every website requires CAPTCHA.
- Matrix/Subbed return available entry metadata before chapter aggregates; coverage fills in separately through a bounded two-worker read lane so coverage requests cannot consume all metadata workers. Preserve existing coverage during refresh, report missing/failed coverage honestly, and reject superseded results after manual decisions. The original full Matrix API response remains the default for other clients. Initial Subbed reads do not wait eight seconds for remote membership; saved membership stays visible while it refreshes.
- Render long tables in bounded, adaptive batches. First entries appear immediately; subsequent batches yield without a fixed 16 ms delay. Updates keep the previous table attached until the replacement is complete, preserve expanded source details and the document row anchor, and discard superseded filters. No nested scrolling window or virtualized row restriction is introduced. Unchanged duplicate snapshots update automation status without rebuilding the review desk; large duplicate JSON encoding moves off the HTTP loop.
- Chapter refresh ingestion and reconciliation run as one owned origins transaction outside the HTTP loop. A writer rechecks the exact live source binding and deleted-entry history before committing; detached/deleted work cannot recreate chapters. Cancellation drains admitted writes, failed reconciliation rolls back, and page-cache invalidation/color follow-ups happen only after commit. Refresh metadata keeps current manual flags/roles; automatic identity metadata, edition-label follow-ups and title consensus use conditional updates so a concurrent manual title wins. Source concurrency, timeouts, request pacing and matching thresholds are unchanged.
- Cards now use accent-colored surfaces throughout all ten tabs, including Status metrics, Settings headers, Tasks and source extensions. Resolve older `!important` overrides that kept those cards neutral. Dual/Aurora use the secondary fill for counterpart/evidence/automation cards. Settings → Customization adds **Card backgrounds: Accent color / Neutral**, independent of theme style and saved per user. Existing color pickers and strength controls adjust the fills; contrast is derived for both card roles.
- Browser reloads retain cover URL versions and bounded per-cover revision maps for that tab, allowing the existing six-hour image cache to work. Explicit cover refresh/reset still changes the URL. Cover freshness checks use one filesystem stat instead of two. Diagnostics text exports include the last 120 browser request/render timing samples; request labels omit query values and numeric entry IDs. This separates request waits from table rendering on the deployed server without adding a telemetry service.
- Compared on the same generated 10,000-entry/451-subscription catalog: full Matrix/Subbed response about 550 ms versus metadata-first about 39 ms; full chapter coverage remains equivalent and follows separately. A controlled 600 ms origins writer delayed the event-loop heartbeat about 639 ms before versus 12 ms afterward. With actual shipped UI code in jsdom at 500 entries/2,000 matches, Matrix first entries 1,239 → 118 ms; complete table 1,269 → 1,295 ms. Subbed first entries 323 → 21 ms, complete 330 → 308 ms. A fixed 16 ms batch delay took 2,462 ms for Matrix and was rejected. Actual HTTP/gzip checks measured full Subbed 569 ms versus metadata-first 48 ms, or 52 ms while both coverage workers were held busy; authentication and reset admission remained intact. These measure local fixture work, not native browser paint or a guaranteed improvement on the user's server. No second database or Redis service was needed.
- Validation: **985 pytest tests pass** (60 new cases), full project checks and eleven-tab DOM/theme/request tests. Test-only `graphql-core==3.2.6` is in `autosourcarr/requirements-test.txt`; production dependency pins are unchanged. The independent source contract is a minimal fixture derived from the [pinned Suwayomi scalar](https://github.com/Suwayomi/Suwayomi-Server/blob/v2.3.2243/server/src/main/kotlin/suwayomi/tachidesk/graphql/server/primitives/LongAsString.kt), not live introspection. Docker/JVM/live hourly scheduler/source verification, actual CAPTCHA solving and APK/JAR compilation were unavailable; managed browser preview remains blocked, so native rendering and deployment speeds are unverified. Not deployed.
- Upgrade: overlay this complete project, retain both databases, integrated source data, configuration and signing keys, then Save/Redeploy normally; automatic image builds remain enabled. Refresh the browser to load the new versioned assets. Source-host does not need to be restored on the working integrated deployment. Extension fallback version is 1.4.3001. Delivery uses the ordinary ZIP link; this project cannot disable ChatGPT's file-preview screen and changing the archive folder structure does not control it. Evidence stays in this updating changelog; no scratch benchmarks, logs or review bundles are packaged.

## 0.3.0 - 2026-09-13

- The supplied 0.2.98 export confirms the integrated source process is ready with no restarts. Its 14 MangaFire CAPTCHA records represent 42 occurrences. Automatically enable MangaFire's supported `solve_captcha` switch by discovering its actual position/type and confirming the result; retain other preferences and the extension's existing rate limits. Source WebView remains the fallback when verification fails.
- Check the existing configured FlareSolverr before allowing automatic Cloudflare work, cap attempts to 30 seconds within the client budget (including JVM overhead), and retry unavailable setup at most once per minute. Preserve the solver URL, session, cookies and intended enabled state across guarded disables/restarts. Status shows verification capability and a retry-setup action. Generic Cloudflare still requires configured FlareSolverr; KCEF alone does not solve it and no solver service is silently installed.
- Browse can reuse the same source-ranked saved feed when only page size changes; it never mixes source filters, sorts, queries or upstream pages. Failed/all-cooling-down refreshes preserve good feed IDs. Cached reads use the existing dedicated catalog lane. Subbed restores a server-scoped display snapshot after restart, marks it refreshing and still uses live membership for import/reset decisions. Successful empty snapshots remain authoritative.
- Replace duplicate-producing chapter-count joins with an indexed EXISTS query. Continuous discovery, reader page storage and main-library mapping run in owned workers so SQLite waits do not block the HTTP event loop. Cancellation drains owned writes. No source-worker limits or matching thresholds were increased; HTTP 429 pauses immediately and honors Retry-After. CAPTCHA, access-denied and origin-outage messages remain distinct.
- Group identical retained errors by source, operation, cause/status and resolved state, with occurrence/affected-request counts. One successful chapter does not hide other failures; raw records remain available in text exports. Health preserves grouped diagnostics and affected-source WebView links. Browse coalesces repeated requests and rejects stale responses; unchanged Matrix data/duplicate results no longer rebuild the same large DOM.
- Reverted delivery to the normal ZIP link format requested by the user. Internal archive folder structure does not control ChatGPT's preview behavior. Keep the complete project, functional tests/scripts and this single updating changelog; no raw logs, audit folders or versioned review reports are packaged.
- Measured on the same synthetic 10,000-entry/451-subscription catalog: chapter statistics median 341.0 → 60.0 ms; equivalent complete Matrix JSON 821.3 → 531.8 ms. Under controlled worker contention, cached Browse 1,023.7 → 20.9 ms. With a 600 ms SQLite writer, continuous-ingestion heartbeat delay 634–637 → 11–12 ms. A covering-index alternative did not improve consistently and was rejected. These are local fixture measurements, not claimed server speedups.
- Validation: **925 pytest tests pass** (36 new cases beyond 0.2.98), plus the full project gate for syntax, version alignment, Compose/env wiring, retention and extension validator fixtures. The historical version assertion now checks the current release instead of assuming every release starts with 0.2. DOM checks cover ten tabs, themes, popup dismissal, thumbnails, source WebView links and errors. Limits: Docker/JVM/actual challenge solving and APK/JAR compilation are unavailable here; managed browser preview is blocked by ERR_BLOCKED_BY_CLIENT, so DOM checks are not rendered-browser verification. Not deployed.
- Upgrade: overlay the source into the existing project and use TrueNAS Apps Save/Redeploy or Stop → Start; normal builds remain automatic. Keep both application databases, source data, config and signing keys. No one-time source-host repair is needed on the now-working integrated deployment. Extension fallback mapping is 1.4.3000; the Status APK button still requires a real current-version build with the deployment's existing signing configuration.

## 0.2.98 - 2026-09-13

- Fixed recovery when the old source-host service and data mount have already been removed from saved TrueNAS YAML. `scripts/repair-truenas.py --source-data /absolute/host/path` restores the exact user-supplied retained bind at `/home/suwayomi/.local/share/Tachidesk`; it does not create a replacement dataset. The user's confirmed path is `/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/source-host`.
- Added `--source-user UID[:GID]` to retain the old source process identity when its service definition is gone. README/TRUENAS give the complete command using 568:568 from the previous source service example. File ownership and data contents are not changed. Supplied paths cannot silently replace a different existing mount; missing/empty directories fail before Docker or API changes. New binds disable automatic host-directory creation.
- Recorded the user's direct ZIP-download preference and the confirmed source mount in the updating project context. Release delivery uses the supported native file download endpoint.
- Validation: 35 focused migration tests pass, including nine new cases for removed-service recovery, exact mount/UID restoration, invalid/conflicting paths, missing/empty data and CLI preview/apply forwarding. **889 pytest tests pass**, and all project checks pass; Docker checks are explicitly skipped because Docker is unavailable here.
- Limits: no live TrueNAS/Docker deployment performed here. Applying the host helper restarts the entire Custom App stack; existing databases/configuration remain retained. All 0.2.97 runtime, UI, popup and dual-accent changes are preserved.

## 0.2.97 - 2026-09-13

- Investigated both new logs and reproduced missing-runtime amplification: 437 chapter-page requests across 146 chapters in 1.895 seconds; 145 chapters retried three times. The export reports no bundled runtime in the running container. Main Suwayomi and translation are healthy. This is a deployment/runtime failure, not evidence that all source websites failed.
- Integrated-runtime readiness now defers engine-bound requests locally with a typed retryable error. Reader/catalog endpoints return HTTP 503 with Retry-After. Fresh/stale page maps and direct external images remain usable; local unavailability does not cause website cooldowns, repeated candidate connections or persisted invalid chapter mappings.
- Shared engine failures use one pinned diagnostic with a repeat count instead of hundreds of per-chapter records. Active root causes remain visible through history floods; resolved entries are evicted first. Browser errors now display readable JSON details and retain the actual HTTP status/full response in exports. Image-version provenance distinguishes a baked image from a legacy code mount.
- Added the host-only `scripts/repair-truenas.py` helper. Default mode previews; `--apply` builds, privately backs up the original complete Custom App configuration, narrowly migrates the existing service and source-data mount, preserves the old numeric source UID/GID, checks concurrent edits and updates through the installed TrueNAS API client. It removes the old source-host only when safe to identify, preserves unrelated services and retained data, and never initializes an empty replacement dataset. Applying restarts the entire Custom App stack. No Docker-control privileges are added to the application.
- Errors and existing modal backdrops close on outside click. Inside clicks and selection drags remain open; Escape closes only the top dialog. Toolbar and source-language popup menus also dismiss outside/Escape. Inline review details remain open. Error diagnostics retain focus trapping and restore prior focus; closing the last dialog releases page scrolling.
- Confirmed Dual accent previously styled Duplicates and one Imports label only. It now has consistent roles across all ten tabs: primary selection/actions, secondary evidence/source details/supporting panels/automatic tasks. Secondary tint responds to Color strength, and Settings previews it. Semantic success/warning/error colours and other theme styles remain intact.
- Validation: **880 pytest tests pass** (36 new runtime/repair cases), plus new executed JavaScript error-status/root-priority checks. Full project checks pass. Ten-tab DOM checks cover outside/inside/selection-drag clicks, nested Escape and scroll restoration, popup menus, HTTP detail/status, primary/secondary colour strength, per-user theme/thumbnail persistence and palette contrast. All 26 host migration cases pass, and the actual previously delivered legacy TrueNAS example transforms with source UID/GID 568 retained. Docker/API fixtures are distinguished from live execution below.
- Limits: no live Docker/TrueNAS/JVM/WebView deployment or APK/JAR build here. Managed browser navigation remains blocked by ERR_BLOCKED_BY_CLIENT; DOM/colour checks do not establish rendered visual fidelity. The archive is not deployed. README/TRUENAS provide the one-time host repair and normal automatic-build update workflow. No raw logs, research folders or versioned review reports are packaged.

## 0.2.96 - 2026-09-13

- Automatic image builds are part of Compose deployment: stable `autosourcarr:local`, `pull_policy: build`, and an absolute host build context in TrueNAS examples. After one-time YAML migration, future updates need only a source overlay and Apps Stop → Start / Save / Redeploy. Normal `docker compose up -d` also builds; a plain container restart and TrueNAS registry Pull Images do not update local source.
- Fixed the 0.2.95 Python-only startup guard that caused the uploaded restart loop. A legacy image now starts the UI with an explicit setup-required source engine state instead of exiting. It does not silently use the removed external source host. The integrated image includes the startup wrapper and uses its baked Python environment.
- Moved VERSION and application copies after dependency installation in the Dockerfile, preserving the pip build cache for version/code-only updates. Added a small build-context ignore file. The old manual builder is optional and tags the same stable image Compose selects.
- The supplied log proves the existing container lacked the integrated runtime even though building `autosourcarr:0.2.95` succeeded. One-time fix: use the updated service definition/build context, remove legacy startup/code/dependency overrides, retain the existing source-data mount and deploy. Keep the main reader, translation service, application databases, source database, config and signing keys. README/TRUENAS contain the exact sequence.
- TrueNAS normal deployment behavior was checked against official 25.04.2.6 and 25.10.7 middleware and Docker's build policy. No host-control socket, updater container or external deployment was added. The application cannot replace its currently selected Docker image by itself.
- Validation: **844 tests pass** (7 new deployment cases); full project checks pass for syntax, YAML/build/env wiring, retained behavior, version mapping and synthetic extension validators. The real startup wrapper and application lifespan were exercised on a controlled Python-only fixture: UI, liveness, preferences and setup-required engine status are accessible; network transport rejects source calls and no package download is needed in the fixture. Optional builder invocation and dependency-layer ordering are covered.
- Limits: no Docker daemon/TrueNAS host is available here, so the complete new image build and JVM/WebView startup remain unverified. The user's supplied log verifies the prior 0.2.95 image built successfully, not that it ran. No live deployment or APK/JAR SDK compilation was performed. UI/layout/theme and matching behavior from 0.2.95 remain unchanged; no raw verification/review files are packaged.

## 0.2.95 - 2026-09-13

- Removed the remaining per-tab width caps in Browse, Duplicates, Imports, Manual actions, Status and Settings. Every tab now uses the available width; internal card spacing remains. Split navigation, gear-only Settings and top diagnostics are retained.
- Added independent 50–200% main-cover and matched-Matrix-thumbnail controls, larger defaults, live previews and reset. Sizes persist per user and do not depend on row density. Imports now shows lazy cover thumbnails beside the left-hand titles; unavailable covers have an explicit placeholder.
- Cached covers bypass database metadata lookups. Uncached cover queries use a separate bounded two-worker lane. Fingerprint cache/hint reads and writes run off the HTTP event loop; cancellation still drains writes. Cover recovery keeps task scheduling on its owner loop.
- Integrated mode is now the application default. A missing bundled runtime is a setup-required error without a restart loop or silent external fallback. Engine status and error exports include mode, effective URL and runtime availability. The legacy startup wrapper uses baked dependencies on the integrated image and fails clearly on a Python-only image instead of installing packages for an impossible integrated boot.
- The supplied boot log spent 51.63 seconds installing packages and reached Uvicorn in 54.59 seconds; ASGI startup itself took 17.6 ms. That deployment was still Python-only and used the old source-host URL. The later DNS errors followed removal of that external container. Translation also had its own connection failure; it remains a separate service.
- Upgrade: **build and use `autosourcarr:0.2.95`**, replace the legacy Python service definition and reuse the existing source-data mount. Remove old install commands, `/app` and `/deps` overrides. Stop the old source-host before the integrated process opens its database. Keep the source dataset, both application databases, configuration, translation and main reading Suwayomi. README/TRUENAS contain the exact steps; copying files or restarting an old image is insufficient.
- Validation: **837 pytest tests pass** (8 new regression cases), full project checks pass, and ten-tab DOM checks pass with all six theme styles, primary/secondary colors, independent size save/restore/user switching, import thumbnails, full-width computed tab styles, WebView targeting and error export. Automatic duplicate completion/error setting writes and fingerprint selection are also offloaded.
- Same-workload comparison: a 350 ms cover-hint storage delay blocked the old event-loop heartbeat for 350.4 ms; it now takes 10.6 ms. A real one-second SQLite writer blocked the fingerprint path heartbeat for 1,032 ms; it now takes 12.0 ms while the same fingerprint still persists. These measure responsiveness, not upstream download speed or the complete user's delay.
- Limits: Docker is unavailable and managed browser navigation is blocked. The actual integrated image/JVM boot, rendered visual/mobile behavior, live TrueNAS/Authelia/source/translation connectivity and a real SDK APK/JAR build remain unverified. Not deployed. No versioned review files or raw logs are included.

## 0.2.94 - 2026-09-13

- Removed the 2100px page-width cap and reduced outer workspace padding. Restored left/right navigation groups; Settings is a gear-only accessible button. Natural page scrolling and top-level diagnostics remain.
- The primary picker is always visible and reflects the active preset/custom color. Filled actions retain the exact selected color; text shades adjust for readability. Added a persisted 10–50% color-strength slider across all six theme styles.
- Bundled the pinned Suwayomi v2.3.2243 JVM/KCEF source engine in the AutoSourcarr image. It starts asynchronously, runs under the existing source dataset owner, reports readiness/restarts, and stops with a process-group grace period. Compose removes the separate source-host service and reuses its data mount. Cached engine asset URLs are redirected to the integrated runtime without changing website URLs.
- Added per-source and Status WebView buttons plus affected-source error actions. The fixed HTTP/WebSocket bridge uses a scoped HttpOnly session, checks origins and preserves the real source engine cookie session. One active WebView is supported.
- Repeated confirmed chapter HTTP403/429/521 failures wait briefly per candidate, preserving alternative sources and stale cached pages. Diagnostics retain the precise cause and failed-source context, including when cached pages are served.
- Upgrade: build with `bash scripts/build-integrated-image.sh`; stop the old private source-host before mounting its existing data into the integrated container. Keep config, both AutoSourcarr databases, source data and signing keys. See TRUENAS.md for ordered migration/rollback steps. The main reading Suwayomi remains.
- Validation: **829 tests pass**; full project checks pass (Docker checks explicitly skipped because Docker is unavailable). Real controlled child start/stop, authenticated HTTP/WS relay, scoped-session/origin guards, source-data ownership selection, cached-URL migration and chapter fallback/retry regressions pass. Full application DOM checks cover all ten tabs, split navigation, active picker values, strength/save/restore and affected-chapter WebView links. 126 palette/strength combinations pass text/action contrast checks. No live Docker/TrueNAS/KCEF execution or APK/JAR build in this workspace. Rendered-browser preview is blocked by ERR_BLOCKED_BY_CLIENT; DOM/controlled upstream fixtures do not establish live visual fidelity or website access. Container consolidation does not establish a source-request speedup or resolve upstream CAPTCHA/403/521 by itself.

## 0.2.93 - 2026-09-13

### Layout, customization and diagnostics
- Fix the header's icon-only Settings width and remove its unnecessary horizontal scrollbar. Keep the error/health button at the top. Navigation wraps at narrower widths.
- Remove capped vertical scroll windows from Subbed, Match matrix, Sources, inline inspectors, chapter lists and review queues. Wide tables retain horizontal overflow; diagnostic traces and true dialogs remain bounded.
- Replace text-heavy Duplicate/Import cards with labeled comparison fields, larger covers, expandable summaries/evidence and explicit decision controls. Imports show one selected candidate, remember it per run/item across polling, and retain search drafts and existing decision/request race protections.
- Add independently selectable Neutral, Tonal panels, Dual accents, Paper & ink, Graphite and Aurora styles. Retain six preset accents; add a primary color picker alongside HEX/RGB and a secondary picker/HEX for Dual/Aurora. Save style/colors per user with the existing revision protections. Derive readable text/action/status colors and theme nested surfaces, controls and badges.
- Diagnostics exports all retained backend and current-browser errors, including resolved history and complete retained technical fields. Failed refreshes produce an explicitly partial cached export. Common credentials, including the reader-key header, are redacted. Backend history remains bounded and resets on restart; exports cannot recover cleared or previously discarded records.

### Current-version Android download
- Status checks APK availability independently of dependency health. The existing external SDK builder validates the compiled package/version and signature, then atomically publishes a hash-bound APK through the already shared persistent configuration mount.
- Admin-authenticated downloads reject stale, missing or modified artifacts; return no-store Android attachments; and bypass redundant synchronous gzip compression. The browser rejects HTML sign-in pages instead of saving them as APKs.
- Build once from this updated project using `bash extension/build.sh`, then refresh Status. No precompiled APK is included: a genuine build requires your public reader URL, reader token and persistent signing identity. No SDK, Docker socket, service replacement or new production dependency is added.

### Upgrade and validation
- Overlay the complete project while preserving active env files, both databases, data and signing keys. Rebuild/redeploy the backend and refresh the browser. No database reset or reader reinstall is required. Keep the private Suwayomi source host; source execution has not moved into AutoSourcarr. Version mapping: 0.2.93 -> 1.4.2093.
- Validation: 813 pytest tests pass (12 new test cases). Project checks pass for syntax, retained behavior, Compose/env wiring, version mapping and synthetic APK/JAR validators; Docker validation is skipped because Docker is unavailable. Behavior checks include 198 style/color combinations at 4.5:1 text contrast; ten-tab DOM interactions, theme persistence/user switching, color pickers, export/download flows, candidate selection and existing concurrency regressions. APK publisher/signature-tool tests use synthetic fixtures.
- Rendered browser access is blocked by this environment. Actual visual fidelity/mobile rendering, live TrueNAS/Authelia/source workflows and a real SDK APK/JAR build remain unverified. Not deployed. Release notes/evidence stay in this changelog; no versioned review reports, logs or patches are packaged.

## 0.2.92 - 2026-09-12

### Compare Desk interface
- Apply the approved dark workspace design across Browse, Subbed, Match matrix, Duplicates, Manual actions, Sources, Status, Imports, Tasks and Settings, with a shared activity strip and focused Diagnostics.
- Duplicate reviews use a queue and side-by-side evidence; unrelated decisions retain independent request ownership. Imports render one selected workbench and preserve search drafts by import run/item. Submission feedback distinguishes pending requests from accepted library changes.
- Subbed shows cached chapter coverage, known library/download counts and per-source gaps without inventing aggregate missing chapters. Source-record controls, manual decisions, chapter provenance and matching thresholds remain intact.
- Consolidate historical theme rules into one neutral-surface palette. Six accents, custom HEX/RGB and density settings update the live preview and save per user. Very dark custom colors receive a readable derived control accent. Additional surface/multicolor themes remain proposals.
- Sources adds persistent repository management and read-only manifest previews, including Keiyoushi. Changes preserve installed extensions and require a Custom App redeploy/recreation to apply the saved environment configuration. The private Suwayomi runtime remains in place.
- Task snapshots identify manual and automatic origin explicitly. Diagnostics separates active issues from resolved history and keeps complete source messages/traces in collapsed technical details.

### Upgrade and delivery
- Overlay the complete project while preserving active config env files, both databases, data and extension signing keys. Rebuild/redeploy the backend and refresh the browser. No reset or reader reinstall is required.
- No new production dependencies or database schema changes. Version mapping: 0.2.92 -> 1.4.2092. Release evidence and limits are kept in this updating changelog; versioned review/audit files are excluded from the archive.
- Validation: 801 pytest tests pass (37 new tests, including a wrapper executing eight workspace JS scenarios). Full-DOM interaction checks with synthetic APIs cover all ten tabs, six presets, custom extremes, preset reset, save/reload, three user identities, import drafts, task filters, source repositories and inspector controls. Accent contrast passes 4.5:1 against the control surface for every preset and 64 custom RGB samples. This is behavior/DOM verification, not a rendered-browser inspection.
- Project checks pass for syntax, deployment/env wiring, retained feature guards and synthetic APK/JAR validator fixtures. Docker is unavailable. The rendered browser preview was blocked by the environment; mobile spacing, actual visual fidelity, live TrueNAS/Authelia/source workflows and compiled APK/JAR builds remain unverified. Keiyoushi's actual gzip-file manifest format was checked read-only; installation into a running source host was not performed.
- Compose files, env templates, dependency pins and reader Kotlin source are retained from 0.2.91. No deployment or data reset. Additional theme concepts are not bundled or installed.

## 0.2.91 - 2026-09-12

### Manual actions and responsiveness
- Manual duplicate reviews no longer wait for complete automatic duplicate scans. Shortlisting and review scoring run separately from the catalogue mutation lock; automatic pairs recheck current identities, manual decisions and direct-evidence lineage under the lock immediately before committing.
- A review updates the cached candidate list immediately and requests a background refresh. It no longer rebuilds all candidate scores or scans every persisted Browse feed after each click. Feed reads resolve merged IDs lazily; bulk maintenance can repair the persisted IDs once.
- Manual entry refresh, missing/additional discovery, Redo and manual-match hydration share bounded manual capacity independently of automatic jobs. The existing source-host worker limit remains enforced. Conflicting entry commits and main-library reconciliation stay serialized for integrity.
- Explicit discovery skips the browser-idle wait, including the direct HTTP path that previously waited for its own request to finish. Automatic work retains its cheaper search limits and idle admission.
- Browse cache reconstruction, local matching/scoring, source inventory, ingestion and persistence are offloaded from the HTTP event loop. Per-record ingestion remains serialized with imports and preserves background follow-ups. Slow storage may still delay that Browse response, but other HTTP requests can continue.
- Automatic duplicate identity preparation also runs outside the HTTP loop and stops checking missing identities once its per-pass budget is filled.

### Reader retries and interface fixes
- Simultaneous readers of one virtual chapter share a single page lookup and receive separate response objects. Cancellation of one reader preserves the others; final-reader cancellation and shutdown drain actual work, including repeated cancellation during cleanup.
- CAPTCHA failures back off for 30 seconds per source chapter in a bounded in-memory cache. Alternative sources and stale page maps remain eligible; the cache expires for retry and clears on database reset. A CAPTCHA still needs source verification in Suwayomi WebView.
- Chapter-page failures have chapter-specific error records. A healthy GraphQL host check no longer incorrectly clears them. Responses expose Retry-After and a short action message; original source traces remain in technical details.
- Duplicate review controls disable only decisions sharing an entry with pending work. Unrelated pairs can be reviewed concurrently. Both direct and evidence controls use the same state; stale/out-of-order responses cannot restore confirmed decisions or reopen an inspector the user left.
- Persisted duplicate snapshots are checked against current decisions and live IDs on restart, preventing old cache writes from restoring dismissed or merged pairs.

### Delivery and verification
- Release notes, concise validation results and limits are consolidated here. Versioned REVIEW/VERIFICATION files, audit folders, raw logs, patches and release manifests are omitted from the ZIP. Regression tests and functional build/deployment scripts remain included. No generated UI concept files are installed in the application.
- Final `bash scripts/check-project.sh` passes, including 764 tests and wrappers executing 22 JavaScript behavior scenarios. Version checks, deployment-file checks and synthetic APK/JAR validator fixtures pass; Docker validation is skipped because Docker is unavailable. The offline application lifespan/API smoke passes. Reproductions and independent checks cover manual/automatic concurrency, stale decisions and restart, Browse/automatic preparation responsiveness, and reader cancellation/cooldown/fallback.
- Existing API paths, matching thresholds, edition gates, subscriptions, manual groups/titles, chapter provenance, reader authentication and deployment env paths are retained. Dependency pins, Compose files and reader Kotlin source are unchanged from 0.2.90. Version mapping: 0.2.91 -> 1.4.2091.
- No production deployment or data reset. Live TrueNAS, Authelia, upstream manga sources, rendered browser QA and actual APK/JAR builds remain unverified. UI image concepts are proposals, separate from the implemented responsiveness fixes.
- Upgrade: stop the backend, back up project/config/databases, overlay project files while retaining live env/data/signing keys, restart/rebuild the backend and refresh the browser. No reset or reader reinstall is required. Old reporting files left by previous overlays may be removed; an overlay does not delete them automatically. Rollback restores previous project files while keeping current data.

## 0.2.90 - 2026-09-12

### Recovery scheduling and visible progress
- Fix eight automatic source-work paths that acquired shared capacity before waiting for browser idle. Idle work no longer prevents explicit repair, refresh or metadata actions from acquiring capacity.
- Give the coalesced Subbed repair/search job admission independent of bulk catalogue maintenance. Each actual source call still uses the private client's reserved background capacity; foreground slots remain available.
- Explicit priority/exhaustive recovery skips automatic browser-idle waits, reports the active source/wave and marks queued versus executing tasks honestly. Source attempts follow the configured background capacity rather than overfilling its admission queue.
- Bound complete source recovery to 45 seconds and each Subbed item to 120 seconds by default. Keep committed records, drain owned work, advance to later items and label partial completion. Both limits are editable settings; existing env files use defaults.
- Retry orders sources by prior attempt time, visiting untouched sources first. Interrupted source attempts are recorded as deferred so a shorter item deadline cannot repeat the same first source indefinitely. Local limits and cancellation do not trigger source cooldown.

### Startup and ongoing responsiveness
- Resolve historical merge alias chains with memoized paths, query only referenced entry IDs, and index merge history. Preserve existing live IDs, history recovery, cycle/broken-alias cleanup and canonical reader identities.
- Capture missing/reassigned manual bindings in bounded transactions. Already-correct bindings and manual titles retain their payloads/timestamps; interrupted manual-group creation is still repaired. Add sparse manual-source indexes after legacy columns migrate.
- Status polls share one owned metrics scan, cache fresh results for 15 seconds and serve older results during refresh. A timed-out request no longer leaves another competing filesystem worker; reset/shutdown drains the real worker.

### Interface and release
- Start Browse and task polling independently of remote preferences and source inventory. Report Browse/source-list failures visibly while preserving prior cards and retry controls.
- Preserve preference edits made while an older preference GET is in flight, including edits already saved. Reconcile late server page-size preferences after the active Browse request finishes, using current filters.
- Unfiltered Browse uses broad-source semantics, preserving source cooldown; explicit filters still request their selected sources.
- Show ETA after the first completed item and age of the last task update. Preserve cancellation, newest-first history and the existing three-column task layout.
- Include project contributor/skill guidance in AGENTS.md, new recovery/startup/status/UI regression evidence and the performance/opportunities report. Version mapping: 0.2.90 -> 1.4.2090. Readers retain their API/source identity; no reset or reader reinstall is required.
- No new external services or dependency-pin changes. Builders are included; this source release does not contain newly compiled APK/JAR binaries. See CHANGELOG.md for fresh results and verification limits.

## 0.2.89 - 2026-09-11

### Import correctness and safe cancellation
- Retain the recovered 0.2.88 fixes and apply the existing identity/edition gate to exact-title import shortcuts, including the existing-library index and legacy lookup. A Manga/Webtoon conflict no longer becomes an automatic match merely because the title is exact. Exact original source URLs keep precedence; rejected choices remain rejected.
- Add an additive `decision_revision` column. Match, Retry, Skip and Reject share revision ownership; late terminal writes and cancellation restoration cannot overwrite a newer decision, including a replacement with the same transient state. Cancelling a replacement restores the last settled snapshot rather than an abandoned pending action.
- Cancel obsolete manual row actions as well as automatic discovery. Check foreground and automatic decisions after waiting for the main-library lane and before upstream mutation. Add and Reject use the same lane; recheck other accepted import references immediately before rejection cleanup.
- Keep automatic terminal writes conditional on the captured revision and do not count superseded work as successfully auto-matched. Existing subscription provenance protections from 0.2.88 remain intact.

### Source recovery and interface fixes
- Central source health accounting ignores local admission deferrals and shared batch expiry. A source that never started no longer gains a failure/cooldown from a global deadline. Genuine upstream failures still count; local deferrals remain visible as retryable diagnostics.
- Import search rejects superseded queries, old views and obsolete automatic-match responses. Empty import refreshes clear stale cards and update pagination.
- Preserve unsaved main-title drafts after the input loses focus. Matrix Enter-to-search calls the correct loader and handles failed requests.
- Chapter details and served history ignore responses from older requests or previous inspector generations.

### Release validation
- JAR structural validation accepts relative paths and uses standard unzip when available, retaining a JDK fallback. It reads class bytes into an owned temporary filename and does not claim installation compatibility from a structure check.
- Retain prior regression assertions; test fake adapters accept the optional decision token. Add 42 new regression cases. Fresh integrated result: 704 passed. See CHANGELOG.md and CHANGELOG.md for exact scope and limits.
- Version mapping: 0.2.89 -> 1.4.2089. No reset, backup re-upload or reader reinstall is required. Keep active configuration, databases and signing keys. No new APK/JAR binaries are included.

## 0.2.88 - 2026-09-11

### Search responsiveness and outcomes
- Restore the ignored overall live-search budget. Direct, fallback and translated alias requests share one cap; completed results survive expiry, and unstarted requests are not counted as attempted. Per-request admission/execution clocks remain separate. Budget exhaustion is a local deferral and does not trip a source failure circuit.
- Drain bounded source workers through repeated cancellation, protecting cleanup from a second Cancel/reset/shutdown signal.
- Manual search displays deferred/budget-limited work distinctly from a confirmed miss, shows attempted live-query counts, preserves cached/partial results and rejects obsolete responses for a newer search of the same entry/source.

### Import decisions and library protection
- Release Match/Retry pending counts and runtime keys via completion callbacks, including cancellation before the coroutine starts. Restore the previous row/snapshot/provenance on cancellation; preserve a newer Skip state.
- Track automatic discovery per import row. Skip, Match and Retry cancel obsolete row work without cancelling the rest of the import run. Automatic completion also uses an atomic state guard so a late database write cannot overwrite a manual decision.
- Classify pre-existing main-Suwayomi membership from live materialization results (with complete-library fallback for older responses), instead of treating a missing Subbed cache as absence. Serialize main-library reconciliation so concurrent imports do not both claim a new addition. Bulk and manual resolution use the same path.
- Persist verified new additions in an additive import-history column. Existing history defaults to unverified; Reject remembers the rejection but preserves a historical subscription without verified importer ownership. Existing imports remain protected, and confirmed new additions retain the existing rejection cleanup path.

### Verification and compatibility
- Retain all 640 previous tests, updating one source-text assertion to the tracked row task while preserving its 180-second deadline requirement. Add 22 regression cases covering budgets, cancellation, manual-decision races, membership/concurrency, additive migration and executed JavaScript behavior.
- No identity thresholds, edition rules, manual-title scope, reader Kotlin files, environment templates, dependency pins, signing material or Compose configuration changed. No reset or backup re-upload is required.
- Version mapping: 0.2.88 -> 1.4.2088. Builders are included; this archive does not contain newly compiled JAR/APK artifacts. See CHANGELOG.md and CHANGELOG.md.

## 0.2.87 - 2026-09-08

### Responsive catalogue views
- Make import-history GETs read-only: they no longer manufacture/write historical match snapshots while reading, so a background writer does not block the view's avoidable write. Preserve recorded applied snapshots separately from live entry summaries; label missing historical snapshots explicitly.
- Page Imports in groups of 50, applying review filters/counts over the complete selected import/state. Resolve merged/deleted candidate references in batches and hydrate only the visible page. Clamp a vanished last page and distinguish a failed request from a genuinely empty result. The API retains its prior default/cap for external clients and adds optional offset/review fields.
- Run Imports, Matrix/Subbed and summary reads plus response encoding in a separate bounded four-worker catalogue lane, independent of network/default/request-worker saturation. Drain admitted reads on cancellation/reset/shutdown.
- Load heavy hidden tabs on demand instead of preloading both matrices and Imports at Browse startup. Coalesce duplicate requests, reject late responses for older filters, preserve valid views on transient errors, and invalidate import-history requests after a history-changing action.
- Bound cached source candidate decoding/scoring to a title-indexed shortlist (240 records; up to 80 returned). Include active primary and additional records; use exact titles/native aliases before bounded token fallback. Offload ranking from the HTTP loop and reuse native evidence.

### First-source recovery and honest outcomes
- Hydrate the live details of a bounded set of promising thin search hits before final identity verification. Fetch chapters only after acceptance; cache-only or failed-detail results cannot authorize attachment.
- Extend unique exact metadata bootstrap to source-less main-library entries and revalidate older identity links for first-source recovery. Fuzzy/bootstrap ties remain unresolved; provider aliases are search hints, not self-confirming match evidence. Changed native titles invalidate stale bootstrap authority.
- Add Subbed > Repair unmatched for subscribed entries with no active source records. Search all remains broader and prioritizes empty entries. Both share the existing bulk discovery slot; repeated clicks do not queue duplicate work.
- Persist source outcomes: verified match, ownership conflict requiring duplicate review, needs verification, unavailable, deferred, no results, or separate-entry restriction. Do not silently merge established owners. Chapter errors remain errors, not successful empty scans.
- Distinguish waiting for source-host capacity from upstream execution timeouts; local admission defers without incrementing the source failure circuit. Preserve bounded source concurrency, per-source timeout and cancellation cleanup.
- Reload source records in the visible matrix when metadata pulses change source/match counts, rather than updating only the badges. Preserve scroll position and frozen import audit snapshots.

### Restore fixes from both supplied 0.2.85 audit branches
- The supplied 0.2.85 archive and 0.2.86 were based on different audit branches. Reconcile the missing 0.2.85 fixes while retaining 0.2.86 task/queue behavior and the other branch's staged dependency/artifact publishing and reader credential filtering.
- Restore resumable orphan-repair cursor/failed-ID tracking, outage-aware stopping, provider pooling/cooldowns and per-provider error handling; preserve accepted import metadata only against original backup evidence.
- Restore cover generation checks, malformed-sidecar recovery, atomic cache publication and bounded failure backoff. Retain raster/body limits.
- Restore reset admission/drain for foreground catalogue requests, serialized reset/relocation, source preferences, auxiliary task ownership and non-recycled chapter IDs. Tasks/Errors/health remain available during maintenance. An active handler drain timeout aborts reset before data is cleared.
- Restore safe relocation target/marker handling, upstream-response ownership, token Unicode handling and atomic preference saves. The two SQLite databases are not a single crash-atomic transaction; keep backups.
- Refuse dependency cache paths that equal/contain the application, data or config directories. Cache compatibility keys include libc as well as Python ABI and platform; refreshes still stage new packages and preserve old generations on failure.
- Restore the other audit branch's 87 regression cases, keeping all 513 v0.2.86 cases, and add 40 v0.2.87 cases. Retained tests adapt only moved fixture paths, async authorization and lossless escaped line-separator behavior.

### Compatibility and verification
- Preserve existing API route method/path pairs, settings/environment templates, manual actions, strict match/edition gates, source management, reader endpoints and both JAR/APK build paths. No automatic full rematch or destructive migration is added.
- Preserve the previous chronological changelog and regression document. The historical preload-every-tab optimization is intentionally superseded by on-demand loading; import pagination replaces the UI's 5,000-row request, not access to the remaining history.
- Version mapping: 0.2.87 -> 1.4.2087. Reader Kotlin source and signing/token behavior are unchanged from 0.2.86; no reader reinstall is needed for these backend/UI fixes. This source archive contains builders, not newly compiled artifacts.
- See CHANGELOG.md for actual test results, retained feature mapping and deployment limitations. No DB reset or backup re-upload is required. Dependency ABI-key refresh may need package-index access/free disk space at the first restart.

## 0.2.86 - 2026-09-07

### Task visibility and responsiveness
- Exclude read-only duplicate-candidate and entry-summary POST requests, plus self-tracking automatic duplicate scheduling, from mutation task logging. Unknown real admin actions receive their method/path instead of the misleading "Update AutoSourcarr" name.
- Keep authenticated Tasks, Errors and Cancel accessible without an AnyIO synchronous request-worker token. Offload duplicate-snapshot lookups and targeted import/identity ranking, SQLite/cache reads and writes, and hydration persistence from the HTTP event loop.
- Retain serialized import ingestion and schedule its identity/hydration follow-ups back on the event loop. Source concurrency and all automatic identity/edition/rejection gates are unchanged.
- Distinguish RUNNING, QUEUED and WAITING activity while preserving the existing lifecycle API. Show import-run context, separate waiting time and active-only ETA; summarize active/queued/waiting counts separately.
- Coalesce task polling, preserve a visibly stale snapshot on failure and back off after failed requests. Read-only lookups no longer trigger mutation refreshes. Apply every metadata snapshot row instead of stopping after the first changed row.

### Queue lifecycle and diagnostics
- Release the import-discovery pending count in the runtime completion callback, including cancellation before the coroutine first runs. Drain owned translation/metadata/item workers before releasing the import lane, including repeated cancellation and child failures.
- Re-read queued rows before discovery so already resolved/skipped rows are not searched from a stale queue. Preserve partial candidates and report unhandled row errors/timeouts in import cards and Errors.
- Resume only durable, unfinished discovery rows on ordinary startup when no versioned pipeline upgrade is needed. Keep completed candidates/review decisions and explicitly paused rows; do not bump the existing 0.2.84 pipeline marker or trigger a full rematch.
- Explicit user cancellation pauses unfinished rows. Imports show the paused state and do not count it as active searching; Retry unresolved remains available.
- Source version mapping: 0.2.86 → 1.4.2086. No reset, backup re-upload, dependency change or reader-extension reinstall is required for these fixes. See the separate test report for verification and limitations.

## 0.2.85 - 2026-09-07

### Resolver and catalogue performance
- Retain successful metadata-provider results at a batch timeout; cancel/drain the slow provider and retain per-provider diagnostics. Apply the same behavior to backup metadata resolution.
- Reuse one immutable native-evidence snapshot across candidate scoring, avoiding candidate-count-multiplied SQLite reads without weakening identity/edition checks.
- Retry stale/legacy empty import metadata and transient failures with bounded TTLs; preserve accepted provider records during temporary errors. Leave tied distinct import identities unresolved.
- Version provider-query cache keys with Unicode-normalized exact queries and relevant filter/result-limit settings; avoid transliteration collisions.
- Replace one-coroutine-per-input source mapping with a bounded worker pool. Apply HTTP connection limits to the actual custom transport and respect stream-specific timeouts.

### Data integrity and lifecycle
- Fix nested SAVEPOINT/outer rollback behavior and rollback on BaseException, including cancellation. Allow an already-admitted database operation to finish nested work while relocation waits for it; fail closed on corrupt relocation markers or missing copied databases.
- Ordinary database reset preserves source_config preferences/group assignments and clears stale automatic merge provenance.
- Drain source requests, mutation threads, all scheduler families, lifespan maintenance and delayed crawler retry callbacks before reset/shutdown. Cancel task runtimes via their owning event loop.
- Fail incomplete main-Suwayomi library pagination instead of treating a truncated snapshot as complete.
- Avoid forwarding compressed Content-Length when reader pages are decoded while streaming.

### Input/browser hardening
- Escape titles for both JavaScript-literal and HTML-attribute contexts, reject executable metadata-link URLs, handle denied localStorage and remove forwarded abort listeners after requests finish.
- Stream backup uploads with a 128 MiB cap and cleanup on cancellation; bound gzip expansion to 512 MiB and manga rows to 100,000, validate protobuf varints/field numbers.
- Cap buffered image bodies at 32 MiB and raster analysis at 40 million pixels; whitelist raster formats and reject unsupported active/document formats.
- Validate Host headers, use raw routed paths, disable range processing on UI assets, add nosniff and sandbox headers to proxied assets. Update FastAPI/Starlette and Pillow pins; this is targeted hardening, not a full dependency-vulnerability clearance.
- Scope the reader's custom API key to the backend origin on each network hop, including redirects. Requires rebuilding/updating the reader extension to take effect in an installed client.

### Build, deployment and tests
- Include VERSION in the Docker image. Stage/validate isolated ABI-keyed Python dependency generations rather than deleting the working cache before pip succeeds.
- Stage both extension artifacts before replacing published output; retain last-known-good output on failure. Signing identities and reader tokens are unchanged.
- Make backend tests hermetic and correct three historical fixtures/assertions that previously failed or contacted real networks. Add runtime, database, metadata, input and operations regressions.
- Version mapping: 0.2.85 → 1.4.2085. No reset or re-import required. See the separate audit report for test totals, synthetic benchmarks, limitations and remaining priorities.

## 0.2.84 - 2026-09-07

- Fixed **Retry unresolved import** stopping after the original backup source failed. Retry now runs the same bounded, all-enabled-source live discovery used by automatic import review, using both the original title and accepted metadata aliases. The original reference is not searched twice before that fallback. Existing strict identity/edition/rejection gates still decide acceptance; provider/suggestion percentages alone do not authorize a match.
- MangaFire-style CAPTCHA failures immediately open a temporary source circuit (at least five minutes, or the configured longer source cooldown). Further aliases and import rows defer that source while local matches and other enabled sources remain usable. A successful real request clears the circuit; a skipped request cannot masquerade as a successful empty result. Ordinary transient errors now accumulate correctly across health checks toward the configured failure threshold.
- Original-source binding is optional after a safe catalog/alternate-source match: accepted import metadata is applied first, a blocked original source is deferred without fabricating a source record, and the real main-Suwayomi library-add path still runs.
- Source errors have concise messages plus expandable **Source diagnostics** containing the original exception. Existing import rows with embedded Java traces are rendered compactly without a reset. Metadata percentages are labelled **Metadata identity** to distinguish them from verified chapter-source matches.
- Explicit live shortlists now also respect previously rejected import matches before scoring, closing a path that could bypass the normal rejection filter.
- Retry retains concrete live candidates and diagnostics when a search times out rather than overwriting them with an older local shortlist. Retry task outcomes distinguish **Still unresolved** from real failures; cancellation still propagates.
- Requeues unresolved/failed import candidate discovery once through **Upgrade import match candidates** on upgrade. Existing backups/import history, source configuration, data, and manual decisions are retained; no DB reset or backup re-upload is required.
- No third-party extension, CAPTCHA preference, credentials, dependencies, or reader API/Authelia rules are changed. The source-host's MangaFire extension may still require its own challenge setting or supported WebView action.
- Extension version mapping is `0.2.84 → 1.4.2084`; the import fix itself does not require reinstalling the reader extension.

## 0.2.83 - 2026-09-06

- Fixed external metadata resolution for **source-less/title-only entries** such as `Time Roulette`. The identity resolver can now use the entry's existing preferred title as a one-time **exact-only bootstrap** when there is no source/library/manual title evidence and no accepted provider identity yet. Fuzzy preferred-title bootstrapping remains rejected.
- Exact bootstrap results are ambiguity-safe: if one provider returns multiple distinct IDs with the same exact title, AutoSourcarr leaves that provider unresolved instead of picking an arbitrary row.
- Fixed metadata-cache poisoning. Provider timeouts, HTTP errors and rate-limit failures are no longer cached as genuine empty search results; successful empty results now expire after at most 30 minutes instead of the normal multi-day positive-result TTL.
- Added a one-time background **Repair orphan metadata identities** upgrade task. It force-retries existing entries with a preferred title but no active source records and no external identity, so existing databases recover without a DB reset.
- Source-less recovery can now use the original preferred title as the first source-match anchor. Metadata accepted through the exact orphan bootstrap may provide only summary/author corroboration for that first attachment; provider aliases still cannot become automatic source-match authority, preserving the anti-snowball protections from v0.2.66+.
- Metadata identity refresh responses now expose provider errors and ambiguous-bootstrap providers for diagnostics.
- Extension mapping is `0.2.83 → 1.4.2083`.

## 0.2.82 - 2026-09-06

- Manual main-title overrides are now strictly **entry-local**. A source record can no longer carry a manual title into a different entry after a split, merge, rediscovery, reset rebuild, or future match.
- Added frozen `manual_title_scopes`: when a main title is set, AutoSourcarr records only the source records attached at that moment for audit/display. New matches are never appended to that scope and the scope is never matching evidence.
- Duplicate merges no longer inherit a manual title from the losing entry. The surviving entry keeps its own manual title only if it already had one.
- Replaced the **Manual groups** top-level tab with **Manual actions** and subtabs for **Matches / groups**, **Titles**, **Separate entries**, **Rejected chapters**, and **Duplicate decisions**.
- Manual title cards show their frozen source-record scope and manual aliases; separate-entry decisions, rejected chapter candidates, and duplicate decisions can be reversed directly from their subtabs.
- Extension mapping is `0.2.82 → 1.4.2082`.

## 0.2.81 - 2026-08-31
- Completed and failed Tasks now show their finish **date and time** in the browser's local timezone and are explicitly sorted newest-first within each section.
- Added **Search all** to the Subbed toolbar only. It queues a cancellable background pass over every subscribed entry, searches every enabled source using all known names/aliases, and also checks for additional records; Match Matrix intentionally has no equivalent bulk button.
- Added **Full reset** to Status. It clears all AutoSourcarr catalog/relationship/user-decision data (including manual matches/groups/titles/separate decisions, duplicate decisions, imports, chapter rejections and caches) while keeping persistent env/API/signing settings, database location, UI preferences, source groups/priorities/language/search configuration, and the main-Suwayomi library. Subbed anchors are recreated from the main library and searched again from scratch without old source hints.
- Extension mapping is `0.2.81 → 1.4.2081`.

## 0.2.80 - 2026-08-31
- Fixed database-reset Subbed recovery: restored subscription anchors now keep a trusted `main-library` title, all old exact source IDs are independently re-fetched/re-checked instead of stopping after the first accepted match, and changed/missing old records are searched again on their original source.
- Preserves pre-reset canonical alias relationships for main-Suwayomi library URLs, preventing one previously merged subscription from being recreated as several source-less duplicate entries. A conservative one-time repair also reconnects existing source-less subscription clones when exact title plus exact cover/summary proves the same source-backed work.
- Duplicate review now ignores entries with no active source records, so unresolved reset placeholders no longer appear as empty duplicate cards.
- Matrix/Subbed source editing now offers `Separate entry` for a search result or attached record. The decision creates/keeps the source record as its own entry, records a `not_duplicate` review, persists an exact source-record exclusion for the selected entry, and prevents background missing/additional/catalog matching from attaching that record there again. `Allow match` reverses the exclusion, and a later explicit manual match overrides it automatically.
- Extension mapping is `0.2.80 → 1.4.2080`.

## 0.2.79 - 2026-08-29

- Entry chapter cards now render in **three columns on desktop**, two on medium-width screens, and one on narrow/mobile screens. Each card keeps the requested two-line `Chapter / Source` and `candidates / Entry` serving layout.
- Source groups now have one **shared priority**. Changing the group priority updates every source family and language variant in that group atomically, and assigning a source to a group immediately inherits the group priority. Extension/source resyncs also re-apply the shared priority so members cannot drift apart.
- The Sources table now has a dedicated **Group** column. Grouped sources are visually stacked beneath one group cell and one shared priority cell (for example `Scans | Drake Scans / Asura Scans / Hades Scans`), while ungrouped sources remain individual rows. Sources can still be moved between groups from their source row.
- Source-group priorities are persisted in the database, included in reset preference snapshots, and migrated from the best existing member priority on upgrade.
- Extension mapping is `0.2.79 → 1.4.2079`.

## 0.2.78 - 2026-08-29

- Automatic main titles now follow the title used by the largest number of currently bound source records. Primary/additional records each contribute one vote; metadata-provider/generated aliases do not. A manually selected main title remains pinned until explicitly cleared. Existing non-manual entries are refreshed once during the v0.2.78 startup maintenance pass.
- Added **Remove manual titles** to entry controls. It clears the persisted manual main-title override plus manually added aliases, then immediately returns the entry to bound-source title consensus. Added **Manually renamed / Automatic main title** filters to both Match Matrix and Subbed, plus a `MANUAL TITLE` badge on affected entries.
- Official/community status is no longer user-configurable in Sources. Chapter resolution derives official state from source-provided chapter/scanlator metadata (with a conservative inherent-official-source fallback), allowing a single source to expose both official and unofficial chapter candidates. Explicit `Unofficial`/fan/scanlation markers override an official-source fallback.
- Chapter rows in the entry inspector now use the requested compact two-row decision layout: `Chapter … | Source: …` followed by `N candidates | Entry: …`. Reject/restore candidate controls and the detailed candidate inspector remain available when opening a chapter.
- Extension mapping is `0.2.78 → 1.4.2078`.

## 0.2.77 - 2026-08-29

- Fixed an upgrade-startup regression introduced by v0.2.76 source groups: existing databases created before `source_config.group_id` existed crashed during bootstrap with `sqlite3.OperationalError: no such column: group_id`. The bootstrap schema no longer creates the `group_id` index before migrations have had a chance to add the column.
- Source-group migration remains fully in-place and non-destructive. On the first successful v0.2.77 start, AutoSourcarr adds `group_id`/official source fields when missing, creates `source_groups`, then creates `idx_source_config_group`; existing entries, matches, chapters, subscriptions, settings, manual corrections, and catalog data are kept.
- Added a regression fixture that opens a realistic pre-source-group SQLite catalog and verifies that AutoSourcarr upgrades it successfully while preserving its existing source rows.
- Audited other forward-added SQLite columns for the same bootstrap-before-migration failure pattern; `group_id` was the only current schema index that referenced a column introduced later by `ALTER TABLE`.
- Extension mapping is `0.2.77 → 1.4.2077`.

## 0.2.76 - 2026-08-29

- Added persistent **Source display groups**. Create any named group in Sources (for example `Scans group`) and assign multiple source websites to it; Matrix, Subbed, entry source inspection, and Browse source filtering collapse those websites into the named group while preserving the individual underlying source/language records. Groups, membership and per-source official flags survive ordinary source syncs and are restored across AutoSourcarr database resets.
- Added an explicit per-source **Official** toggle, with conservative first-party source auto-detection for newly discovered sources. Chapter resolution strongly prefers official candidates over equivalent community/reupload candidates, while the existing wrong-season protection remains stronger than the official bonus. Unified source metadata also prefers official source records over non-official source records when external identity metadata does not already win.
- Matrix/Subbed chapter rows now show the candidate AutoSourcarr will actually serve: **Source**, source-entry title, candidate count, and official status. Opening a chapter highlights the current **WILL SERVE** candidate instead of presenting an undifferentiated candidate list; the previous served origin remains visible separately as history.
- Added persistent **Reject episode** / **Restore candidate** controls per chapter candidate. A rejected source chapter is deactivated immediately, removed from the current served-origin decision if necessary, and is not resurrected by later source/chapter refreshes. Rejection memory is intentionally kept across database reset because it is a user decision rather than rebuildable cache state.
- Source groups are display/organization units only: they do not erase individual source priority, search, language, chapter, manual-match, or chapter-origin identity. A grouped column can contain multiple underlying source records and `No match` means none of the group's enabled sources currently matches the entry.
- Extension mapping is `0.2.76 → 1.4.2076`.

## 0.2.75 - 2026-08-29

- Fixed source searches such as **Drake Scans** returning a full `fetchSourceManga` stack trace when the upstream source uses HTTP 404 to mean “no results for this query”. SEARCH-mode 404s are now normalized into an empty result page instead of being treated as a broken source.
- A 404 miss no longer trips the source-wide circuit breaker, so AutoSourcarr continues trying the entry's remaining aliases, fallback title variants, translated names, cached catalog candidates, and later background discovery.
- The special handling is deliberately limited to `SEARCH`; a 404 from `LATEST` or `POPULAR` still surfaces as a real source/feed error.
- Manual matching therefore shows normal **No results** behavior for a missed Drake Scans alias instead of **Source search unavailable** plus the Suwayomi/RxJava stack trace.
- Extension mapping is `0.2.75 → 1.4.2075`.

## 0.2.74 - 2026-08-29

- Manual matching is now authoritative ownership: selecting a source manga forcibly detaches that exact source record from any other AutoSourcarr entry instead of merging the two entries. The selected record is then locked to the entry the user chose.
- Forced manual moves also deactivate the displaced entry's chapter candidates and purge aliases/covers contributed by the moved record, preventing stale metadata from making the old entry look as if it still owns the source.
- Manual `replace` and `add` both use the same force-ownership rule, including fast search-card commits and later background hydration.
- A forced move also detaches the source record from its previous persistent Manual group, so a later database reset cannot silently merge it back to the entry the user explicitly corrected.
- Fixed `UNIQUE constraint failed: source_matches.entry_id, source_matches.source_id` during manual/duplicate operations. Merge code now accounts for inactive source-match tombstones because the SQLite uniqueness rule applies to inactive rows too.
- Extension mapping is `0.2.74 → 1.4.2074`.

## 0.2.73 - 2026-08-29

- Automatic duplicate matching is now **hard-paused for the entire Redo/reset relationship rebuild window**. A running automatic sweep is cancelled/cooperatively stopped, duplicate snapshot work is deferred, the Duplicates page shows the pause reason, and one fresh automatic sweep is coalesced after the authoritative rebuild releases the pause. The manual **Run automatic sweep** button is disabled while this pause is active.
- Database Reset now uses the previous catalog only as **untrusted acceleration hints**. AutoSourcarr snapshots exact `(source_id, source_manga_id)` records before clearing rebuildable catalog state, restores Subbed anchors first, re-fetches those source records and re-runs the current evidence gate, then replays the rest of the previous catalog by exact ID under the current matcher. Old automatic bind/merge decisions are never restored merely because they existed before reset.
- Previous-catalog replay now fetches exact source IDs in small bounded background batches instead of performing a fresh source-wide search for each old record. Subbed recovery remains first priority and the background source-host semaphore keeps foreground capacity reserved. This is intended to make large reset rebuilds substantially faster than the old full-search path without weakening validation.
- Added **continuous catalog discovery**. While AutoSourcarr is running it walks enabled Latest/Popular source feeds one page at a time, persists a fair source/page cursor, and keeps cataloging new records. It yields to foreground activity, Subbed reset recovery and imports, pauses under a large enrichment backlog, and uses only the low-priority private-Suwayomi lane. Revisited records are ingested cheaply; expensive translation/missing-source/metadata fan-out is only queued for genuinely new source records. The crawler self-restarts after transient unexpected failures.
- Added **movable database storage** under Settings → Configuration. `catalog.sqlite` and `chapter-origins.sqlite` can be moved to another persistent container mount without creating fresh DBs. The mover stages live SQLite backups, verifies them with `PRAGMA quick_check`, briefly quiesces DB contexts for a final no-write-gap sync, switches the running process live, persists `DATABASE_DIR`, and retains the old files as a safety copy until the next redeploy verifies the new DBs again.
- Separate database paths now fail closed. A missing/unmounted `DATABASE_DIR`, or a separate directory missing either existing SQLite file, stops startup instead of letting SQLite silently initialize an empty replacement catalog. TrueNAS examples document the optional `/database` bind mount required when moving the DBs to another physical pool/drive.
- Database connections now reopen lazily when the active DB path changes. Normal SQLite requests remain concurrent; only the short final storage cut-over blocks new DB contexts. This avoids introducing a global database lock just to support relocations.
- Reduced continuous-background churn by not re-queuing translation, missing-source, variant and metadata enrichment every time the crawler revisits an already catalogued source record.
- Fixed the database-reset duplicate-pause helper so it records the pause reason instead of recursively calling itself.
- Extension mapping is `0.2.73 → 1.4.2073`.

## 0.2.72 - 2026-08-28

- Fixed **Redo entry automation** failing immediately with `Error: no running event loop`. The admin endpoint was a synchronous FastAPI route, so FastAPI executed it in a worker thread while `schedule_entry_automation_redo()` attempted to create an asyncio task. The route now executes on the application event loop, and the scheduler checks/captures that loop before creating its background task.
- Audited the same background-scheduling boundary and moved the other affected admin actions onto the application event loop as well: Reject import match, Refresh feeds, Repair missing metadata, Find extra source records, and Run automatic duplicate matching. These actions can no longer silently queue zero work or fail solely because a sync route was running in FastAPI's thread pool.
- No database reset, Authelia change, reader API change, or signing-key change is required for this update. Existing persistent configuration and APK signing state remain compatible.
- Extension mapping is `0.2.72 → 1.4.2072`.

## 0.2.71 - 2026-08-28

- Fixed **Redo automation** for already-snowballed entries. v0.2.70 could preserve records created by an earlier redo because they were relabeled `split-duplicate`, making subsequent redo clicks appear to do nothing. Redo now re-evaluates every unlocked non-manual attachment, including historical `split-duplicate`, `redo-anchor`, `known`, `import`, and automatic rows. Only explicit manual/locked choices are preserved.
- Replaced the oldest-primary redo anchor rule with a **native-evidence consensus anchor**. Redo scores the attached records against each other, manual/main-library intent and the entry title, so a single early bad match can no longer become the permanent trusted anchor. This specifically targets large mixed entries where several sources agree on the real work while an older unrelated source record happened to be inserted first.
- Redo-created split records now use the internal `redo-anchor` ownership type rather than `split-duplicate`. They remain clean independent anchors, but a future Redo is allowed to re-evaluate them if they are ever merged incorrectly again. Manual record-level splits still use the durable `split-duplicate` type.
- Redo now removes stale generated/source/provider aliases copied by historical merges, then rebuilds aliases only from the source records still attached to each clean entry. This prevents old aliases from recreating the same duplicate snowball immediately after cleanup.
- After splitting, Redo records **clearly unrelated** clean entry pairs as `not_duplicate` while leaving plausible localized/same-work pairs eligible for the current duplicate matcher. This prevents obvious foreign records from immediately merging back while still allowing correct records from different sources to recombine automatically.
- The Redo task now reports the selected clean anchor, split-record count, and number of clearly unrelated pairs blocked from re-merging.
- Extension mapping is `0.2.71 → 1.4.2071`.

## 0.2.70 - 2026-08-28

- Replaced the per-entry **Unbind automatic matches** and **Split / unbind duplicates** controls with one **Redo automation** action in both Match Matrix and Subbed. It preserves manual/locked relationships, splits every automation-owned attachment back to clean native anchors, drops unlocked provider identities and stale automatic-merge lineage, then reruns source discovery, metadata/chapter refresh and the current strict duplicate matcher. Correct relationships can therefore be rebuilt while historical snowball mistakes are separated instead of requiring several manual cleanup steps.
- Redo treats unlocked `known` records as automation-owned too. A later browse/refresh can relabel an originally automatic source row as `known`; the redo therefore keys preservation on manual/import/split/locked ownership rather than trusting the latest transient match label.
- Combined **Refresh metadata + chapters** and **Refresh cover** into one **Refresh** action for Matrix/Subbed entry inspectors. The same whole-entry refresh is also used by the Browse inspector: it invalidates unified/source cover cache entries, force-schedules identity re-resolution, and refreshes metadata plus chapters in the background.
- Corrected the documented Authelia reader-path regex to `^/api/reader/v1([/?].*)?$`. The previous non-capturing-group form is not accepted by Authelia's regex engine in this configuration.
- Extension mapping is `0.2.70 → 1.4.2070`.

## 0.2.69 - 2026-08-28

- Moved live AutoSourcarr configuration out of the replaceable `project/` release tree. TrueNAS now uses the persistent sibling `/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/` for `autosourcarr.env`, `source-host.env`, reader credentials, and Android signing state, so drag/drop release replacements no longer reset user settings.
- Reworked `scripts/init-truenas-config.sh` into a non-destructive migration/update step: it migrates legacy `project/config` values once, adds newly introduced variables without overwriting existing values, automatically creates a strong reader API token when blank, and migrates the v0.2.68 project-local Android signing identity.
- Updated the TrueNAS extension-builder example and local Docker build wrappers to mount/read the persistent sibling config. The in-container builder also rescues an existing v0.2.68 project-local signing key before it can generate a new identity.
- Kept external Android source traffic on AutoSourcarr's own `/api/reader/v1/` API key authentication. The extension now explicitly detects an Authelia HTML/login interception and reports the missing narrow reader-path bypass instead of silently presenting an empty catalog. No interactive WebView login is required for source requests.
- Added **Unbind automatic matches** directly to both Match Matrix and Subbed entry inspectors. It conservatively keeps the original/oldest primary anchor, splits every unlocked automatic attachment into its own entry, preserves manual/import/locked records, and records the repaired pairs as different works so the automatic matcher cannot immediately join them again.
- Added **Split / unbind duplicates** to the same per-entry inspector, opening the existing record-level duplicate splitter for that entry when merge history is available.
- Brought Subbed and Match Matrix controls back into parity by adding the concrete **Entry match count** filter to Subbed; both tabs continue to share the same entry inspector/actions.
- Closed another automatic source-record snowball path. Automatic source/variant matching now uses only the oldest native source anchor plus explicit manual/main-library/locked evidence. Prior `auto`, `auto-discovered`, `auto-additional`, provider identity titles, and provider summaries cannot become evidence for the next automatic attachment.
- Summary similarity can no longer authorize an automatic source attachment on its own. Generic short exact titles such as `First Love` require independent summary/author corroboration, while distinctive titles still require the configured high title similarity threshold.
- Tightened entry-level automatic duplicate merging as well: generated/shared metadata, summary or thumbnail corroboration requires at least one independently observed distinctive exact native title; generic shared titles cannot prove a merge by themselves.
- Bumped the safety-repair marker to `0.2.69`. Startup now rechecks snowball-shaped historical automatic groups with the new anchor-only gate before automatic duplicate matching can resume, splitting only clearly foreign or uncorroborated generic-title records; ordinary small historical groups are left untouched.
- Extension mapping is `0.2.69 → 1.4.2069`.

## 0.2.68 - 2026-08-28

- Replaced Android Authelia service-account login with AutoSourcarr's own reader API authentication. Android requests now use `/api/reader/v1/` and send `X-AutoSourcarr-API-Key`; the backend validates `AUTOSOURCARR_READER_API_TOKEN` before internally routing to the existing read API.
- The reader endpoint is read-only and fails closed: non-GET/HEAD requests return `405`, missing configuration returns `503`, and a missing/wrong token returns `401`. Normal `/api/v1/`, `/api/admin/`, and the web UI are unchanged, so Authelia can bypass only `/api/reader/v1/` while keeping the rest of the hostname under the existing browser policy.
- Android cover and chapter-page URLs are rewritten to the reader prefix and page-image requests explicitly keep the reader API header.
- Deprecated `AUTOSOURCARR_AUTHELIA_BASIC_USERNAME` / `AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD` for new builds; they remain accepted in env files so upgrades do not break parsing.
- Added persistent Android APK signing under `config/.extension-signing/`. The builder generates the key once, re-signs every Android artifact with it, verifies the result, and records the signing-certificate SHA-256. This prevents future `INSTALL_FAILED_UPDATE_INCOMPATIBLE` errors as long as the signing directory is preserved.
- Existing APKs signed by an older key still need a one-time uninstall/reinstall because the previous private key cannot be recovered from an installed APK.
- Extension mapping is `0.2.68 → 1.4.2068`.

## 0.2.67 - 2026-08-28

- Fixed the TachiyomiSY/Mihon external-client crash caused by a `kotlinx.serialization` compiler/runtime ABI mismatch (`GeneratedSerializer.typeParametersSerializers()` `AbstractMethodError`). The legacy 1.4 extension no longer uses generated serialization DTOs and now parses AutoSourcarr JSON with a dependency-free reader that is compatible with older client runtimes.
- Added non-interactive Authelia Basic service-account support for the Android extension. `AUTOSOURCARR_AUTHELIA_BASIC_USERNAME` and `AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD` are read by the builder and embedded only in the Android APK as an `Authorization: Basic ...` header; the internal Suwayomi JAR is deliberately built without the credential.
- The Android builder validates that both Authelia credential fields are set together, refuses to embed them into a non-HTTPS public URL unless explicitly overridden, and warns when the service-account password is shorter than 64 characters.
- Documented the preferred secure remote topology: keep `https://autosourcarr.coobnetwork.com` behind Authelia, grant a dedicated service account one-factor access only to `/api/v1/`, keep the browser/UI under the normal two-factor rule, and do not expose raw port 8282 to the Internet. A separate unauthenticated API hostname is no longer required.
- Added the Authelia service-account fields to the Settings/env editor as security settings, with the password treated as sensitive.
- Extension mapping is `0.2.67 → 1.4.2067`.

## 0.2.66 - 2026-08-28

- Fixed external Android extension reachability. The extension builder now builds the Suwayomi JAR with `AUTOSOURCARR_INTERNAL_URL` and performs a separate clean Android APK build with `AUTOSOURCARR_PUBLIC_URL`, so Docker-only `http://autosourcarr:8282` is no longer forced onto Tachiyomi/Mihon clients.
- The builder now refuses to publish an Android APK that still points at Docker-only `autosourcarr` DNS (unless explicitly overridden for an unusual environment), preventing a seemingly successful build that can only end in Android `ERR_NAME_NOT_RESOLVED`.
- Chapter page proxy URLs are now returned relative to the client base URL; the Suwayomi JAR and Android APK therefore keep using their own internal/external hostnames through chapter reading instead of switching back to one server-global URL.
- Documented the Authelia boundary: the normal Admin/UI hostname can remain behind Authelia, while Android needs either a directly reachable LAN URL or a dedicated non-interactive reader/API hostname. Internet-facing reader hosts should expose only `/api/v1/`; VPN-only access is preferred when possible.
- Tightened automatic duplicate merging after another false multi-work merge. Provider-generated identity titles and metadata summaries no longer count as direct duplicate proof, fuzzy metadata identities cannot authorize a merge by themselves, and an exact thumbnail fingerprint also requires an independently observed exact native title.
- Automatic duplicate evidence now uses source/library/manual titles plus source-provided summaries only. A metadata identity found by provider search is corroboration; only locked/manual or source-exact identity provenance may stand alone.
- Closed the same feedback path in source ingestion and metadata hydration: automatic local source attachment now trusts the entry's oldest surviving source anchor plus explicit manual/library/locked evidence, not provider-selected preferred titles or later automatic matches; one wrong auto-match therefore cannot generate the next 100% auto-match. Metadata providers are also scored only against native source/library/manual evidence, so one fuzzy provider identity cannot bootstrap another provider identity.
- Strengthened the unsafe-merge repair and bumped its startup marker to `0.2.66`, forcing the repair to run again on existing databases before automatic duplicate matching can resume. This specifically catches large mixed entries created after the earlier v0.2.63 repair had already completed.
- Added regressions for the `Disastrous Necromancer`-style metadata feedback loop, provider-to-source and provider-to-provider bootstrap failures, later-auto-source snowballing, placeholder/exact-thumbnail false merges, separate JAR/APK base URLs, request-relative reader page URLs, and the v0.2.66 safety-repair gate.
- Extension mapping is `0.2.66 → 1.4.2066`.

## 0.2.65 - 2026-08-28

- Browse now keeps keyed cards in place during metadata/background refreshes instead of rebuilding the entire grid. Visible title, status, match count, summary and source chips patch in place, so lazy covers and browser focus/scroll are not reset by heartbeat updates.
- Browse full-feed reloads preserve the visible card as a viewport anchor; the status line and card text areas have stable geometry, and Browse no longer uses `content-visibility:auto`, eliminating the off-screen intrinsic-height corrections that could make scrolling jump.
- Added a dedicated **Storage** status panel containing **AutoSourcarr size**, **Database size**, and **Downloaded size**; those values were removed from Catalog review.
- Added a stable browser scrollbar gutter so loading/reloading content cannot shift the whole application sideways as the page scrollbar appears or disappears.
- Extension mapping is `0.2.65 → 1.4.2065`.

## 0.2.64 - 2026-08-28

- Extension builder now publishes both `AutoSourcarr-v<version>-Suwayomi.jar` and `AutoSourcarr-v<version>-Tachiyomi-Mihon.apk` from the same Keiyoushi Release build, with separate JAR/APK validation.
- Status now reports AutoSourcarr's local app/config/data/deps footprint with manga downloads excluded, plus the combined SQLite database/WAL/SHM size separately.
- `Rebuild subscribed entries` no longer searches every enabled source before it can recover one subscription. Priority rebuilds search sources in up-to-three-worker priority waves (with the default four-worker source host) and stop after the first safe source anchor, preserving one worker for interactive use.
- Fixed the subscribed-rebuild task exception path incorrectly referencing an unrelated `import_id`, which could mask the real cancellation/failure.
- Extension mapping is `0.2.64 → 1.4.2064`.

## 0.2.63 - 2026-08-27

- Fixed the entry inspector layout: **Metadata identities** now sizes to its own content, while **Source matches** stacks directly beneath it in the right column instead of being stretched to the height of a huge Alternate titles block.
- Broadened automatic-merge safety repair so it no longer depends on surviving `entry_aliases`/v0.2.61 lineage. Suspicious historical groups are detected from current concrete-record count, source-title diversity and generated-alias volume, allowing older snowballs to be repaired too.
- The automatic-merge safety repair is now a visible startup Task and the duplicate auto-merger waits for the new `0.2.63` repair marker before it can run.
- Audited/fixed import live discovery: concrete source results are preserved directly in the strict resolver and persisted candidate shortlist even when a duplicate-heavy local catalog reranker would otherwise push them out of the top-N.
- Fixed alternate-title import discovery request batching. Aliases are now searched one alias at a time across bounded source waves instead of one large source×alias cross-product whose overall timeout could silently skip later aliases/sources. Automatic discovery now tries up to ten prioritized metadata/source aliases.
- Existing unresolved/failed imports are requeued once through the v0.2.63 candidate-pipeline marker so the corrected live-candidate/alias search applies to the current backlog without uploading the backup again.
- Added regressions for history-less legacy snowball repair, independent inspector column sizing, direct live-candidate preservation, and non-cross-product alias search.
- Extension mapping is `0.2.63 → 1.4.2063`.

## 0.2.62 - 2026-08-27

- Fixed unsafe transitive automatic duplicate merging: A↔B plus B↔C can no longer merge A+B+C unless C independently proves itself against every original member of the existing automatic group.
- Summary similarity is now corroborating evidence only; it cannot authorize an automatic duplicate merge by itself.
- Added persistent automatic-merge lineage and full pre-merge rollback snapshots for future automatic merges.
- Added a one-time conservative startup repair that splits clearly foreign source-record clusters out of unusually large pre-v0.2.62 automatic snowball merges and clears inherited unlocked metadata identities for re-resolution.
- Match Matrix now exposes **Entry match count**, counting concrete primary + additional manga records rather than distinct source families. The dropdown lists only counts currently present in the loaded Matrix, so anomalous values stand out immediately.
- Matrix entry badges now show both concrete match records and distinct source count.
- Extension mapping is `0.2.62 → 1.4.2062`.

# AutoSourcarr changelog

## 0.2.61 - 2026-08-27

- Added elapsed-time and ETA estimates to running Tasks; queued scheduler work resets its estimate clock when it actually starts.
- Manual `Resolve imported entry` actions now use a dedicated parallel lane (2 slots by default) instead of the single low-priority background source lane.
- Automatic duplicate matching defers/yields to manual import resolves and schedules a follow-up sweep, so catalog maintenance cannot monopolize user-requested import work.
- Added restart recovery for orphaned `resolving`/`retrying` import rows; selected matches are requeued through the parallel resolve lane after an upgrade/restart.
- Added `IMPORT_RESOLVE_CONCURRENCY=2`; the effective concurrency is capped to preserve private source-host capacity for interactive Browse/Matrix/reader traffic.
- Extension mapping is `0.2.61 → 1.4.2061`.

## 0.2.60 - 2026-08-26

- Fixed v0.2.59 background-task starvation that could leave every **Discover unresolved import matches** task at `Queuing unresolved candidate discovery` while automatic duplicate matching sat at `0/<catalog size>`. Import discovery now has its own serialized queue and queued import runs do not consume the generic background-task semaphore.
- Candidate discovery is coalesced per import run, so repeated startup migrations/Retry-all requests cannot launch multiple copies of the same unresolved-import pipeline. Historical import runs are still processed, but one run at a time.
- Automatic duplicate matching now defers to already queued/active import discovery before starting catalog-wide maintenance, and the defer wait does not reserve a generic background slot. Rescan requests raised while waiting for Imports are absorbed by the imminent sweep instead of forcing an unnecessary second full pass.
- Removed the duplicate startup race between the hidden duplicate-snapshot refresh and visible automatic duplicate merger. When automatic matching owns/schedules the catalog sweep, the standalone snapshot job yields; the automatic sweep rebuilds the review snapshot afterward.
- Fixed idle-only background tasks acquiring scarce task capacity *before* waiting for foreground quiet. Idle waiting now occurs first, preventing sleeping maintenance from starving real work.
- Duplicate progress no longer displays a misleading `0/<all catalog entries>` for the expensive shortlist phase. It reports **Building duplicate shortlist** first, then switches to actual candidate-pair progress as evidence is evaluated.
- Existing unresolved/failed imports are requeued once through the v0.2.60 pipeline after upgrade.
- Extension mapping is `0.2.60 → 1.4.2060`.

## 0.2.59 - 2026-08-26

- Fixed unresolved imports stopping on a 94-100% *review score* before the strict auto-match gate had actually accepted a work. High-scoring local/source candidates now continue through live source discovery and final evidence resolution until safely accepted or genuinely exhausted.
- Live/manual source-search shortlists are now passed directly into the strict import resolver instead of being discarded and replaced by a global catalog rerank. This prevents exact results found by title from falling outside the global top-N because of stale duplicate catalog rows.
- Automatic unresolved-import discovery now tries up to six prioritized concise/Latin-script metadata aliases (instead of only two), allowing provider aliases such as `Auto Hunting` to be searched automatically for romanized backup titles.
- Softened creator-name contradictions for imports/metadata when exact/full-title or near-identical synopsis evidence already proves the work; pen names, initials and romanization differences no longer veto exact-title matches.
- Existing unresolved/failed imports are requeued once through the v0.2.59 candidate pipeline so the new search/resolution path applies to the current backlog without re-uploading the backup.
- Made automatic duplicate matching observable and self-draining: every sweep is a normal Task, foreground-idle waiting is bounded, new evidence arriving during a sweep requests one coalesced follow-up sweep, and identity enrichment/import completion can trigger another sweep automatically.
- Automatic duplicate sweeps now metadata-enrich a bounded set of entries from the *actual duplicate shortlist* before applying shared MangaDex/AniList/etc. identity gates, then prepare exact decoded-thumbnail fingerprints and run the conservative merge rules. The summary auto-merge default is restored to 70%; manga/webtoon and colored/monochrome conflicts remain hard blockers.
- Added a **Run automatic sweep** control and duplicate-automation status reporting so the last checked/merged/error state is visible instead of the automation running silently.
- Fixed native browser text selection being destroyed by periodic UI refreshes. Structural renders are deferred while non-collapsed text is selected, and the Imports poll itself pauses until selection ends.
- Extension mapping is `0.2.59 → 1.4.2059`.

## 0.2.58 - 2026-08-26

- Reworked built-in themes toward a brighter Opera-GX-like neon treatment on neutral near-black surfaces instead of muted full-surface tinting.
- Added a **Custom** per-user theme with synchronized `#RRGGBB` HEX and RGB 0-255 controls; the derived vivid accent palette applies across navigation, controls, selected states, progress, links, badges and highlighted panels.
- Made the existing Nginx Proxy Manager / Authelia contract explicit: `Remote-User`, `Remote-Email`, and `Remote-Groups` are the primary headers. Added group/email diagnostics and a direct-backend-access warning so opening `IP:port` is correctly identified as bypassing Authelia rather than a broken header configuration.
- Strengthened the final import auto-match pass: candidate entries are metadata-identity-enriched before giving up when the import already has trusted MangaDex/AniList/etc. IDs. This fixes newly discovered rows having an empty candidate identity table while the import itself already has 100% DB identities.
- Fixed exact import-thumbnail tie-breaking to read cover URLs from persisted full metadata provider records (`records[].candidate`) rather than only the compact UI match rows.
- Multiple AutoSourcarr catalog rows can now be treated as an equivalent safe import cluster when every contender independently proves the same edition through a common trusted DB identity, or exact/full title plus >=90% summary, with pairwise colored/monochrome and manga/webtoon conflict gates still enforced. Multiple exact-thumbnail matches can likewise choose one safe row only after ordinary metadata gates pass.
- Removed `(colored)` from AutoSourcarr unified/main titles. Color/monochrome remains internal edition metadata and a hard matching/duplicate blocker; startup maintenance removes previously AutoSourcarr-added color suffixes. Imports also hide the color suffix in displayed backup/candidate titles while retaining the raw title as matching evidence.
- Existing unresolved/failed imports are requeued once through the v0.2.58 candidate pipeline.
- Extension mapping is `0.2.58 → 1.4.2058`.

## 0.2.57 - 2026-08-26

- Added a dedicated Settings → Customization tab with full-interface themes, interface density, and per-user preference persistence.
- Expanded Authelia identity detection with common forwarded username/email headers, `AUTHELIA_USER_HEADER`, and in-app proxy-header diagnostics.
- Made source-extension install/update/uninstall return after the Suwayomi extension mutation; concrete source reconciliation now runs in the background instead of blocking the button.
- Import auto-match no longer stops at ambiguous exact titles: metadata IDs, full/alternate titles, summary, author, source-reference evidence and exact decoded-thumbnail fingerprints can resolve the tie.
- Added cryptographic decoded-pixel thumbnail fingerprints. Different encoding/metadata is allowed only when dimensions and every decoded RGBA pixel are identical; one changed pixel is a mismatch.
- Automatic source matching can use exact thumbnails with compatible metadata, while color/media edition gates remain hard blockers.
- Automated conservative duplicate matching now uses shared metadata IDs, existing title/summary rules, and exact-thumbnail evidence while keeping manga/webtoon and colored/monochrome editions separate.
- Existing unresolved imports are requeued once through the v0.2.57 candidate pipeline.
- Extension mapping is `0.2.57 → 1.4.2057`.

## 0.2.56 - 2026-08-26

- Restyled **Installed Sources / Manage Sources** as full button tabs matching AutoSourcarr's main navigation instead of low-contrast underline-only subtabs.
- Reorganized Manage Sources into collapsible **Updates**, **Installed**, and **Not installed** sections. Updates appears first only when updates exist and owns the Update all action. Section open/closed state is persistent per user.
- Added source/extension icons to both Installed Sources and Manage Sources, with AutoSourcarr proxy endpoints so icons from the private source-host remain visible through HTTPS/Authelia without exposing the internal source-host URL to the browser.
- Added selectable Blue, Amber, Emerald, Violet, Rose, and Cyan UI themes. Theme selection is applied through shared CSS variables and persisted as a user preference.
- Added persistent per-user UI preferences. AutoSourcarr resolves the current Authelia reverse-proxy identity from `Remote-User` (plus common proxy aliases), stores preferences in SQLite by that identity, and restores theme, Browse page size, Duplicates scope, Sources subtab, and Manage Sources collapse state on login. Existing browser-only preferences are migrated into the user's server profile when no server value exists.
- Settings now shows which user/header owns the current UI preferences; when no Authelia identity header reaches AutoSourcarr it clearly falls back to the shared local profile.
- Extension mapping is `0.2.56 → 1.4.2056`.

## 0.2.55 - 2026-08-26

- Fixed **Discover unresolved import matches** being able to freeze permanently at a fixed progress count when one automatic source request was waiting behind a stalled background-source semaphore holder. Automatic source/reference deadlines now include semaphore acquisition rather than starting only after the slot is obtained.
- Added bounded per-source and per-wave deadlines to automatic unresolved-import discovery. A slow/broken extension is recorded as an error/cooldown candidate and no longer blocks the remaining queue.
- Added a 180-second hard deadline per unresolved import row. A pathological item is marked as deferred/error and the workers continue with the rest of the import instead of pinning the whole Task indefinitely.
- Task progress now includes deferred/error count while candidate discovery is running so a timeout is visible rather than looking like a frozen progress bar. Timed-out rows remain available for Retry unresolved.
- Extension mapping is `0.2.55 → 1.4.2055`.

## 0.2.54 - 2026-08-26

- Sources now has two internal tabs: **Installed Sources** (the existing source-language/search/chapter/priority controls) and **Manage Sources**.
- Manage Sources proxies the private Suwayomi extension catalog directly through AutoSourcarr, so normal source installation and updates no longer require opening the source-host WebUI.
- Added extension search, installed/available/update/obsolete filters, language filtering, extension-store badges, per-extension Install/Update/Uninstall actions, **Refresh catalog**, and **Update all**.
- Install/update/uninstall actions immediately re-sync AutoSourcarr's concrete source IDs. Removed extensions become unavailable historical source records instead of being destructively deleted.
- The Manage Sources tab is lazy-loaded and normal views use Suwayomi's local extension database; remote extension stores are contacted only when Refresh catalog/Update all needs a fresh repository snapshot.
- Extension mapping is `0.2.54 → 1.4.2054`.

## 0.2.53 - 2026-08-25

- Reworked import resolution so the untouched Mihon/Tachiyomi backup metadata and accepted external database metadata are combined as one evidence set instead of one replacing the other. Original full title, backup summary, author, alternate names parsed from the backup and source identity remain first-class evidence.
- External MangaDex/AniList/MangaUpdates/Kitsu/MyAnimeList/Comic Vine identities, canonical titles, alternate titles and summaries now participate directly in import candidate discovery, ranking and strict automatic acceptance.
- Fixed the unresolved-compatible-candidate bug: after local/source discovery finds a candidate, Imports now runs the automatic evidence gate again and immediately adds a unique strong match instead of merely showing it as a possible Match card. This covers changed-URL cases such as **The War of Corpses** → **War of Corpses** when the summaries identify the same work.
- A unique enabled backup-source reference with an exact/full alternate title can now auto-resolve even when its URL changed. Compatible title + strong summary and strong summary + compatible title combinations can also auto-resolve; ambiguous candidates remain review-only.
- Import metadata identities are persisted inside the import audit record and reused by Retry, local ranking and live source searches. When an import is accepted, the exact trusted provider records are also transferred onto the resolved AutoSourcarr entry so their IDs, aliases, descriptions, covers and creator metadata continue to participate after import. Imports now shows the accepted metadata-provider matches and confidence.
- Existing unresolved import runs are automatically re-queued once on upgrade to v0.2.53 so old rows receive the new metadata/auto-accept pipeline without re-uploading the backup.
- Normal source matching now reads trusted descriptions from accepted external identity records in addition to the catalog/source descriptions.
- Shared external database IDs are direct identity evidence, while colored/monochrome, manga/webtoon, rejection, author-conflict and ambiguity guards still take precedence.
- Added regressions for original+external metadata preservation, metadata transfer onto the resolved entry, shared-ID import evidence, metadata descriptions in normal source matching, and the War of Corpses compatible-reference case.
- Extension mapping is `0.2.53 → 1.4.2053`.

## 0.2.52 - 2026-08-25

- Made Matrix/Subbed and Browse inspector visibility user-owned. Only trusted user Open/Close events may change inspector visibility; asynchronous task, metadata, duplicate and snapshot refreshes cannot open or close an entry window. Background inspector refreshes no longer touch the `hidden` state.
- Added an external metadata/identity layer ahead of source discovery and metadata fallback. Initial providers are MangaDex, AniList, MangaUpdates, Kitsu, MyAnimeList via Jikan, and optional Comic Vine.
- Metadata identities persist separately from chapter sources, contribute canonical titles/aliases, descriptions, covers, creators, genres and status, and survive entry merges.
- MangaDex cross-links seed AniList/MangaUpdates/MyAnimeList/Kitsu identities when available. Weaker cross-links cannot overwrite a stronger existing provider identity.
- Adult metadata matching is enabled by default so doujin and adult manga/manhwa/manhua are not silently excluded. Comic Vine is opt-in through `COMICVINE_API_KEY`.
- External identities improve duplicate review scoring but do not bypass colored/monochrome separation, manga/webtoon separation, or the strict automatic duplicate-merge evidence gate.
- Added identity provider/admin endpoints and identity cards with a manual Re-resolve action in entry inspectors.
- Existing databases migrate in place through the normal catalog schema initialization.
- Extension mapping is `0.2.52 → 1.4.2052`.

## 0.2.51 - 2026-08-25

- Stabilized the admin UI while matches and background Tasks are running. Large list/table renders are now deferred while a modal, Matrix/Subbed inspector, Browse inspector, click gesture, or focused form control is active, then flushed after the interaction ends.
- Background metadata/cover heartbeats no longer rebuild Matrix, Subbed, Browse, Imports and Duplicates every ~2.6 seconds. Cover-ready changes update image elements directly; structural rerenders occur only when visible text/metadata actually changed.
- Added inspector generation guards so stale async entry-detail responses cannot overwrite a newer close/reopen of the same entry.
- Background entry reconciliation no longer replaces an open inspector with `Loading entry…`, scrolls it, or rebuilds it when a Task finishes. Title/status/description changes are patched in place instead.
- Explicit inspector actions that genuinely change source/chapter structure can still refresh that one inspector, but do so without the loading placeholder or automatic scroll jump.
- Manual-match modal updates no longer rebuild the Matrix/Subbed tables behind the modal; the updated snapshots are kept in memory and the table is rendered after the editor closes.
- Duplicate snapshot refreshes similarly keep the duplicate manager stable while it is open and flush the candidate grid afterward.
- Browse entry loads and Matrix/Subbed inspector loads now reject stale/out-of-order responses using generation tokens.
- Added UI hot-path regression guards for background inspector rebuilds and heartbeat-driven full-table rerenders.
- Extension mapping is `0.2.51 → 1.4.2051`.

## 0.2.50 - 2026-08-25

- Reworked background-task scheduling so Tasks cannot monopolize the resources needed by interactive Browse/Matrix/Imports/manual-search/reader requests.
- Added a true private-Suwayomi priority lane using a context-propagated background marker. Background federated searches and their child requests share `BACKGROUND_SOURCE_CONCURRENCY=1` by default, while the remaining source-host worker slots stay reserved for foreground traffic.
- All background source families now share that same low-priority budget instead of separate semaphores whose combined traffic could fill the source host.
- Added `BACKGROUND_TASK_CONCURRENCY=2`: many jobs may remain queued/visible in Tasks, but only two long-running task bodies actively compete for Python/SQLite at once.
- SQLite no longer serializes every read and write behind one global Python `RLock`. Thread-local WAL connections can now serve foreground reads while background jobs are working; SQLite itself coordinates the short write transactions.
- Added `SQLITE_BUSY_TIMEOUT_MS=3000` so an interactive write fails/retries promptly instead of appearing frozen for up to 30 seconds behind maintenance.
- Interactive HTTP request lifetime is now tracked explicitly. Low-priority idle maintenance pauses before new work while a real UI/API request is active.
- Explicit per-entry Refresh and Find-extra-record Tasks now use the reserved background lane rather than competing as foreground source work.
- Import Match/Retry/Reject, unresolved-candidate enrichment, and full backup import runtimes are marked as background source work so their internally parallel source calls cannot consume foreground-reserved slots.
- Auto-merge duplicates is now queued immediately instead of holding its HTTP request open for the full catalog pass; it waits for foreground activity to settle and remains visible/cancellable in Tasks.
- Duplicate snapshot rebuilds also participate in the bounded background task lane.
- Extension mapping is `0.2.50 → 1.4.2050`.

## 0.2.49 - 2026-08-24

- Duplicate candidate cards now expose **Match to selected** and **Different work** directly in the duplicate manager; opening Matrix is optional rather than the only prominent action.
- A manually confirmed duplicate merge now converts every active source record from both reviewed entries into durable locked manual bindings and creates/extends the corresponding **Manual group**. The decision therefore survives ordinary catalog/database rebuilds.
- Duplicate-review confirmation reports when the merge was saved in Manual groups and refreshes the Manual groups tab immediately.
- Added an explicit cover-cache readiness signal to compact entry metadata. Cards that initially received a placeholder automatically swap to the completed cached cover as soon as the background loader finishes, even when the database thumbnail URL itself did not change.
- Duplicate browser cards now participate in the same entry-cover revision system as Matrix/Browse/Imports, fixing covers that previously remained stale only on the Duplicates grid.
- A failed unified-cover attempt now schedules one cooldown-limited eager source-detail hydration for the best attached source record. This repairs sparse historical/search records automatically instead of requiring the user to open the Matrix inspector first.
- Cover recovery remains bounded by the existing metadata/private-source semaphores and a per-entry cooldown, so missing-cover repair cannot recreate the old source-host request storms.
- Extension mapping is `0.2.49 → 1.4.2049`.

## 0.2.48 - 2026-08-24

- Search keeps leading `The` on titles shorter than four words (counting the article on the edition-free title). Article removal remains a second-pass hint only for longer titles, and source result titles are never rewritten to the fallback query.
- Every main admin tab is warmed in parallel immediately after the initial Browse paint, so Matrix, Subbed, Duplicates, Imports, Tasks, Manual groups, Settings and Status already have data when opened.
- Duplicate review now uses a persisted stale-while-revalidate snapshot. Restarts can display the last completed duplicate groups immediately while a fresh scorer pass runs in the background; duplicate/status polling no longer starves that background refresh.
- Duplicates refresh in the background after catalog/source mutations and while the UI is open, without blanking the current review list.
- Added a compact `/api/admin/entries/summaries` heartbeat. The browser uses it only for currently rendered entries missing cover/summary data, patches those cards in place, and avoids full 500-row reloads.
- Covers discovered after a card rendered now receive an entry-specific cache revision and are updated everywhere (Browse, Matrix/Subbed, Duplicates, Imports and manual-match target) instead of remaining stuck on an earlier placeholder.
- Opening an entry immediately feeds its newer metadata/thumbnail back into all already-loaded tabs.
- Import live-search hits with sparse search payloads promote their source-detail metadata hydration so thumbnail/summary/author can appear while the candidate is still being reviewed; Imports performs short metadata refresh bursts after live search.
- Extension mapping is `0.2.48 → 1.4.2048`.

## 0.2.47 - 2026-08-24

- Added **Subbed** and **Unsubbed** sub-tabs inside Duplicates. The split uses the cached main-Suwayomi library mapping, so switching between the two views is instant and does not trigger source/network work.
- Duplicate cards are filtered per AutoSourcarr entry: a subbed entry appears under Subbed even when one of its possible counterparts is unsubbed; the duplicate manager still shows every counterpart so mixed pairs can be reviewed correctly.
- Added live counts to both duplicate sub-tabs. Search/title and candidate-count filters continue to combine with the selected Subbed/Unsubbed scope.
- The selected duplicate scope is remembered in the browser. Opening Duplicates from a specific Matrix duplicate badge automatically selects the scope containing that entry.
- Duplicate review now canonicalizes cached subscribed IDs through entry aliases, so a just-merged duplicate remains classified under the correct scope without waiting for a full main-Suwayomi library scan.
- Duplicates remains database-first: if the main-library snapshot is still being prepared, the duplicate index is not recomputed or blocked by a network request; the UI waits only for the lightweight cached subscription snapshot.
- Extension mapping is `0.2.47 → 1.4.2047`.

## 0.2.46 - 2026-08-24

- Manual **Import → Match** decisions now teach a durable import autoresolve mapping before any slow source/Main-Suwayomi reconciliation starts. Re-importing the same Mihon/Tachiyomi backup identity checks this learned mapping before normal existing/title/source discovery, so previously resolved rows no longer return to Unresolved.
- Import autoresolve is keyed by stable backup source-family + source URL (falling back to source-family + title) and targets a concrete source manga identity whenever possible rather than a transient AutoSourcarr entry id.
- A learned import target is also anchored in the existing semi-permanent manual-match layer. If the ordinary catalog was reset, AutoSourcarr can reconstruct the selected source identity locally from the stored record snapshot without doing a live manga-site search.
- Rejecting an imported match immediately removes that backup's learned autoresolve mapping so the rejected work cannot be silently selected again on the next import.
- Added **Reset import manual autoresolve** to Imports. It clears only learned import→entry mappings; ordinary manual matches/groups, rejected-match memory, import history and the main Suwayomi library are left untouched.
- Learned import autoresolve mappings survive normal **Reset database** and **Clear import history**.
- **Retry unresolved** also checks learned manual import mappings first.
- Existing pre-v0.2.46 manual import decisions are recovered conservatively on first re-import when the old accepted audit row still contains the selected entry in its candidate list. Using **Reset import manual autoresolve** disables that legacy-history inference too, so a reset cannot silently relearn mappings you intentionally cleared.
- Added regression coverage for manual-import learning, re-import autoresolve, database-reset survival, manual-match anchoring and the independent reset control. Backend regression suite: **200 tests passed**. Extension mapping is `0.2.46 → 1.4.2046`.

## 0.2.45 - 2026-08-24

- Rebuilt unresolved-import discovery as an incremental parallel pipeline: each unresolved item now ranks SQLite candidates immediately, translates and searches enabled sources concurrently, and publishes candidates as soon as each item finds them instead of waiting for all translation/local ranking to finish first.
- Automatic import review now searches **every enabled Search source in bounded waves** rather than an arbitrary first-ten/first-24 request slice. A dedicated import source lane keeps up to three source calls moving while reserving private-Suwayomi capacity for interactive browsing/reader actions.
- Automatic source search no longer waits for the UI to become idle. Slow/broken sources still use the existing timeout/circuit-breaker path, while later enabled sources are allowed to run instead of being starved behind the first sources.
- Added a second live pass using URL/English/conservative title aliases only when the exact visible backup title did not find a strong candidate.
- Lowered the **review-candidate** floor without weakening automatic matching/merge thresholds. Plausible alternate-title/synopsis candidates can now be shown for manual review instead of being mislabeled as “No possible match”.
- Imports now distinguishes **Searching** from **No possible match**. An unresolved row is only counted as “No possible match” after its local + enabled-source discovery pass actually finishes.
- Accepted import decisions now store a durable `matched_snapshot_json` review snapshot. Added/Existing cards can always show the exact applied AutoSourcarr title, sources, cover and description even after later catalog merges/rebuilds.
- Manual import Match optimistically shows the selected match card immediately while source binding/main-Suwayomi reconciliation continues in Tasks.
- Added regression tests proving automatic candidate discovery reaches an enabled source beyond the former first-ten cutoff and proving the accepted-match audit card survives removal/rebuild of the current catalog entry.
- Backend regression suite: **197 tests passed**. Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2045`.

## 0.2.44 - 2026-08-24

- Added an **Imported match** review card to Added and Existing import rows. It shows the exact AutoSourcarr entry title, cover, source families, description and entry id used by the import instead of hiding the accepted mapping after the row leaves Unresolved.
- Added **Reject** for accepted import matches. Reject is optimistic in the UI, immediately moves the item back to Unresolved, and runs cleanup/alternative discovery as a visible background Task.
- Import rejections are semi-permanent and survive a normal AutoSourcarr database reset and import-history cleanup. Rejections are keyed by stable source-family/source-URL or source-record identity rather than transient AutoSourcarr entry ids, so rebuilding the catalog does not silently accept the same wrong work again.
- A rejected candidate is excluded from automatic import resolution and suggestion ranking. Explicitly choosing that exact candidate with Match later clears only that candidate's rejection, while other rejected alternatives stay blocked.
- Added **Reset rejected matches** to Imports for intentionally clearing this semi-permanent rejection memory without deleting import history or Suwayomi library entries.
- If Reject is used on a work that this import actually added to main Suwayomi, AutoSourcarr removes that library item in the background unless another accepted import row still depends on the same entry. Items that were already in the library are never removed by Reject.
- Fixed the gap between automatic unresolved suggestions and manual Search. After local/translation ranking, unresolved rows with no credible candidate now automatically run the same live enabled-source search path that manual Imports Search uses. This is background-only, so the actual backup import completes first.
- Automatic live rescue searches up to ten enabled Search sources, prioritizes the original backup source when available, and persists candidates as soon as each row finishes. Work is processed by a small row-worker pool while individual source requests share the existing low-priority source-host semaphore, improving parallel throughput without recreating the source-host timeout storms from older releases.
- Added a dedicated suggestion-enrichment completion flag so the Imports UI can keep updating while live candidate rescue is still running instead of stopping as soon as English translation finishes.
- Added regression coverage for accepted-match audit data, durable rejection across database reset, immediate Reject UI/backend state, and automatic live-source rescue of rows that had no local candidates.
- Backend regression suite: **194 tests passed**. Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2044`.

## 0.2.43 - 2026-08-24

- Cleaned source/site labels out of source titles at the ingestion boundary. A title such as `Son-in-Law Does Cheap Cultivation – ManhuaUS` is stored/displayed as `Son-in-Law Does Cheap Cultivation`; ordinary dash subtitles remain untouched unless the suffix matches the owning source name. Existing affected source rows are repaired once on upgrade.
- Reworked description-alias parsing for line-wrapped Associated Names blocks. Incomplete fragments ending in connectives (for example `I Became a Student at the`) are joined to the next alias line when possible and otherwise discarded instead of becoming matching evidence. Existing source-owned description aliases are rebuilt by the one-time catalog migration.
- Added source-URL slug search hints for imports. A Mihon/MangaFire URL can now recover renamed/romanized titles such as `Hero, I Quit a Long Time Ago` or `Tsuihou Sareta Onimotsu Tamer...` even when the backup display title is completely different. Exact source URL equality remains authoritative identity evidence.
- Imports manual Search is no longer local-catalog-only. It renders SQLite candidates first, then searches the original/enabled live sources when the local result is not strong, ingests plausible results, and reranks them through the normal import evidence scorer.
- Enabled backup-source reference cards now include a direct **Match source reference** action.
- Preserved strict automatic matching: source-search aliases, translations, URL slugs and fuzzy manual-search hints improve discovery/ranking but do not weaken automatic identity attachment. Automatic source attachment still requires a trusted whole-title score of at least the configured threshold (94% minimum) or a cleaned-summary match of at least 90%.
- Added regression coverage for provenance-bound source suffix stripping, line-wrapped alias reconstruction, MangaFire URL-slug resolution, romanized URL lookup, and live Imports source search.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2043`.

## 0.2.42 - 2026-08-23

- Made Imports manual **Match** truly optimistic: the clicked unresolved card changes to a local `Matching…` state immediately and disappears from the Unresolved/review filters before the queue request is sent. Slow source binding and main-Suwayomi library reconciliation continue in Tasks.
- Added a lightweight single-import-item status endpoint and targeted pending-item polling. Matching several imports no longer reloads up to 5,000 import rows every polling cycle; pending rows are polled concurrently and patched individually.
- Import **Retry unresolved** uses the same immediate `Retrying…` state and targeted reconciliation. Cancelled/failed jobs restore or expose the item safely instead of leaving a stale pending row.
- Main-Suwayomi materialization now uses an internal exact-entry lookup token that is resolved directly from AutoSourcarr SQLite. This removes the previous chain of multiple title searches that could fan back out through the private source host just to add a known entry to the main library.
- Cached the main-Suwayomi AutoSourcarr source ID for five minutes and removed the redundant full main-library scan from each manual import match; the final library mutation remains idempotent.
- Increased safe foreground parallelism: private source-host work defaults to 4 concurrent operations, federated per-operation source concurrency to 6, metadata/translation/cover workers to 4, HTTP pooling to 64/32 connections, and SQLite cache to 64 MiB.
- Background source discovery can now use two workers when capacity permits, while reserving at least half of source-host capacity for reader/manual/interactive operations. Startup-wide maintenance remains disabled by default, so the added parallelism does not recreate the post-v0.2.29 source-host saturation issue.
- Reduced the post-interaction background-idle delay from 5s to 2s so queued maintenance catches up faster once the user stops interacting.
- Added regression tests proving the internal entry lookup never touches the private source host and that a queued import match is persisted as `resolving` before its background task completes.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2042`.

## 0.2.41 - 2026-08-23

- Removed the 5–15 second perceived delay from common mutations by eliminating full 500-entry Matrix/Subbed reloads after simple title, match, source-setting, variant-removal and delete actions. Local snapshots are patched immediately and only the affected entry is reconciled later.
- Manual source matching is now **local-first**. The already-visible manual-search candidate metadata is committed immediately; full source details and chapters hydrate asynchronously after the click. A stale background hydrate is discarded if the user changes/clears the match before it finishes.
- Manual-match UI is optimistic: the selected candidate becomes Primary/Additional immediately, then rolls back automatically if the local commit fails.
- Entry **Refresh metadata**, **Refresh metadata + chapters**, and **Find missing + extra source records** now queue cancellable background tasks instead of holding the browser request open on private-source network work.
- Imports manual **Match** and **Retry unresolved** now queue their source/Main-Suwayomi work and return control to the Imports screen immediately. Imports keeps polling only while those queued actions are actually pending.
- Source/language toggles update locally first and no longer reload both matrices. Failed source-setting writes roll back the optimistic UI state.
- Main-title and alternate-title changes update the visible card/inspector immediately instead of reloading Browse/Matrix/Subbed. Clear match, remove additional record, promote record and delete entry likewise patch local snapshots and defer only the small reconciliation work.
- Added request-path regression tests proving a manual match can commit without a synchronous source-host call and that explicit entry refresh is acknowledged before slow source work finishes.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2041`.

## 0.2.40 - 2026-08-23

- Restored secondary **Imports review filters**: Has possible match, No possible match, Source reference found, No source reference, Has English hint, and No English hint. They combine with All/Unresolved/Added/Existing/Failed/Skipped.
- Improved original backup-source binding when a backup's numeric Tachiyomi source ID no longer matches the currently installed source. The embedded backup source name can resolve a unique enabled current source variant, and an enabled source can be used for explicit import provenance even when its general discovery Search toggle is off.
- Added conservative second-pass title search fallbacks. Exact/original title is always tried first; only if it yields no usable result does AutoSourcarr retry without a leading `The`, then without a strict colored-edition qualifier such as `(Colored)`, including the combined form when applicable.
- Applied the same title fallback behavior to original-source import binding, manual source matching, Imports catalog searches, and materializing AutoSourcarr entries in the main Suwayomi library.
- Kept colored identity strict: search fallback does not change canonical identity, and semantic titles such as `Colored Pencil` are never stripped.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2040`.

## 0.2.39 - 2026-08-23

- Added **Clear history** to Imports. It deletes finished import audit runs/items while preserving active imports, Suwayomi library entries, manual matches, and manual groups.
- Added **Clear history** to Tasks. It deletes completed, failed, and cancelled task history while preserving currently running tasks.
- Both actions refresh their tab immediately after clearing.
- Extension version mapping: `1.4.2039`.

## 0.2.38 - 2026-08-22

- Made explicit manual source matches semi-permanent user state. Their exact source IDs, manga IDs and metadata snapshots now live outside the normal resettable catalog tables, so **Reset database** restores them locally instead of forgetting manual choices. Existing pre-v0.2.38 `manual`/locked rows are captured automatically on startup.
- Added a dedicated **Reset manual matches** action. It removes the durable manual-match state and unlocks the currently attached rows so they return to ordinary automatic matching without requiring a full database reset.
- Added persistent **Manual groups**. Two or more durable manual matches pointing at the same AutoSourcarr entry automatically form a group; groups survive normal database resets and merge-ID remaps.
- Added a **Manual groups** tab showing grouped and individual persistent matches, with catalog/source-record search, manual Add/Remove-from-group controls, **Reset groups**, and **Reset manual matches**. Reset groups removes only group membership and preserves the individual persistent manual selections.
- Explicit source-record removal, match replacement and entry deletion now remove the corresponding durable manual binding so user corrections are not resurrected by a future database reset.
- Database reset now restores persistent manual selections after resyncing sources and restoring subscribed entry seeds. Stored source metadata is sufficient for restoration, so this stage does not need a source website request.
- Mihon/Tachiyomi backup decoding now reads `BackupSource` metadata and attaches the embedded source name to each imported manga. Missing/unavailable sources therefore display the expected source name when the backup provides it, with the numeric ID retained only as a fallback.
- Import resolution now binds a resolved AutoSourcarr entry to its original enabled backup source when possible. If that entry already has a current match on the same source, the binding fast path performs no source search; otherwise only one targeted exact URL/title lookup is attempted.
- Simplified Status actions by removing maintenance buttons already present on their proper tabs. Source sync/additional-record discovery stay on **Sources**, duplicate merge/unmatch actions stay on **Duplicates**, metadata repair stays on **Browse**, and manual persistence resets stay on **Manual groups**.
- Added regression coverage for persistent manual restore/reset/group behavior, embedded Mihon source names, enabled-source import binding, and the no-network already-bound import fast path.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2038`.

## 0.2.37 - 2026-08-16

- Reworked Mihon/Tachiyomi import resolution around an existing-first, source-specific pipeline. Already-subscribed exact matches are identified from one main-library snapshot before any private-source search, avoiding unnecessary network work for entries that will be skipped anyway.
- Removed broad federated source searches from automatic backup resolution. Unresolved rows perform at most one targeted lookup on their original enabled source; weak results stay unresolved instead of being guessed.
- Enabled-source records can now be retained as reference evidence for unresolved imports, including title, summary and cover, so manual matching and candidate ranking can use the backup's own source metadata.
- Added rough English title enrichment for unresolved foreign titles. Translation runs in a low-priority background task after the actual library import finishes and is persisted in the import review.
- Rebuilt unresolved candidate ranking to use original title, rough English title, source-reference titles, backup/source synopsis and author evidence. Automatic suggestions now have a meaningful confidence floor instead of filling the page with unrelated entries.
- Enlarged backup/candidate thumbnails and removed title ellipsis in import review so full candidate titles and visual comparisons are visible.
- Deferred expensive suggestion ranking until after the add/existing pass and reduced import-run recount writes from every item to small batches.
- Main-Suwayomi AutoSourcarr materialization now tries only the four strongest title aliases per new import instead of walking a long alias list.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2037`.

## 0.2.36 - 2026-08-16

- Fixed the post-v0.2.29 colored-edition identity regression. A bare word such as `Colored` inside an ordinary title is no longer treated as an edition marker; only bracketed or explicit edition/suffix forms such as `(Colored)`, `[Full Color]`, `- Colored`, or `Color Edition` are accepted. Titles such as `Colored Pencil` and `The Colored Lands` remain intact.
- Color classification now comes from the actual source-record title or sampled chapter pages. Alternate-title text inside a description can no longer mark the current source record as colored just because it mentions a colored counterpart.
- Added a one-time conservative repair for v0.2.30-v0.2.35 persisted color profiles. False title-derived color profiles are invalidated, ordinary canonical titles are restored when unambiguous, and source attachments are split only when they match the exact old bare-color-word stripping failure and were not manually locked.
- Colored/monochrome state is now strictly a compatibility/conflict guard, never positive identity evidence. Explicit colored and regular editions remain separate, and `(colored)` is still added only to the colored entry when both editions are actually known.
- Raised duplicate auto-merge summary evidence from 70% to 90% and disabled automatic startup duplicate merging by default. Duplicate review and explicit auto-merge remain available, but startup cannot silently rewrite catalog identity.
- Restored the v0.2.29-style quiet startup: broad feed refresh, metadata sweep, translation sweep, additional-record sweep and duplicate merging are disabled at startup by default. One-time local catalog migration waits for an idle browser window instead of competing with first page load.
- All automatic private-source-host work now shares one low-priority lane and waits for a short interactive-idle window. Feed refresh, metadata hydration, missing-source discovery, additional-record discovery and color sampling can no longer independently saturate the private Suwayomi worker pool.
- Background missing-source/additional-record searches use one source operation at a time. This directly addresses the repeated `fetchSourceManga` timeout/cancel storms seen in the supplied private-Suwayomi log while preserving manual/interactive searches.
- Automatic missing-source matching now uses a bounded background search profile (two direct aliases plus one translated fallback, serial requests, shorter background deadlines) and reports all-live-failed searches to the existing source circuit breaker instead of retrying the same broken extension for every queued entry.
- Browse/source ingestion can no longer merge multiple already-existing catalog entries. A new source record may attach to one clear existing identity; tied candidates become a separate reviewable row. Existing-entry merges are now confined to manual duplicate review or the explicit Auto-merge action.
- Removed automatic browser cover retry loops. Placeholder images no longer generate repeated `_cover_retry` requests; the backend fills its persistent cache asynchronously and later renders use it naturally. Per-entry **Refresh cover** remains available as an explicit action.
- Opening an entry no longer automatically invalidates/refetches its cover. This removes a cosmetic network operation from the inspector critical path.
- Local entry deletion no longer blocks on a forced Main-Suwayomi library query. It uses a fresh completed library snapshot and fails safely with a retry message while that snapshot refreshes.
- Browser request timeouts are warnings instead of red application failures. Diagnostics keeps already-known errors visible immediately, does not create an `Error diagnostics timed out` issue about itself, and retains the last completed Status snapshot when a later health refresh is delayed.
- Added regression guards for semantic `Colored` titles, description-only color mentions, legacy false-attachment repair, quiet startup defaults, low-priority background source work and the removed cover retry storm.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2036`.

## 0.2.35 - 2026-08-16

- Rebased the post-v0.2.29 status/diagnostics behavior onto the v0.2.29 responsive request model while retaining the later colored/monochrome detection, manga/webtoon separation, persistent Imports review, stale-merge recovery, diagnostics, and chapter-page fallback features.
- Removed full duplicate graph construction from `/api/v1/health` and Status. Since v0.2.30, duplicate scoring includes color/media guards and became too expensive to run in an interactive status request. Status now reads only the last completed duplicate-review snapshot; rebuilding is serialized in the background.
- Matrix/Subbed no longer send up to 500 entry IDs in a giant GET query string or start duplicate scoring from the request. Duplicate badges use a compact POST request against the completed snapshot and retry quietly while the snapshot is being prepared.
- Main-Suwayomi library/download-size Status metrics are cache-first. Health returns the last completed values immediately and refreshes remote/filesystem metrics in the background instead of making every Status refresh wait for a full main-library query and directory walk.
- Normal Match Matrix is also main-library cache-first; only a first load of the dedicated Subbed view may wait briefly for an initial library snapshot. Fresh cached library/download metrics are not re-queried just because `/api/v1/health` was polled.
- Errors & diagnostics now opens from already-known client/backend state immediately. Backend error history and dependency health refresh in parallel; a slow health request can no longer delay display of errors that are already known.
- `/api/v1/health` includes a small active-error snapshot so the red/warning badge and its diagnostic details are from the same state; clicking the badge can render known errors synchronously before the full history request completes.
- A status-probe timeout is treated as degraded status detail, not proof that the AutoSourcarr backend is down. Browser errors no longer recursively create a red AutoSourcarr error merely because diagnostics/status itself was slow.
- Optional local Status-metric overruns are warnings instead of active application errors. They no longer turn the global health badge red while Browse/Matrix/reader APIs remain usable.
- Added low-priority/async decoding hints to lazy cover images and removed the giant duplicate URL, reducing HTTP/1.1 connection starvation while large 500-row Matrix/Browse views are visible.
- Static JS/CSS URLs are release-versioned so an upgrade cannot keep running a cached frontend from the previous release.
- Status cover-cache accounting now streams directory entries with `scandir` instead of materializing the entire cache directory on every refresh.
- Added release guards preventing health from rebuilding duplicate groups and preventing the giant `duplicate-candidates?ids=` request from returning.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2035`.

# Changelog

## 0.2.34 - 2026-08-16

- Fixed the browser-side regression that could show `AutoSourcarr error` even while the backend was returning HTTP 200 responses.
- Restored the Status/health request budget to 45 seconds. v0.2.33 had reduced it to 12 seconds while the endpoint can legitimately take longer on large duplicate-heavy catalogs.
- Removed the dead `statusCard(...)` error-path reference and now render a normal connection row when status retrieval really fails.
- Expected AbortController/navigation cancellations are no longer stored as application errors or surfaced as unhandled promise rejections.
- Task polling no longer runs every 1.5 seconds forever. It is sequential and adaptive: 3s while tasks/imports are active, 30s while idle, and 60s while the page is hidden.
- Task polling and the diagnostics endpoint no longer create recursive diagnostics entries about their own transient refresh failures.
- Private `api()` options are stripped before calling `fetch()`.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2034`.

# AutoSourcarr changelog

## 0.2.33 - 2026-08-16

- Moved the Mihon/Tachiyomi backup upload controls out of **Status** and into the dedicated **Imports** tab, directly above the persistent import-run review.
- Made the header health pill clickable. It now opens an **Errors & diagnostics** section showing current dependency state, recent backend/runtime failures, browser request failures, occurrence counts, request paths/status codes, and expandable technical details. Resolved backend history can be cleared without hiding active problems.
- Added a bounded, deduplicating runtime error registry and `/api/admin/errors` diagnostics endpoints. Repeated identical failures update one record instead of flooding memory or the UI, and a later successful request automatically marks matching unexpected-request errors resolved.
- Hardened `/api/v1/health`: failures in one metrics/dependency sub-check are reported as component problems instead of making the entire health endpoint fail with HTTP 500. This prevents transient status-refresh failures from incorrectly leaving the header at `AutoSourcarr error`.
- Fixed the current reader error path from the supplied log. Temporary private-source-host connection failures while resolving chapter pages are now returned as a controlled 503 diagnostic instead of an unhandled ASGI 500/traceback. AutoSourcarr also tries another chapter candidate after source-specific failures and can use an expired cached page map as a degraded fallback when the private source host is temporarily unreachable.
- Added regression coverage for error dedup/resolution, stale chapter-page fallback, and alternate-candidate page resolution. Backend suite: **152/152 pass**.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2033`.

## 0.2.32 - 2026-08-16

- Fixed Match Matrix/Subbed failures after merge/review operations where an interrupted historical merge could leave an alias chain terminating at a deleted entry. AutoSourcarr now reconstructs recoverable aliases from durable merge history at startup and duplicate scoring treats any remaining stale row as non-mergeable instead of raising `KeyError`.
- Persist merge aliases immediately after the primary catalog merge transaction, before secondary same-source variant work, so a later failure cannot orphan the deleted entry id.
- Removed catalog-wide title sanitation and manga/webtoon migration work from service construction. HTTP startup is now immediate; the expensive upgrade cleanup runs once in the background after startup and is version-marked so ordinary restarts do not repeat it.
- Match Matrix/Subbed return their entries and source records first, then hydrate possible-duplicate badges asynchronously. Duplicate analysis can no longer block the whole Matrix response.
- The default Browse page no longer eagerly loads the 500-row Match Matrix in parallel during first paint. Matrix/Subbed are loaded only when opened.
- Added regression coverage for interrupted-merge alias recovery, stale duplicate-pair tolerance, and keeping heavy catalog migrations off the construction/startup critical path.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2032`.

## 0.2.31 - 2026-08-16

- Added a persistent **Imports** review tab for Mihon/Tachiyomi backup imports. Every backup library item is recorded as Added, Existing, Unresolved, Failed, or Skipped with its backup title/source and resolution state, while the uploaded binary backup and temporary decoded JSON are still deleted after processing.
- Unresolved imports now show suggested AutoSourcarr candidates and provide **Match**, **Search**, **Retry**, and **Skip** controls. A manual Match immediately adds the selected AutoSourcarr entry to the real main Suwayomi library and updates the import result in place.
- Import runs retain their compact per-item audit rows after completion so Added/Existing/Failed/Unresolved results can be reviewed later without retaining the original backup. Re-running an old backup remains add-only: entries already present in the main Suwayomi library are skipped as Existing.
- Added explicit manga-vs-webtoon adaptation identity handling. Manga and webtoon versions of the same base work are kept as separate catalog entries and cannot auto-merge or auto-attach across adaptation type. When both counterparts are known, AutoSourcarr labels them **`(manga)`** and **`(webtoon)`**; a lone adaptation stays unsuffixed. Manual preferred titles are never rewritten.
- Reaffirmed colored-edition naming: only the colored entry receives **`(colored)`**, and only while a confirmed monochrome counterpart of the same base work also exists. The regular/monochrome entry remains unsuffixed.
- Added import-review and manga/webtoon variant regression coverage.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2031`.

## 0.2.30 - 2026-08-16

- Added **colored-vs-monochrome edition detection**. Explicit source-title markers such as `Colored`, `Full Color`, `カラー版`, `彩色版`, `Monochrome`, and `B&W` are recognized immediately; ambiguous editions can be classified from a small sample of interior chapter pages using real pixel saturation rather than RGB file format alone.
- Automatic source matching and duplicate auto-merge now reject high-confidence colored/monochrome conflicts. When neither side has a color signal, matching is not delayed; chapter-backed records are profiled in the background and a high-confidence mixed automatic entry is split into separate works. Manual/locked matches are never auto-split.
- Added persistent per-source color profiles and conservative entry-level color consensus. Color analysis is serialized to avoid saturating the private source host, samples interior pages instead of covers/title cards, and is configurable in Settings under **Color variants**.
- Colored and monochrome counterparts remain separate catalog entries. If AutoSourcarr has positively identified both editions of the same base work, only the colored entry receives the display suffix **`(colored)`**. A lone colored edition is not renamed just because its upstream source title contains a color marker.
- Added regression coverage for explicit color markers, color-base normalization, grayscale-vs-color page classification, colored/mono duplicate rejection, and conditional `(colored)` naming.
- Corrected the historical changelog heading for v0.2.28, which had accidentally been labelled as a second v0.2.29 section.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2030`.

## 0.2.29 - 2026-08-16

- Clarified and hardened the Mihon/Tachiyomi backup importer so it **adds manga to the real main Suwayomi library**, not to an AutoSourcarr-only subscription flag. `Subbed` remains only a view of the main-Suwayomi library. The importer now requires `updateManga(... inLibrary=true)` to be confirmed (with a main-library verification fallback) before counting an item as added, and the Status button/text explicitly says **main Suwayomi library**.
- Fixed season-reset chapter selection. Source chapters explicitly labelled `S2` / `Season 2` (or later) receive a decisive serving penalty when competing with a season-1/unseasoned candidate for the same virtual chapter number. This prevents a source such as `(S2) Ep. 1` from being selected as unified chapter 1 merely because it has more pages or higher source priority.
- Unified chapter display names now come from the same candidate resolver that serves the pages instead of from whichever source happened to be ingested last. Public chapters are rendered as **`Chapter X: <chapter name>`**, with redundant `Ch. X` / `Episode X` prefixes removed.
- Added regression coverage for main-Suwayomi library confirmation, season-2 reset avoidance, and canonical `Chapter X: <name>` rendering.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2029`.

## 0.2.28 - 2026-08-15

- Added an **add-only Mihon/Tachiyomi backup importer** under Status. Upload a `.tachibk` / protobuf-gzip backup; AutoSourcarr decodes it into temporary readable JSON, reads the library manga from that JSON, resolves each manga to a corresponding AutoSourcarr catalog entry, and adds that AutoSourcarr manga to the **main Suwayomi** library.
- Backup import is deliberately non-destructive: only backup manga marked as library/favorite entries are considered, entries already Subbed through AutoSourcarr are skipped, and the importer never removes a main-Suwayomi library item or writes `inLibrary=false`.
- Resolution prefers the backup manga's original source+URL, then exact known AutoSourcarr titles/aliases; if the work is not catalogued yet it performs live discovery and still requires an unambiguous exact identity before subscribing it.
- The uploaded binary backup and the temporary decoded JSON are deleted in a `finally` cleanup after the import completes, fails, or is cancelled. The import itself appears as a cancellable **Tasks** job with `current / total` progress and added/existing/unresolved/failed counters.
- Reworked the Tasks page desktop layout so **Ongoing**, **Completed**, and **Failed** are three side-by-side columns; small screens still stack the columns vertically.
- Wired this TrueNAS deployment's manga/download storage read-only as `/mnt/hdd/storage/komga/manga:/mnt/manga-downloads:ro` and set `MAIN_SUWAYOMI_DOWNLOADS_PATH=/mnt/manga-downloads` so Status can calculate downloaded size without another Compose edit.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2028`.

## 0.2.27 - 2026-08-15

- Tightened every automatic source-title attachment path to a **whole-title, length-sensitive 94% minimum**. Extra prefixes/suffixes now reduce the score; subset/token-set containment can no longer turn `God of War` into a 100% automatic match for `Bride of the War God`, `Dawn of the God of War`, or similar longer works. The configured `AUTO_MATCH_THRESHOLD` may be stricter, but automatic title evidence can never be lowered below 0.94. Manual search ranking remains permissive for human review.
- Applied the same strict automatic gate to additional same-source records, closing a second path where permissive manual-style scoring could attach unrelated variants. Cleaned-summary evidence remains an independent fallback.
- Added an **Auto-merge preview** dialog to Duplicates/Status. It lists every pair currently eligible for automatic merging, the exact-title/clean-summary evidence that makes it eligible, and requires a second explicit confirmation before any merge is executed. The real merge re-evaluates evidence at execution time.
- Expanded Status with **Catalogued entries**, **Subbed entries**, **Downloaded chapters**, and **Downloaded size**. The byte-size metric reads an optional read-only mount of the main Suwayomi AutoSourcarr downloads tree configured with `MAIN_SUWAYOMI_DOWNLOADS_PATH`; chapter/subscription counts continue to come from main Suwayomi.
- Reworked Tasks into separate **Ongoing**, **Completed**, and **Failed** sections. Cancellable long-running jobs show a **Cancel** button; a cancelled job moves to Failed with `Cancelled by user`. Metadata repair, additional-source scans, reset rebuilds, chapter checks and duplicate auto-merge bind cancellation/progress where the worker can safely cooperate.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2027`.

## 0.2.26 - 2026-08-15

- Hardened alternate-title cleanup for historical subtitle fragments such as `Didn't I?~` / `Right?~`. Startup reparses active source descriptions and removes invalid fragment rows before rebuilding aliases.
- Every active primary/additional source display title is now persisted as a provenance-bound **matched alias**. Matched aliases are removed with their source record and are deliberately search-only: they cannot become automatic identity evidence or create cascading bad matches.
- Public AutoSourcarr extension search is now database-first for an exact normalized preferred/alternate/matched alias. Suwayomi Browse/Migrate can resolve an already-known exact title immediately without waiting for slow upstream source searches.
- Match Matrix and Subbed now share the same per-source chapter controls and state: **Check chapters**, latest chapter, cached count, source-local missing count, **No chapters cached**, and **No chapters found**. Additional records expose **Use as primary** in both views.
- The Entry column now reports the latest chapter actually served by AutoSourcarr's unified chapter feed across all active chapter-contributing sources. Main-Suwayomi downloaded count remains separate.
- Per-record chapter refresh updates both Matrix and Subbed in place, including the unified entry latest chapter, without requiring a full page reload.
- Expanded the Tasks monitor so all user-triggered admin mutations appear as tasks. Long-running metadata repair, additional-record scans, reset rebuilds and chapter checks retain their real progress counters; other actions remain visible as recent completed/failed tasks.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2026`.

## 0.2.25 - 2026-08-15

- Added a **Tasks** tab immediately left of Settings with a live running-task badge, progress bars, `current / total` counters, and recent completion/failure state.
- `Repair missing metadata`, the catalog-wide additional-record scan, subscribed-entry rebuild after database reset, and per-source chapter checks now report user-visible task progress.
- Added **Check chapters** to Subbed source records so one matched source manga can be refreshed without refreshing every source attached to the entry.
- Source records now remember whether their chapter list has actually been checked. A checked record that returns zero chapters explicitly shows **No chapters found** instead of looking like an unscanned cache miss.
- Added an explicit AutoSourcarr version line to the Status tab.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2025`.

## 0.2.24 - 2026-08-15

- Split permissive manual-search ranking from the automatic attachment gate. Automatic title evidence no longer uses token-set subset scoring: one/two-word titles are exact-only, while longer titles may use a narrow contiguous-expansion rule. This keeps legitimate expansions such as `Overlord of Sichuan` → `The Iron Overlord of Sichuan` but rejects loose overlaps such as `Beast Taming Evolution` → `Global Beast Tamer: I Can See the Path of Evolution`.
- Automatic source matching no longer feeds titles from previous *automatic* source matches back into later decisions. Only the canonical/preferred title, observed description aliases, user title rows and explicitly manual/locked source titles are trusted. This prevents one bad automatic match from cascading across other sources. Cached/generated translation names remain search/ranking hints only.
- Added source-record-specific chapter coverage to the Subbed matrix. Every matched record can show its own latest/highest chapter, cached chapter count and missing whole chapters, with missing counts capped at that record's own latest chapter rather than another source's later chapter.
- Added **Use as primary** directly to additional source records in the Subbed matrix. Promotion is local and preserves the previous primary as an additional chapter-contributing record instead of dropping its coverage.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2024`.

## 0.2.23

- Added a global private-source-host work limiter (default 2 concurrent network-heavy GraphQL jobs) so overlapping Browse, missing-source, metadata and variant scans cannot saturate the private Suwayomi instance.
- Source-host health now gives the GraphQL probe its own short timeout before falling back to a cheap web-server liveness request, so the outer 5-second Status check can actually report **busy/degraded** instead of cancelling the fallback and falsely reporting the host offline.
- Database reset now gives subscribed entries exclusive priority: it restores subscription anchors first, searches/matches them directly without cataloging every unrelated search result, and defers ordinary background enrichment until the subscribed rebuild finishes.
- One- and two-word titles no longer receive token-set containment scores. `King` cannot auto-match `Demon King`, and `Demon King` cannot auto-match `The Demon King Returns`; exact short titles such as `Bleach` still match each other.
- Cached manual-search candidates can still be displayed, but cached-only records can no longer authorize automatic source matches. When a cached manga id is seen live, live source identity fields replace stale cached title/URL fields; if live detail lookup fails, automatic evidence is scored from the exact live search payload rather than cached summary/title data.
- User-selected main titles are persisted outside the resettable catalog both by entry id and by attached source-manga identity. They are restored after database reset/restart even if a non-subscribed entry is rediscovered under a new numeric id. Newly attached source records inherit the current manual preference; deliberately detached source records have that association removed.
- Catalog AUTOINCREMENT state is retained across resets so a persistent title preference can never be accidentally applied to a newly reused entry id.

## 0.2.22 - 2026-08-15

- Replaced alternate-title comma splitting with delimiter-aware parsing. Commas and other separators inside paired subtitle/title punctuation such as `～...～`, `~...~`, parentheses, brackets and quotation marks remain part of the title. This fixes titles such as `Skill Lender's Retrieving (Tale) ～I Told You It's 10% Per 10 Days at First, Didn't I～` while preserving real top-level comma-separated alias lists.
- Startup title sanitation now removes orphaned legacy `description-alias` rows with no source provenance before rebuilding aliases from each active source record's preserved raw metadata. This removes old split fragments such as `Didn't I?~` that could survive earlier reparsing passes.
- Match Matrix now uses fixed table layout plus explicit equal-width source columns. Lazy/intrinsic row sizing can no longer recalculate column geometry while scrolling, so every source column remains the same width and stable from top to bottom.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2022`.

## 0.2.21 - 2026-08-15

- The Edit Matches source/language selector now lists every enabled searchable source variant, so you can switch between enabled sources without closing and reopening the matcher.
- Fixed alternate-title comma parsing for grammatical clause continuations. Long titles such as `"Skill Lender" Get Back His Pride: Before I Started Lending, I Told You This Loan Charges 10% Interest Every 10 Days, Right?` remain one title while real comma-separated alias lists still split. Startup sanitation now rebuilds source-owned description aliases from preserved raw source metadata, healing rows split incorrectly by older releases.
- Improved UI responsiveness: entry inspectors and the match editor open immediately, stale/in-flight inspector and match-search requests are cancellable, failed entry loads show a retryable error instead of leaving only a selected row, and source-result enrichment is limited to sparse cards rather than refetching every result.
- Cover refresh no longer blocks opening an entry. Uncached covers return a temporary placeholder sooner, continue loading in the background with a higher bounded concurrency, and visible placeholder images automatically retry when the cache is likely ready.
- Added browser-side request timeouts/cancellation and off-screen rendering hints for large Browse/manual-match grids to reduce stalls and unnecessary work.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2021`.

## 0.2.20 - 2026-08-15

- Fixed a gap introduced by cataloging Browse results: an otherwise exact source candidate could already belong to a one-source Browse-created catalog row, causing automatic missing-source discovery to reject it under the stricter duplicate-entry merge rules. AutoSourcarr can now absorb only untouched one-source discovery stubs when the candidate has extremely strong independent source evidence; manual/locked or multi-source owners still require duplicate review.
- Missing-source discovery now keeps several eligible candidates per source and continues to later ranked results when an earlier candidate is blocked by a protected owner. One wrong/protected first hit can no longer suppress every correct result behind it.
- Additional same-source record discovery uses the same safe browse-stub absorption rule, so strong alternate records previously cataloged by Browse can become chapter-contributing additional matches instead of remaining stranded as separate entries. After an automatic missing primary is filled, AutoSourcarr immediately runs the additional-record pass so several obvious hits can become one primary plus additional chapter-contributing records without waiting for the next startup scan.
- Manual matching is now dynamically updated in-place. Choosing **Use as primary**, **Add match**, or **Clear current match** keeps the match browser open, immediately updates PRIMARY/ADDITIONAL badges and target match counts, and no longer waits for/reloads the full Matrix/Subbed datasets after every click. Matrix/Subbed synchronize in the background when the dialog is closed.
- Manual search cards explicitly show when the strict automatic source-evidence gate considers a candidate **AUTO ELIGIBLE**, making it clear which high-ranked results are only search similarities versus candidates that automatic discovery may safely attach.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2020`.

## 0.2.19 - 2026-08-15

- Made alternate-title storage provenance-strict. Automatically stored aliases now come only from explicit **Alternative titles / Associated names / Synonyms** metadata in source descriptions; source display titles stay attached to their source records and generated translations remain transient search hints instead of polluting the title list.
- Added conservative comma-list parsing for alternate-title metadata. Lists such as `+99 Wooden Stick, 99 Hardened Wooden Stick, 99 Reinforced Wood Stick` are split into separate titles, while grammatical comma titles such as `My Wife, the Demon Queen` and `I, Who...` stay intact.
- Added startup migration/cleanup for older databases: legacy source/main-library/generated-translation title rows are removed from the alternate-title table and old comma-packed description aliases are split when the parser can do so safely.
- Source replacement/removal now removes metadata contributed by the detached source manga record, including its extracted alternate titles and cached source/unified cover. Unified thumbnail/description metadata is rebuilt immediately from the remaining active records.
- Added an editable **Main title** control to Matrix/Subbed entry details and the Browse details drawer. A manual main-title choice is stored as the canonical display-title override and is not copied into alternate titles.
- Matching, duplicate detection and source discovery still consider the canonical title plus active source-record titles directly, so stopping source titles from being duplicated into the alternate-title table does not reduce matching recall.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2019`.

## 0.2.18 - 2026-08-15

- Prioritize conservative shortened forms of the preferred title inside the live source-search query budget, fixing cases where manual shortened searches found a manga that automatic all-name discovery missed.
- Ignore generic single-word aliases as automatic source-search queries unless provenance confirms the word is the source's complete title.

- Automatic gap filling now reuses the same expanded alias/romanization search path as the manual match browser for **sources that are still missing from an entry**. Browse discovery, Match Matrix loads, startup/catalog scans and the entry inspector can therefore find a source manga that exists but was never encountered through Popular/Latest browsing.
- Added a strict independent evidence gate for automatic missing-source attachment. Generated translations and loose search queries may locate candidates, but they cannot authorize a match; automatic attachment requires a real source/main-library title score meeting `AUTO_MATCH_THRESHOLD` or a >=90% cleaned-summary match.
- Single-word aliases are no longer automatic match or duplicate evidence. A one-word title is accepted only when provenance says it is the complete primary title returned by a source/main-library record, preserving legitimate works such as `Bleach` while rejecting noisy aliases such as `King`, `Skill` or `Love`.
- The entry inspector's **Find extra source records** action is now **Find missing + extra source records**: it first fills missing primary source matches, then searches matched sources for additional records/chapter coverage.
- Fixed Browse/Match Matrix synchronization. Browse-discovered matches invalidate old Matrix/Subbed snapshots, and opening Match Matrix now reloads it from the database so source records shown in Browse details cannot remain invisible in a stale matrix.
- Made Match Matrix substantially denser: smaller entry/source covers, narrower source columns and compact horizontal source-record rows reduce the height of entries with several additional records.
- Added Settings controls for `AUTO_MISSING_SOURCE_DISCOVERY`, `AUTO_MISSING_SOURCE_ENTRIES_PER_FEED` and `AUTO_MISSING_SOURCE_SOURCE_LIMIT`.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2018`.

## 0.2.17 - 2026-08-15

- Placeholder aliases such as `No alternative titles`, `No associated names`, `N/A`, `Unknown` and similar scraper/UI values are no longer stored or scored as titles. Existing placeholder title rows are removed automatically at startup before duplicate detection runs.
- Added synopsis sanitization for matching and display metadata. Alternative/associated-name blocks, official/original links, related-work chronology, source credits, publisher metadata, Markdown/HTML links and repetitive garbage are stripped before summary similarity is calculated. Metadata-only/gibberish text cannot satisfy the duplicate summary threshold.
- Tightened generated-translation matching again: matching creator names cannot corroborate a translated-only title. A translated-only hit still requires an independent >=90% cleaned-summary match.
- Browse discovery explicitly persists every discovered source manga into the AutoSourcarr catalog/match database before cards are returned, and the live Browse status reports how many catalog entries were touched.
- Reworked Duplicates into a browser-style grid containing every entry with unresolved possible duplicates. Selecting a card opens a focused duplicate-management dialog with the selected work, its possible counterparts, cleaned-summary/title evidence and Same work / Different works actions.
- Reset database now clears the filesystem cover cache as part of the reset. Added a separate **Reset covers** Status action that invalidates all cached covers without touching catalog data.
- Opening a Match Matrix/Subbed entry now invalidates that entry's unified cover plus every matched source-record cover at most once every 10 minutes, so stale/missing covers are automatically retried without hammering source sites.
- Fixed small duplicate-maintenance cleanup issues retained in the optimized tree while preserving the strict two-exact-title-or-configured-summary auto-merge gate.
- Extension display name remains `AutoSourcarr`; this release maps to Suwayomi/Keiyoushi version `1.4.2017`.

## 0.2.16 - 2026-08-15

- Reset database now snapshots every AutoSourcarr manga currently subscribed in the **main Suwayomi** before deletion, recreates those subscription anchor rows with the same URL/entry IDs, then automatically re-browses each subscribed title in the background to rebuild source matches, metadata and chapters. The reset aborts instead of orphaning library items when the main-Suwayomi snapshot cannot be read.
- Fixed the translation path that could connect unrelated works. Generated machine translations are now **search-only aliases**: they no longer create duplicate-shortlist edges and background translation enrichment no longer invokes catalog consolidation.
- Tightened translated-only source matching. A machine-translated title hit now needs an independent >=90% summary match before it can auto-attach; matching the same author is no longer enough.
- Source-title rows now retain their source language, and unified metadata prefers an explicit English/main-library title when available. This keeps English titles such as `+99 Reinforced Wooden Stick` ahead of romanized/native display names when reliable English metadata exists.
- Main-Suwayomi library reads now request richer manga metadata when supported, while preserving compatibility fallbacks for older GraphQL schemas.

## 0.2.15 - 2026-08-15

- Added a guarded **Reset database** action under Status. It clears AutoSourcarr catalog/cache/chapter-origin databases while leaving project/config env files untouched.
- Added **Unmatch duplicates** under Status. It lists entries with recorded merge history and lets a user split selected source manga records back into a separate AutoSourcarr entry. Source-provided aliases belonging only to the detached record are removed from the original entry.
- Added per-source-manga title provenance for new data so future duplicate splits can remove associated source titles precisely; old databases receive a conservative migration/backfill.
- Tightened automatic duplicate merging: fuzzy confidence alone can no longer merge works. Auto-merge now requires at least two completely matching non-translated titles, or a summary match meeting `DUPLICATE_AUTO_MERGE_SUMMARY_THRESHOLD` (70% by default). The minimum exact-title count and minimum summary length are configurable too.
- Reorganized Settings around common user tasks. General, Connections, Paths, Matching, Duplicates, Browse and Metadata are shown before advanced performance/runtime sections.
- Reworked the Duplicates tab to show exact-title count, summary similarity and explicit **Auto-merge eligible / Review only** state for each pair, with simpler Same work / Different works actions.

This is the single project change history. Every future AutoSourcarr release must add a new version section here; per-version upgrade/change documents are intentionally not shipped anymore.

## 0.2.14 - 2026-08-15

- Removed accumulated `UPGRADE-*.md` and other obsolete one-off update/change documents from the project bundle; legacy copies are removed automatically by the cleanup script when the extension builder runs.
- Consolidated historical and future release notes into this `CHANGELOG.md`.
- Changed the extension display name back to the stable **AutoSourcarr**. Keiyoushi/Suwayomi shows the extension version separately as `1.4.<versionCode>`; AutoSourcarr 0.2.14 maps to `1.4.2014`.
- Reworked Browse details into a fixed right-side drawer that does not re-render the result grid when opening/closing, does not lock body scrolling, and restores the exact previous page scroll position.
- Reworked Browse page-size controls into a visible quick-size selector plus custom 1-500 numeric field and explicit **Apply size** button. The chosen value is remembered in the browser.
- Made every Settings variable group collapsible using native disclosure sections.
- Removed the unused Python JAR verifier; builder/install validation uses the shell/JDK validator only.

## 0.2.13 - 2026-08-14

- Fixed the extension builder failure that happened after Keiyoushi reported `BUILD SUCCESSFUL`: the builder image did not provide a `python` command for the custom post-build verifier.
- Replaced builder/install validation with a shell/JDK-only JAR validator, removing the Python runtime dependency from extension packaging.
- Added a custom Browse entries-per-page override (1-500), persisted in browser local storage and included in browse cache keys.
- Changed Browse details from the earlier inline inspector to an overlay so entry details no longer appear at the bottom of the results.

## 0.2.12

- Added a far-right cogwheel **Settings** page that edits both `project/config/autosourcarr.env` and `project/config/source-host.env` in structured groups.
- Added a read/write `/config` bind mount to the AutoSourcarr service in the supplied TrueNAS/Compose configurations so Settings edits the exact env files Compose reads from the host.
- Replaced version-numbered Compose helper artifacts with the stable `compose-env-file.yml` filename; added `scripts/cleanup-old-helper-files.sh`, and made the extension builder automatically remove legacy `autosourcarr-v*-compose*.yml` leftovers.
- Fixed the v0.2.11 extension-builder false failure. Gradle's official Keiyoushi Release JAR could be generated successfully but the custom validator then rejected it when ProGuard/generation did not preserve the literal `AutoSourcarr.class` path. The validator now checks the release JAR/manifest/JVM bytecode without assuming the generated entry-class filename.
- Added regression tests for env-file parsing/editing, comment/order preservation, unknown-key rejection and multiline-value rejection.

## 0.2.11

- Reordered the app navigation to Browse, Subbed, Match matrix, Duplicates, Sources, Status and made Browse the default tab.
- Moved the Browse inspector above the result grid and changed its scroll behavior to match the inline Match Matrix inspector.
- Added MAIN-Suwayomi chapter statistics for subscribed AutoSourcarr entries: total chapter count and downloaded chapter count now appear under Subbed cards and under subscribed cards in the normal Match Matrix.
- Extended main-library GraphQL reads with `downloadCount` and `chapters { totalCount }`, with a basic-field compatibility fallback. Download state is never read from the private source-host instance.
- Kept TrueNAS Custom App environment files release-managed in `project/config/` and verified the exact absolute `env_file` host paths in Compose YAML.
- Replaced the broken APK publication path with Keiyoushi's release JAR output. Suwayomi 2.3.2243+ installs JAR extensions directly, bypassing the APK->JAR dex2jar conversion responsible for the stackmap-frame `VerifyError`.
- Simplified `loadFilterSources()` to remove the synchronized-method bytecode path as an additional compatibility safeguard.
- Regression suite: 70 backend tests pass; Python, JavaScript, shell, YAML, env, overlay-safety and direct-JAR container checks pass. Docker/Android Gradle compilation remains environment-dependent.

## 0.2.10

- Added a companion **Browse** tab with Popular, Latest and Search modes, source/language/match-count filters, pagination, live refresh, cover cards and an inline detail inspector.
- Added automatic sparse-metadata repair on cached browse feeds plus manual per-entry and catalog-wide **Refresh metadata/data** actions.
- Cover proxy now fails over across all active matched records before returning a placeholder.
- Moved the live `autosourcarr.env` and `source-host.env` into `project/config/` and updated TrueNAS Compose to the requested project-local paths.
- Switched the Suwayomi extension compatibility target from `KeiSource`/API 1.6 to legacy `HttpSource`/API 1.4 while retaining APK packaging. This avoids the `KeiSource.readFilterCacheState()` verifier path that fails in Suwayomi 2.3.2243.
- Sparse extension cards now remain uninitialized until a detail refresh fills their missing metadata; complete cards remain fast.

## 0.2.9

- Serialized duplicate review, merging, candidate snapshots and automatic consolidation under one re-entrant maintenance lock while retaining atomic fresh-group responses and stale-ID recovery.
- Restored SAVEPOINT-based nested SQLite transactions so an optimized outer write cannot deadlock against a nested second writer connection.
- Replaced the invalid JAR-only Suwayomi extension path with Keiyoushi's standard debug APK build and deterministic `AutoSourcarr-v<version>-Suwayomi.apk` output.
- Added source-record-level detail hydration for every newly matched record, plus bounded startup repair for historical sparse cover/summary/author/artist/genre/status metadata.
- Prevented sparse browse/search refreshes from erasing richer stored metadata and added cover failover across all active primary/additional matched sources.
- Moved private source-host settings into a persistent `source-host.env` alongside `autosourcarr.env`, so normal future updates do not require editing Compose environment blocks.
- Retained drag/drop overlay-safe extension staging and persistent data/config separation.

## 0.2.8

- Hardened duplicate-review state after sequential merge / different-work decisions using re-entrant maintenance locking and refreshed candidate snapshots.
- Added SAVEPOINT-backed nested SQLite writes so optimized nested operations do not block on a second writer connection.
- Preserved previously acquired thumbnail, description, creator, genre, status and metadata fields when a later browse/search result is sparse.
- Added background matched-record metadata hydration and startup repair for historical incomplete records.
- Added multi-match cover recovery and kept additional source records contributing chapters.
- Changed persistent browse feeds to resolve current catalog metadata instead of freezing old card snapshots.
- Added release-managed environment-file support and overlay-safe extension source staging.

## 0.2.7

- Fixed repeated duplicate review after merge/non-duplicate decisions; review responses now carry a fresh candidate queue and stale IDs self-recover.
- Fixed an accidental nested duplicate loop introduced during the optimization pass.
- Unified metadata now uses both primary matches and additional same-source records.
- Sparse entries are enriched in the background with full metadata without blocking Browse.
- Added persistent `autosourcarr.env` deployment workflow.
- Separated extension output into Suwayomi JAR and Mihon APK directories and added a direct Suwayomi JAR installer helper.
- Made extension staging safe for drag-and-drop/overlay project updates.

## 0.2.6

- Fixed TrueNAS startup when `/deps/.requirements.sha256` exists but the actual Python packages are missing/corrupt. Startup now validates imports, fully rebuilds the dependency cache when needed, and writes the hash marker only after a successful install.
- Added pip retries/timeouts for transient package-download failures.
- Moved the TrueNAS dependency bootstrap into `autosourcarr/start-truenas.sh` so the Compose command stays small and consistent.
- Gradle wrapper/cache now persists under `extension/.gradle-home`, so recreating the extension-builder does not redownload Gradle after the first successful fetch.
- Added Gradle wrapper download retries, a 60-second wrapper network timeout, and a limited release-build retry for transient network failures.
- Removed duplicated quality-analysis environment entries from the TrueNAS compose template.

## 0.2.5

- Full performance/reliability audit of the hot browse, matching, duplicate, chapter-page and HTTP paths.
- Reuses persistent pooled HTTP clients for private/main Suwayomi and translation requests instead of creating a new connection pool per call.
- Adds bounded LRU caches for repeated search results, cover hints and page lookup metadata, plus a TTL-backed chapter page-list cache.
- Reader page images are streamed from the private source host instead of being fully buffered in AutoSourcarr memory.
- Reworked entry-card/catalog loading into batched SQL queries, removing major N+1 query patterns from Matrix/browse/subbed result assembly.
- Reworked local auto-match candidate scoring into batched SQL/in-memory scoring; exact-title matching no longer opens several SQLite connections per candidate.
- Rebuilt duplicate candidate generation around exact/rare-title signatures and bounded buckets instead of quadratic common-token pair generation; duplicate groups are snapshot-cached.
- Added additional SQLite indexes plus WAL-friendly cache, mmap, busy-timeout, synchronous and temp-store tuning.
- Split-chapter reconciliation now loads page-count evidence in bulk instead of querying each virtual chapter independently.
- Added per-source failure circuit breakers so repeatedly failing/Cloudflare-broken sources are temporarily skipped during broad discovery while explicit manual retries remain possible.
- Startup no longer waits for the private source host; persistent browse feeds remain available while dependencies start or are temporarily offline.
- Added a lightweight `/api/v1/live` health endpoint and changed Docker healthchecks to use it instead of expensive dependency/status checks.
- Bulky admin/status JSON is gzip-compressed, while manga image/page streams deliberately bypass gzip.
- Cover cache filesystem I/O and quality image decoding/hashing are moved off the asyncio event loop; quality sampling can use a small bounded concurrency pool.
- Cover requests no longer load full entry details/chapter-contribution metadata before checking the persistent image cache; the common cached-cover path is now filesystem/cache-first.
- Suwayomi extension refreshes now pass `fetchDetails`/`fetchChapters` through to the backend so detail-only updates do not fetch chapter lists and chapter-only updates do not re-fetch metadata.
- Chapter candidate refresh detects source-order changes and invalidates only the affected cached REST chapter index/page map, preventing a stale positional chapter index from serving the wrong chapter after source reordering.
- Persistent browse-cache reads no longer take SQLite's writer lock just to update an unused access timestamp.
- Extension builds are now reproducible by pinning the Keiyoushi source-tree commit after the first build; set `KEIYOUSHI_UPDATE=1` only when intentionally updating that build toolchain.
- Expanded advanced Status metrics for cache limits, HTTP pooling, source cooldowns, SQLite tuning and quality-analysis limits.
- SQLite now reuses one configured connection per worker thread for top-level operations, preserving isolated nested contexts. This removes repeated connection/PRAGMA setup and makes SQLite's page cache effective instead of throwing it away after each query.
- Background feed refreshes and automatic same-source record discovery are globally serialized so several stale pages/entries cannot multiply per-request concurrency into a burst of source-host traffic.
- Federated local search title scoring, manual matching, and same-source additional-record scoring reuse batched/preloaded entry context instead of rehydrating the same entry for each candidate.
- Generated title translations use a bounded in-memory hot cache in front of the persistent SQLite cache.
- Exact-title duplicate clusters no longer have an arbitrary group-size ceiling; exact buckets use an O(n) star connection while fuzzy/common-title buckets remain bounded.
- AutoSourcarr extension network timeouts now leave headroom above the backend's longest source-page request so a valid slow page-list request cannot be aborted by the extension first.
- Background cover loaders are cancelled cleanly before shared HTTP clients close during shutdown.
- Backend test suite expanded to 59 passing tests.

## 0.2.4

- Manual match search is now cache-first and partial-result tolerant: already-known source records are ranked from SQLite immediately, direct aliases are attempted before generated translations, and translated-query timeouts no longer discard useful matches. Defaults are 12s/query, 24s overall and four concurrent/best live queries.
- Automatic same-source discovery now searches expanded aliases for already-matched sources, can retain any number of strongly corroborated additional manga records, and immediately imports their chapter lists so every accepted record can fill chapter gaps.
- Added **Multiple records on one source** filter to Match Matrix and Subbed, plus per-record chapter contribution counts and on-demand **Find extra source records** actions.
- Added persistent duplicate review decisions and a dedicated **Duplicates** page with cluster cards, counterpart titles, evidence, **Same work — merge**, and **Different works** actions. Reviewed non-duplicates are suppressed from future duplicate suggestions.
- Matrix duplicate warnings now name/link likely counterpart entries instead of showing only a generic badge.
- Orange/attention cards on Status are actionable and navigate directly to the relevant review view.
- Improved persistent-feed cover recovery: merged/aliased entries reuse existing entry/source cover-cache files before attempting any network thumbnail fetch.
- Source availability filters were added to the Sources tab.
- The extension build now reads the root AutoSourcarr `VERSION` and displays `AutoSourcarr v0.2.4` in Suwayomi while keeping the actual source name stable for source-ID/library compatibility.
- Backend test suite expanded to 49 passing tests.

## 0.2.3

- Auto-matches and consolidates duplicate clusters of any size instead of treating duplicates as pair-only.
- Same-source duplicate records remain additional matches and every active record contributes chapters.
- The entry inspector now shows the number of chapters contributed by each primary/additional source record.
- Manual matching searches substantially more aliases, plus conservative shortened title variants.
- Adds offline romanized search queries for Japanese/Korean/Chinese titles.
- Adds optional LibreTranslate integration for origin-language title searches and reverse/literal translations of native titles.
- Generated translations are cached in SQLite and used for duplicate discovery, but require description/author corroboration before an automatic merge when the generated title is the only matching evidence.
- Background translation enrichment can discover translated duplicate clusters without slowing normal browsing.
- Duplicate review now considers fuzzy matches across all known titles, including generated translation aliases.
- Reworked the Status page around three questions: are connections healthy, is the catalog clean, and is browsing cached/ready. Advanced metrics are collapsed under More details.
- Status now reports translation availability/cache state.
- Keeps persistent feed behavior unchanged.

## 0.2.2

- Allow one source/language to keep multiple matched manga records on a unified entry. Additional records contribute chapters instead of creating duplicate AutoSourcarr entries.
- Manual match browser automatically searches up to six known titles/aliases, merges duplicate search results, and ranks candidates by closest title/description evidence.
- Add **Use as primary** and **Add match** actions in the visual matcher.
- Add manual Matrix entry deletion with source-record tombstones so deleted entries do not immediately reappear.
- Add a **Possible duplicate** Matrix/Subbed filter based on every known title/alias, not only the displayed preferred title.
- Add publication-state filtering: ongoing, finished/completed, hiatus, cancelled, licensed, and unknown.
- Make first uncached browsing return partial results on a short interactive deadline (4s/source, 6s overall by default), then finish the full refresh in the background. Persistent cached feeds remain instant.
- Prefetch Popular/Latest pages 1-2 at startup by default.
- Redesign Status around actionable warnings, quick maintenance actions, publication breakdown, cache state, and separate interactive/background performance limits.

## 0.2.1 - 2026-08-10

- Fixes duplicate AutoSourcarr entries that came from two manga IDs on the same underlying source. Same-source records now merge only with unusually strong evidence, while the losing source record is retained as an alternate chapter provider.
- Adds stable entry aliases so a manga already added to the main Suwayomi still resolves if its AutoSourcarr catalog row is merged into another canonical entry.
- Adds startup/background duplicate repair plus a **Repair duplicates** action on the Status page. Existing browse-cache entry IDs are rewritten through aliases and deduplicated.
- Marks browse/search `SManga` objects as initialized in the AutoSourcarr extension so main Suwayomi does not immediately refresh every visible browse card. Extension versionCode is now 8 (`1.6.8`).
- Stops uncached Popular/Latest from falling back to arbitrary recently-updated catalog rows; the first uncached feed now comes from a bounded live fetch and is then persisted.
- Source sync now marks removed/unloaded private-Suwayomi source IDs unavailable and stops querying them while retaining their historical match data.
- Manual source-search failures such as HTTP 404/Cloudflare errors are returned as an inline source error instead of causing a FastAPI 500.
- Limits concurrent background cover fetches (`COVER_FETCH_CONCURRENCY`, default 2) to reduce source-host thumbnail bursts and HTTP 429 responses.
- Same-source alternate records participate in entry refresh so their missing chapters can extend the unified chapter list.
- Status now reports merged aliases, alternate source records, unavailable source variants, startup delay, and cover concurrency.
- Backend test suite expanded to 33 passing tests.

## 0.2.0

- Persist browse/search feed ordering in `catalog.sqlite` so previously acquired entries appear immediately after restarts.
- Added stale-while-revalidate browsing: cached results return first and stale feeds refresh in the background.
- Popular and Latest page 1 refresh automatically in the background on startup.
- Match Matrix and Subbed match-count filters are now exact counts, including `0 matched` and `1 matched`.
- AutoSourcarr extension search now supports exact matched-source counts as well.
- Rebuilt the Status tab as a dashboard with connection, catalog, chapter, source, cache, cover and performance cards.
- Health checks are bounded so an offline Suwayomi instance cannot stall the Status page for the full source timeout.
- Fresh-install concurrency defaults were reduced to 4 to be gentler on sources prone to Cloudflare/522 failures; existing installs can choose their own value.

## 0.1.9

- Consolidates reliable duplicate entries touched by browse/search into one AutoSourcarr entry.
- Preserves source matches, aliases, chapter candidates, chapter history, and combined chapter coverage when entries merge.
- Chapter resolver now favors complete page sets before source priority when page counts prove one candidate is truncated.
- Adds a persistent asynchronous cover cache under `/data/cover-cache`; slow/broken source thumbnails no longer block browsing.
- Matrix covers now use larger Suwayomi-library-style cards with title overlays.
- Adds a short-lived federated result cache and caps per-source browse ingestion for faster repeated discovery.
- Default federated deadlines are reduced to 8 seconds per source / 14 seconds overall; entry refresh uses bounded source deadlines as well.
- Backend test suite now passes 21 tests.

## 0.1.8

- Match Matrix now shows AutoSourcarr and per-source manga thumbnails.
- Entry Details functionality moved into an inline inspector inside Match Matrix/Subbed, including the full description, titles, source matches, chapter candidates, quality tools, and served history.
- Added a Suwayomi-style visual manual match browser with cover grid, title, author/artist, description, source-language selector, paging, locked manual selection, and clear-match action.
- Added **Subbed** tab that mirrors Match Matrix for entries currently added to the main Suwayomi library. Configure `MAIN_SUWAYOMI_URL` for this view.
- Automatic matching now also compares descriptions. A meaningful description similarity of 90% or higher is treated as high-confidence match evidence.
- Added source-candidate cover proxy and source-manga detail endpoint for visual matching.
- Added main-Suwayomi health/config status.
- Backend test suite expanded to 18 tests.

## 0.1.7 - 2026-08-10

- Fixed main-Suwayomi browse/search requests timing out while AutoSourcarr waited for slow underlying sources.
- Federated browse/search now has an independent per-source timeout and a hard overall deadline.
- Completed sources are returned as partial results when other sources time out.
- Increased the AutoSourcarr extension HTTP read/call timeout to safely cover the bounded backend request.
- Added `FEDERATED_SOURCE_TIMEOUT` and `FEDERATED_OVERALL_TIMEOUT` configuration.
- Extension versionCode bumped to 6.

## 0.1.6 - Suwayomi JAR build fix

- Changed the extension builder from `assembleDebug` to `assembleRelease`.
- The builder now copies only Keiyoushi's release JAR to `extension/dist/`.
- AutoSourcarr should be installed into Suwayomi using that `.jar`, not the debug APK.
- Bumped the AutoSourcarr extension version code to 5 (`1.6.5`).
- Added a hard check that exactly one release JAR was generated.

## 0.1.5 - 2026-08-10

- Fixes extension rebuilds after the AutoSourcarr rename by removing the legacy `src/all/suwayomicompanion` module from the persistent Keiyoushi build checkout before compiling.
- Prevents KSP from seeing both the old `SuwayomiCompanion` and new `AutoSourcarr` `@Source` classes in the same module.
- Keeps the extension `versionCode` at 4 because the extension code itself is unchanged from v0.1.4.

## 0.1.4

- Removes the `Hybrid Unified + Live Discovery` subtitle from the AutoSourcarr admin header.
- Keeps the project, API, Docker services, extension and UI branded as **AutoSourcarr**.

## 0.1.3 - 2026-08-10

- Renames the project and user-facing extension from `Suwayomi Companion` to **AutoSourcarr**.
- Renames Docker services/containers to `autosourcarr` and `autosourcarr-source-host`.
- Renames the backend source directory to `autosourcarr/` and the persistent application data directory to `autosourcarr-data/`.
- Renames `COMPANION_PUBLIC_URL` to `AUTOSOURCARR_PUBLIC_URL`.
- Renames the Keiyoushi extension package/module from `suwayomicompanion` to `autosourcarr` and the extension class to `AutoSourcarr`.
- Sets the extension name shown in Suwayomi to `AutoSourcarr`.
- Bumps extension `versionCode` to 4.
- Adds a TrueNAS migration guide and a ready-to-paste manga-cluster service snippet for the AutoSourcarr names.

## 0.1.2 - 2026-08-10

- Fixes the AutoSourcarr extension Kotlin build against the current Keiyoushi source tree.
- Fixes nullable `SManga.description` shadowing in `EntryDto.toSManga()`.
- Renames the `SourceFilter` backing list so it no longer hides `Filter.Select.values`.
- Bumps the extension `versionCode` to 3.

## 0.1.1 - 2026-08-10

- Groups language variants of the same underlying source into one Sources row.
- Adds a persistent multi-select language picker with checkboxes, Select all, and Clear.
- Keeps Search and Chapters as source-family toggles while applying them only to selected languages.
- Migrates existing v0.1.0 source settings without losing the languages that were effectively enabled.
- Normalizes priority across all language variants of a source family.
- Groups the AutoSourcarr extension's underlying-source filter and adds a multi-language checkbox filter.
- `SOURCE_LANGUAGES` now acts as the initial default for newly discovered language variants; later choices are stored in SQLite.

## 0.1.0 - 2026-08-09

Initial TrueNAS/Docker candidate.

- Main-Suwayomi AutoSourcarr extension for normal browse/search/details/chapters/pages.
- Private Suwayomi source-host for Mihon/Keiyoushi extensions.
- Hybrid live discovery + persistent unified catalog.
- Multi-title matching with corroborating-title boost.
- Source priorities and manual locked matches.
- Separate catalog and chapter-origin SQLite databases.
- Exact served-source history per virtual chapter.
- Whole-chapter preference over fractional parts when known page totals agree.
- Manual image-quality sampling.
- Sticky source-match matrix admin UI with search, match-count/gap/manual filters and sorting.
- Per-chapter candidate inspection and on-demand quality analysis in the admin UI.
- TrueNAS Install-via-YAML Compose variant with persistent datasets.
- Docker, PowerShell and GitHub Actions extension build paths.
- Compatibility updates for current Suwayomi GraphQL manga status enums and source `homeUrl`.
