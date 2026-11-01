# Checkpoint — 2026-11-01 05:30 CET (Phases 0–7 DONE; Phase 8 next)

STATUS: 0.5.0 · Phase 7/14 done · next Phase 8 Matching · 54% done (94/173 plan-h) · remaining to cutover 79 plan-h (≈ 15.2 h) · 0.6.0 after that ~35 plan-h (≈ 5.9 h)

RE-ESTIMATE #8 (1 Nov 05:30). Phase 7 took 1.25 h against 1.15. Measured pace phases 0–7: 16.0 h for 94 plan-h = 0.17 h per plan-h. Difficulty factors kept.

| Phase | plan-h | expected h | status |
| 0 Foundation | 15 | 3.5 actual | done |
| 1 Host and settings | 11 | 2.75 actual | done |
| 2 Data layer | 8 | 0.65 actual | done |
| 3 Jobs/events | 7 | 0.9 actual | done |
| 4 Extension host | 19 | 2.55 actual | done |
| 5 Accounts | 10 | 1.0 actual | done |
| 6 Sources/repair | 17 | 3.4 actual | done |
| 7 Connections | 7 | 1.25 actual (04:05–05:20) | done |
| 8 Matching | 21 | 3.6 | next |
| 9 Imports/metadata/sharing | 16 | 2.7 | |
| 10 Chapters/reader | 15 | 2.5 | |
| 11 Web UI finish | 6 | 1.5 (×1.5 Playwright) | |
| 12 Backup | 6 | 1.05 | |
| 13 Pipeline | 7 | 1.8 (×1.5 image builds) | |
| 14 Cutover | 8 | 2.0 (×1.5 real data, your command) | |
| 0.5.0 total | 173 | 16.0 done + ≈ 15.2 remaining | |
| 0.6.0 (15–17) | 35 | ≈ 5.9 | |

RULE (31 Oct): before a phase starts, write its checklist from (a) the plan's phase text and "Done when" (plan doc = Claude Docs 4cf80085-d5a6-40e1-9c4e-c40a0e4fbbba, search the node 9b1de541-77e7 with {"kind":"search","text":"<n> <Name>"}), (b) its requirement IDs (claude/requirements.md), (c) its port-map rows incl. section 0 route assignments, (d) the UI pages it must move to /api/v1, (e) phase end: green CI on `conversion`, ZIP in F:\claude projects\autosourcarr data, knowledge-DB records (device VM: `cd $HOME; cp "<data>/AutoSourcarr-knowledge.sqlite3" kdb.sqlite3; uv run --python 3.12 --no-project python kdb.py --db kdb.sqlite3 index <zip>; ... import <records.json>; ... check; cp back`; record states: implemented/verified/resolved/open, not "accepted"), port-map ticks (docs/port-map.md in the repo, uploaded to claude/port-map.md), PC sync (zip of `git diff --name-only <prev>..HEAD` minus .github → conversion-export, extracted in the device VM into ~/mnt/autosourcarr/autosourcarr), independent audit by a fresh agent, re-estimate with actual time, report.

Phases 0–5 closed (see earlier ZIPs/records). Phase 5: 2940c0c.

