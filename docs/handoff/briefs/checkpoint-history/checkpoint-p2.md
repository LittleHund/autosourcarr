# Checkpoint — 2026-10-31 18:47 CET (Phases 0, 1, 2 DONE; Phase 3 started)

STATUS: 0.5.0 · Phase 2/14 done · now Phase 3 jobs and events · 20% done (34/173 plan-h) · remaining to cutover 139 plan-h (≈ 42 h) · 0.6.0 after that ~35 plan-h (≈ 10 h)

RE-ESTIMATE #3 (31 Oct 18:47). Pace kept at 0.24 elapsed h per plan-h (phases 0–1). Phase 2 measured 18:06–18:45 ≈ 0.65 h for 8 plan-h (0.08 h/plan-h) — much faster than planned, but one short phase is not enough to lower the pace; it is re-checked after phase 3.

| Phase | plan-h | expected h | status |
| 0 Foundation | 15 | 3.5 actual | done |
| 1 Host and settings | 11 | 2.75 actual | done |
| 2 Data layer | 8 | 0.65 actual | done |
| 3 Jobs/events | 7 | 1.7 | in progress (start 18:47) |
| 4 Extension host | 19 | 9.1 (×2: Kotlin/JCEF built only in CI) | |
| 5 Accounts | 10 | 2.4 | |
| 6 Sources/repair | 17 | 6.1 (×1.5 Landlock in CI) | |
| 7 Connections | 7 | 1.7 | |
| 8 Matching | 21 | 5.0 | |
| 9 Imports/metadata/sharing | 16 | 3.8 | |
| 10 Chapters/reader | 15 | 3.6 | |
| 11 Web UI finish | 6 | 2.2 (×1.5 Playwright) | |
| 12 Backup | 6 | 1.4 | |
| 13 Pipeline | 7 | 2.5 (×1.5 image builds) | |
| 14 Cutover | 8 | 2.9 (×1.5 real data, your command) | |
| 0.5.0 total | 173 | 6.9 done + ≈ 42.4 remaining | |
| 0.6.0 (15–17) | 35 | ≈ 10 | |
% done = finished plan-h ÷ 173. Expected hours exclude your gates, deploys and waiting.

RULE (31 Oct): before a phase starts, write its checklist from (a) the plan's phase text and "Done when", (b) its requirement IDs, (c) its port-map rows incl. section 0 route assignments, (d) the UI pages it must move to /api/v1, (e) phase end: green CI on `conversion`, ZIP in F:\claude projects\autosourcarr data, knowledge-DB records (index ZIP + import records JSON, via device VM: `cd $HOME; cp "<data>/AutoSourcarr-knowledge.sqlite3" kdb.sqlite3; uv run --python 3.12 --no-project python kdb.py --db kdb.sqlite3 index <zip>; ... import <records.json>; ... check; cp back`), port-map ticks, PC sync (device_commit_files of the changed files to F:\claude projects\autosourcarr\autosourcarr\, .github excluded), independent audit by a fresh agent, re-estimate with actual time, report. Percent counts only ticked work. Record phase start/end times here.

Phase 0 (closed 31 Oct): ZIP autosourcarr-0.5.0-phase-0.zip (d5cbb20); records RELEASE-0.4.5, RELEASE-0.5.0-PHASE-0, VERIFY-20261031-PHASE-0, LIMIT-20261031-TEST-RUNNER, FINDING-20261031-PHASE-RITUAL.

Phase 1 (closed 31 Oct 17:55): conversion 3e63ee3, CI 41221012992. ZIP autosourcarr-0.5.0-phase-1.zip; records RELEASE-0.5.0-PHASE-1, VERIFY-20261031-PHASE-1, DECISION-20261031-SETTINGS-PRECEDENCE, FINDING-20261031-PHASE1-AUDIT, LIMIT-20261031-PHASE1-DEFERRED. PC synced.

