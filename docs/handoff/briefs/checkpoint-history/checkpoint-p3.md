# Checkpoint — 2026-10-31 19:45 CET (Phases 0–3 DONE; Phase 4 next)

STATUS: 0.5.0 · Phase 3/14 done · now Phase 4 extension host (started 19:45) · 24% done (41/173 plan-h) · remaining to cutover 132 plan-h (≈ 32 h) · 0.6.0 after that ~35 plan-h (≈ 7 h)

RE-ESTIMATE #4 (31 Oct 19:45). Phases 2 and 3 both finished well under their estimate (0.65 h vs 1.9; 0.9 h vs 1.7), so the pace is lowered to the measured average of phases 0–3: 7.8 h for 41 plan-h = 0.19 h per plan-h. Difficulty factors kept (CI-only builds and real-data steps).

| Phase | plan-h | expected h | status |
| 0 Foundation | 15 | 3.5 actual | done |
| 1 Host and settings | 11 | 2.75 actual | done |
| 2 Data layer | 8 | 0.65 actual (18:06–18:45) | done |
| 3 Jobs/events | 7 | 0.9 actual (18:47–19:40) | done |
| 4 Extension host | 19 | 7.2 (×2: Kotlin/JCEF built only in CI) | next |
| 5 Accounts | 10 | 1.9 | |
| 6 Sources/repair | 17 | 4.8 (×1.5 Landlock in CI) | |
| 7 Connections | 7 | 1.3 | |
| 8 Matching | 21 | 4.0 | |
| 9 Imports/metadata/sharing | 16 | 3.0 | |
| 10 Chapters/reader | 15 | 2.9 | |
| 11 Web UI finish | 6 | 1.7 (×1.5 Playwright) | |
| 12 Backup | 6 | 1.1 | |
| 13 Pipeline | 7 | 2.0 (×1.5 image builds) | |
| 14 Cutover | 8 | 2.3 (×1.5 real data, your command) | |
| 0.5.0 total | 173 | 7.8 done + ≈ 32 remaining | |
| 0.6.0 (15–17) | 35 | ≈ 7 | |
% done = finished plan-h ÷ 173. Expected hours exclude your gates, deploys and waiting.

RULE (31 Oct): before a phase starts, write its checklist from (a) the plan's phase text and "Done when", (b) its requirement IDs, (c) its port-map rows incl. section 0 route assignments, (d) the UI pages it must move to /api/v1, (e) phase end: green CI on `conversion`, ZIP in F:\claude projects\autosourcarr data, knowledge-DB records (index ZIP + import records JSON, via device VM: `cd $HOME; cp "<data>/AutoSourcarr-knowledge.sqlite3" kdb.sqlite3; uv run --python 3.12 --no-project python kdb.py --db kdb.sqlite3 index <zip>; ... import <records.json>; ... check; cp back`), port-map ticks, PC sync (device_commit_files of the changed files to F:\claude projects\autosourcarr\autosourcarr\, .github excluded), independent audit by a fresh agent, re-estimate with actual time, report. Percent counts only ticked work. Record phase start/end times here.

Phase 0 (closed 31 Oct): ZIP autosourcarr-0.5.0-phase-0.zip (d5cbb20); records RELEASE-0.4.5, RELEASE-0.5.0-PHASE-0, VERIFY-20261031-PHASE-0, LIMIT-20261031-TEST-RUNNER, FINDING-20261031-PHASE-RITUAL.

Phase 1 (closed 31 Oct 17:55): conversion 3e63ee3, CI 41221012992. ZIP autosourcarr-0.5.0-phase-1.zip; records RELEASE-0.5.0-PHASE-1, VERIFY-20261031-PHASE-1, DECISION-20261031-SETTINGS-PRECEDENCE, FINDING-20261031-PHASE1-AUDIT, LIMIT-20261031-PHASE1-DEFERRED. PC synced.