Phase 7 (04:05–05:20, closed): conversion 6acfff5 CI run 41272743279 green (both jobs); probe ce4ab0f (all source-host tests incl. interactive WebView + external solver fallback). ZIP autosourcarr-0.5.0-phase-7.zip (sha256 d19ebaea…); records RELEASE-0.5.0-PHASE-7, VERIFY-20261101-PHASE-7, DECISION-20261101-WEBVIEW-STREAM, FINDING-20261101-PHASE7-AUDIT, LIMIT-20261101-PHASE7-DEFERRED. PC synced (54 files). Audit: 17 findings, 15 fixed, 2 limitations. Checklist: (a) Connections menu ✓, solvers cookies-only + per-domain cache ✓, image CAPTCHAs → WebView ✓, MangaUpdates/AniList (30/min, Retry-After)/MangaDex ✓, Komf ✓, MalwareBazaar ✓, Suwayomi import (plan) + change feed ✓, UI ✓; Done when ✓. (b) CON-1,2,3,5 ✓ (CON-3 entries created in 8); CON-4 → 16. (c) ConnectionEndpoints ✓ (/services dropped). (d) Settings > Connections, source-webview page ✓.\n\nPhase 6 (23:25–02:50, closed): conversion 3cb753b + fix 73834a2 (user approved the extra push), CI 41267266674 green; CI run 41261104600: .NET job green (all C# tests + repair-runner check), Python job red: the regenerated UI client named a parameter `package` (reserved word in strict mode). Fixed locally in 73834a2 (generator renames reserved words; Python suite "No new failures (baseline 27)", project checks pass). The second push to `conversion` was blocked by the permission classifier → waiting for the user. Probe ci-probe green (36e072a): build, all C# tests, source-host check, source-host C# tests incl. the Done-when test, repair-runner check (22 PASS).
ZIP autosourcarr-0.5.0-phase-6.zip (sha256 3d0b15a8…, from 73834a2); records RELEASE-0.5.0-PHASE-6, VERIFY-20261101-PHASE-6, DECISION-20261101-DOMAIN-REPAIR, DECISION-20261101-LANDLOCK-ABI6, FINDING-20261101-PHASE6-AUDIT, LIMIT-20261101-PHASE6-DEFERRED. PC synced (88 + 2 files). Audit: 19 findings, 17 fixed, 2 limitations.
Checklist: (a) indexers ✓, APK drag-and-drop with preview ✓, Installed/Not installed orphans ✓, last-working kept ✓, structure check ✓, domain migration ✓, Landlock runner + proxy (no Via, honest UA, refuses without ABI 6) ✓, opt-in auto repair ✓, pacing ✓, UI pages ✓; Done when ✓ (moved-domain orphan repaired against the real host; runner cannot read /config or reach a non-allowed domain). (b) SRC-1,2,3,4,7,8,9 (pacing) ✓, REP-1..8 ✓; host as separate user → 13. (c) all phase-6 route groups on /api/v1 ✓, main-library status ✓, repair task projection ✓; SuwayomiLibrary 10/15 methods, 5 → phase 8. (d) UI pages ✓. (e) all except green conversion CI (push pending).
Deviations: domain repair runs no new code, so it installs through the source host without the runner (runner is for code rebuilds; binary into the image in 13). AD0001 (route-handler analyzer crash, intermittent in solution builds) kept as a warning in the Api project.

Carried into later phases: real Cloudflare check on the user's network; interactive Open in WebView (7); AI provider for repair + SRC-9 benchmark (7); translation health (10); Suwayomi plan → entries (8); metadata clients in matching (8); schedule_* job types (7–10); SuwayomiLibrary reset seeds / search-all / alias repair + library-monitor safe replay + reset stop/resume (8); import context in diagnostics (9); entry-id token in the reader source (10); cleanup after setup, restore stop/resume (12); hosted image, runner binary, host as separate user, native launcher removal (13); RuntimeMetrics call sites (7–10); page-first per list (8–10); burst notification via SET-11; two-factor/owner transfer (16); Postgres baselines (16).

Open decisions for the user: (2) custom C# test runner vs xUnit. (3) SET-5 default /config vs current /data. (1, host entry: default taken, stays in Api.) 

USER RULE (31 Oct): use GitHub only when really necessary. Compile/test on `ci-probe` (`git pull --no-rebase origin ci-probe` first, then `git merge -X theirs conversion`). Push `conversion` once per finished phase. Before pushing: scripts/check-project.sh (AUTOSOURCARR_SKIP_PYTEST=1, env from config/autosourcarr.env.example) AND scripts/ci-pytest.sh — after taking the regenerated api-client.js from ci-probe (the probe does not run the Python suite). Take regenerated lock files / openapi.json / api-client.js / docs/settings.md from ci-probe. Never disable JVM bytecode verification.

Open user items: delete old src\eu folders (stage 2), run scripts\stage3-remove-old.ps1, delete src\AutoSourcarr.Core\Text, deploy 0.4.5, optionally allow nuget/dotnet domains, delete branch ci-probe at the end.
Gates: 0.4.5 deploy (user), merge to main / ghcr publish, any NAS change, cutover, 0.6.0 release.