Phase 2 (18:06–18:45, closed): conversion 3f85715, CI run 41225208045 green, probe 927/927 tests. ZIP autosourcarr-0.5.0-phase-2.zip (sha256 6b78d958…); records RELEASE-0.5.0-PHASE-2, VERIFY-20261031-PHASE-2, DECISION-20261031-LAST-SEEN-MEANING, FINDING-20261031-PHASE2-AUDIT, LIMIT-20261031-PHASE2-DEFERRED. PC synced (39 files).
Checklist (all ticked): (a) FluentMigrator migrations for all tables on SQLite ✓; Dapper under DatabaseSession ✓; page-first API (QueryPage) ✓, per-list adoption in 8–10; changed-rows-only writes ✓; engine-neutral export = CatalogueFormat (exists, routes in 9) ✓; two DBs separate ✓; Done when: repository tests pass ✓, 0.3.27 + 0.4.4 upgrade ✓. (b) PERF-5, SET-5, META-6 ✓. (c) db.py ✓, storage/database/move ✓. (d) Settings > Database storage move ✓. (e) all ✓.
Audit: 7 findings → 6 fixed (WAL after move/start, symlink check + instance lock on move target, IMMEDIATE nested write scoped to its call, live paths in status, UI note), 1 accepted (last_seen = last changed).

Phase 3 checklist (jobs and events, 7 plan-h):
(a) job queue with Sonarr rules: 3 workers, High/Normal/Low then oldest, identical jobs merged, needs-disk / exclusive / long-running flags; run limit and ETA per type; watchdog (frozen = error); back-off; one event stream with IDs → SSE live updates (heartbeat, X-Accel-Buffering: no), task history and diagnostics; grouped errors. Done when: load test shows no type starved, frozen jobs become errors, live updates arrive through nginx.
(b) PERF-1, PERF-2, PERF-3, PERF-8, PERF-10, UI-13.
(c) port-map: async_utils.py, due_scheduler.py (DueScheduler new), read_lane.py, task_journal.py, task_registry.py; Jobs section 20 service methods; TaskEndpoints 4 routes (GET /api/admin/tasks, clear-history, {id}/cancel, {id}/retry-safe); DiagnosticEndpoints diagnostics, errors, errors/clear; database move as a queued task (from phase 2 audit); RuntimeMetrics call sites for jobs.
(d) UI: task list (Ongoing/Completed/Failed), System diagnostics/errors move to /api/v1 + SSE.
(e) phase-end steps as in RULE.

Carried into later phases: Host entry point + source-engine health (4); main-library status (6); main Suwayomi/translation health (7); cleanup after setup (12); RuntimeMetrics call sites (3–10); page-first per list (8–10); Postgres baselines (16).

Open decisions for the user: (1) Host entry point in phase 4. (2) custom C# test runner vs xUnit. (3) SET-5 default /config vs current /data.

Plan docs: requirements https://claude.ai/artifact/Ww9SEgJQNF8tgGJ1mXBPmb ; conversion plan https://claude.ai/artifact/AWFuRGdUmm8pdBAqpxMnky (its phase table still shows the original hours).

USER RULE (31 Oct): use GitHub only when really necessary. Compile/test on `ci-probe` (probe.yml always succeeds → no mail; `git pull` it first, then `git merge -X theirs conversion`). Push `conversion` once per finished phase. Run scripts/check-project.sh locally before pushing (AUTOSOURCARR_SKIP_PYTEST=1). Take regenerated lock files / openapi.json / api-client.js from ci-probe.

Open user items: delete old src\eu folders (stage 2), run scripts\stage3-remove-old.ps1, delete src\AutoSourcarr.Core\Text, deploy 0.4.5, optionally allow nuget/dotnet domains, delete branch ci-probe at the end.
Gates: 0.4.5 deploy (user), merge to main / ghcr publish, any NAS change, cutover, 0.6.0 release.