Phase 2 (18:06–18:45, closed): conversion 3f85715, CI run 41225208045 green, probe 927/927 tests. ZIP autosourcarr-0.5.0-phase-2.zip (sha256 6b78d958…); records RELEASE-0.5.0-PHASE-2, VERIFY-20261031-PHASE-2, DECISION-20261031-LAST-SEEN-MEANING, FINDING-20261031-PHASE2-AUDIT, LIMIT-20261031-PHASE2-DEFERRED. PC synced (39 files).
Checklist (all ticked): (a) FluentMigrator migrations for all tables on SQLite ✓; Dapper under DatabaseSession ✓; page-first API (QueryPage) ✓, per-list adoption in 8–10; changed-rows-only writes ✓; engine-neutral export = CatalogueFormat (exists, routes in 9) ✓; two DBs separate ✓; Done when: repository tests pass ✓, 0.3.27 + 0.4.4 upgrade ✓. (b) PERF-5, SET-5, META-6 ✓. (c) db.py ✓, storage/database/move ✓. (d) Settings > Database storage move ✓. (e) all ✓.
Audit: 7 findings → 6 fixed (WAL after move/start, symlink check + instance lock on move target, IMMEDIATE nested write scoped to its call, live paths in status, UI note), 1 accepted (last_seen = last changed).

Phase 3 (18:47–19:40, closed): conversion 7bbc672, CI run 41229924104 green (first push 3f17e1f failed in the Python suite's UI harnesses; fixed). Probe 954/954. ZIP autosourcarr-0.5.0-phase-3.zip (sha256 a3d685aa…); records RELEASE-0.5.0-PHASE-3, VERIFY-20261031-PHASE-3, DECISION-20261031-SSE-NOT-SIGNALR, FINDING-20261031-PHASE3-AUDIT, LIMIT-20261031-PHASE3-DEFERRED. PC synced (27 files).
Checklist (all ticked): (a) job queue with Sonarr rules + run limit/ETA per type ✓, watchdog ✓, back-off ✓, one event stream with ids → SSE (heartbeat, X-Accel-Buffering) ✓, task history (TaskJournal wired) ✓, diagnostics ✓, grouped errors ✓; Done when: load test no starvation ✓, frozen = error ✓, live updates through nginx ✓ (CI installs nginx, AUTOSOURCARR_REQUIRE_NGINX=1). (b) PERF-1/2/3/8/10, UI-13 ✓. (c) due_scheduler, task_registry, task_journal ✓; Jobs section infrastructure ✓, schedule_* callers → 6–10; TaskEndpoints 4 + jobs ✓; diagnostics/errors/errors-clear ✓; move as job ✓; job_run metric ✓. (d) tasks, errors, diagnostics, safe retry, live updates on /api/v1 ✓. (e) all ✓.
Audit: 10 findings → 9 fixed, 1 scoped (stop/resume callers come with resets in 8 and restores in 12).
PRE-PUSH ROUTINE (from phase 3): check-project.sh AND scripts/ci-pytest.sh locally (the Python job runs UI harnesses that check app.js).

Phase 4 (start 19:45) checklist:
(a) plan: Kotlin host on Java 25 from Suwayomi v2.4.2366 (commit 40bc5da5…) loader + extension-store code; loads Tachiyomi 1.4 and current Mihon extensions; search, details, chapters, pages; JCEF WebView with own Xvfb (UA presets, clear cookies); source back-off 30 min, 2 h, 6 h, 12 h; Suwayomi-bound C# (SuwayomiClient, SourceEngineController, NativeSourceOwnerConnection) retired except a slim import client; re-estimate afterwards. Done when: 10 Keiyoushi sources search and load pages without Suwayomi, a 1.4 extension still works, a Cloudflare source passes via WebView.
(b) requirement IDs: read the SRC/EXT section of requirements.md at start of 4b.
(c) port-map: SourceRuntimeEndpoints + engine/compatibility (9 routes), child_process.py, Host entry point (Host project becomes the web entry; native resolver/PID1 launcher retired), source-engine health in /api/v1/health.
(d) UI pages using those routes → /api/v1.
(e) phase-end steps as in RULE.
Build approach (decided 19:50): no Maven access from the cloud container (GitHub only), so Kotlin builds run in the ci-probe workflow. New folder `source-host/` = one Gradle module copied into a pinned Suwayomi checkout as subproject `:source-host` (reuses Suwayomi's version catalog and buildSrc; depends on `:AndroidCompat` + `:AndroidCompat:Config` only). Tachiyomi API classes (`eu.kanade.tachiyomi.*`, Apache-2.0) and the loader utilities (PackageTools, ChildFirstURLClassLoader, BytecodeEditor, AndroidManifestParser, OkHttp helpers; MPL-2.0) are vendored into source-host with the Suwayomi server/DB/config couplings replaced by a small HostConfig. HTTP API (REST, JSON) replaces the GraphQL operations the C# uses (sources, source preferences, extensions list/fetch/install/update/uninstall, browse search/popular/latest, manga details + chapters, pages, health). WebView: KCEF as in Suwayomi (CEFManager/KcefWebView) under Xvfb.
Sub-steps: 4a host skeleton builds in probe → 4b vendored loader + REST API + C# client swap → 4c WebView/Cloudflare + back-off → 4d CI integration test against real Keiyoushi sources (10 + a 1.4 extension + a Cloudflare source) → 4e retire Suwayomi process management in C#, Host entry point, health → audit and phase end.
PHASE 4 PROGRESS (31 Oct ~21:00):
- 4a done: source-host/ (Kotlin, vendored eu.kanade.tachiyomi API + Suwayomi loader files, our autosourcarr.host package), built by scripts/build-source-host.sh inside Suwayomi commit 40bc5da5 as :source-host (shadow jar with Suwayomi's GraalJS exclude), probe job builds + runs scripts/source-host-check.py.
- 4b host side done: id-based Suwayomi-shaped REST (/v1/sources, /v1/sources/{id}/mangas?type=, /v1/mangas/{id}/fetch, /v1/chapters/{id}/pages, /api/v1/manga/{id}/thumbnail and /chapter/{order}/page/{n}, /v1/extensions [+fetch, install-url, install-file, {pkg}/install|update|uninstall], /v1/extension-stores, /v1/library, /v1/mangas/{id}/library, /v1/import-ids, preferences, back-off), SQLite id store (HostDb), repos (index.pb + legacy JSON), back-off ladder 30m/2h/6h/12h after 3 failures.
- R8 problem (Keiyoushi builds are R8-optimised): JVM VerifyErrors after dex2jar. Fixed WITH verification on (disabling verification was refused by the tool as security weakening; not used): AutoSourcarrDex2jar (port of dex2jar doTranslate) retargets new-X/superclass-ctor and skipped super ctors + ExtensionConstructors adds forwarding ctors; corrected getCommonSuperClass (dex2jar never compared type2 with type1's ancestors); ExtensionBytecodeRepair (verifier-guided checkcasts) as a safety net.
- Result probe 31 Oct ~21:00: MangaDex, MangaPill, Weeb Central, Mangatown, Webtoons — 5/5 install → search → details → chapters → pages → image.
- Next: libVersion metadata parse ({4:...} resource refs), C# client swap (SuwayomiClient GraphQL → host REST, keep validations), 4c WebView/Cloudflare (KCEF + Xvfb), 4d 10 sources + a 1.4 APK + a Cloudflare source in CI, 4e retire Suwayomi process mgmt + Host entry + health.



Carried into later phases: Host entry point + source-engine health (4); main-library status, repair task projection in /api/v1/tasks (6); main Suwayomi/translation health (7); schedule_* job types (6–10); library-monitor safe replay + reset stop/resume (8); import context in diagnostics (9); cleanup after setup, restore stop/resume (12); RuntimeMetrics call sites (4–10); page-first per list (8–10); Postgres baselines (16).

Open decisions for the user: (1) Host entry point in phase 4. (2) custom C# test runner vs xUnit. (3) SET-5 default /config vs current /data.

Plan docs: requirements https://claude.ai/artifact/Ww9SEgJQNF8tgGJ1mXBPmb ; conversion plan https://claude.ai/artifact/AWFuRGdUmm8pdBAqpxMnky (its phase table still shows the original hours).

USER RULE (31 Oct): use GitHub only when really necessary. Compile/test on `ci-probe` (probe.yml always succeeds → no mail; `git pull` it first, then `git merge -X theirs conversion`). Push `conversion` once per finished phase. Run scripts/check-project.sh locally before pushing (AUTOSOURCARR_SKIP_PYTEST=1). Take regenerated lock files / openapi.json / api-client.js from ci-probe.

Open user items: delete old src\eu folders (stage 2), run scripts\stage3-remove-old.ps1, delete src\AutoSourcarr.Core\Text, deploy 0.4.5, optionally allow nuget/dotnet domains, delete branch ci-probe at the end.
Gates: 0.4.5 deploy (user), merge to main / ghcr publish, any NAS change, cutover, 0.6.0 release.
