# AutoSourcarr C#/.NET conversion plan

Oct 27, 2026 · @Hund

## Summary

The work is two releases: **0.5.0, the conversion, about 147 working hours (range 120–195 h)** in 15 phases, after which the Python app is retired; then **0.6.0, the new features, about 35 h** in 3 more phases. Total about **182 h** (was 193). Conversion first: nothing that is new and unrelated to the conversion is built before the cutover. All accepted proposals (P1–P6), review changes (S1–S7), research findings (R1–R28), time reductions (T1–T8, PROP-1–4) and the five sequencing moves of 29 Oct are built into the sections below; the sections at the end keep the reasoning and sources.

- Calendar estimate: 0.5.0 in about **6 weeks** at roughly 5 productive hours a day (range 5–8 weeks), 0.6.0 about 1 week more. Session limits and checks that need your PC are the main variables.
- 0.5.0 contains everything the Python app does today, standalone operation (extension host, WebView, downloads), the security essentials (accounts, roles, keys, cross-site protection, Landlock repair runner), the new API and UI pages, backup with rollback, and the pipeline.
- 0.6.0 contains guests and requests, two-factor and owner transfer, notifications, scheduled backups and recovery codes, Postgres, the AI features and profile sharing. These are built on the finished C# app, with no Python left.
- Basis: the paused migration (stages 1–26) is reused where it fits the new layout; its matching core, sharing, merge/split, reader serving, APK checks and repair protocol are kept as they are.
- Phase 0 starts with Python 0.4.5, which closes the open Suwayomi port. Every stage is built and tested by GitHub Actions on the public `conversion` branch. Merging to `main`, publishing an image and anything on the NAS wait for your go.
- **Nothing starts until you say "start the conversion".**

## Prerequisites before start

These are needed on day one; phase 0 stops if any is missing.

- [x] Connect `F:\claude projects\autosourcarr` and `F:\claude projects\autosourcarr data` to this chat (desktop app, "Add folder").
- [x] Run `cleanup-autosourcarr.ps1` and `kb-maintenance.py --apply` (still pending from 2026-10-26).
- [x] .NET 10 SDK on the PC (`dotnet --version`), or confirm builds run only in the GitHub pipeline.
- [x] Shorten the Kotlin package path (10–13 folders deep) so file tools can reach it, or allow me to move it.
- [x] GitHub repo `LittleHund/autosourcarr` reachable with push rights, and ghcr.io publishing allowed (needed from phase 14).
- [x] A copy of a live `catalog.sqlite` / `chapter-origins.sqlite` (no keys) for the migration test in phase 15.

## Approach and key design choices

Build the new design directly in C#, not a 1:1 port followed by a redesign: Changed requirements go in as each area is converted, so nothing is written twice. The Python code is ported by feature through a function-to-class map, not file by file (`service.py` alone is 1.1 MB).

| Area | Choice | Why |
| --- | --- | --- |
| Solution layout | Feature folders as in Sonarr's `NzbDrone.Core`: `Core` (one folder per area: Sources, Repair, Matching, Catalogue, Imports, Reader, Accounts, Guests, Jobs, Settings, Backup, Notifications, each with its models, logic and data access), `Api` (one endpoint file per area), `Host` (startup), `Repair.Runner` (separate low-rights process), Kotlin `ExtensionHost`, `Tests` mirroring Core | Everything about one feature sits in one folder; easy to review (S1) |
| Review rules | One public class per file; files under about 500 lines (partial files above that); a 5–10 line README per feature folder; test classes named after the class they test | Human-navigable code (S5) |
| Database | SQLite default, Postgres optional; one FluentMigrator 8 migration set with `application_id` + `user_version` stamp; the two runtime databases stay separate files | ARC-9, review #5; separate files keep chapter-origin writes from blocking catalogue reads |
| Data access | Dapper with page-first queries and changed-rows-only writes, replacing the hand-written row mapping | PERF-5 |
| API | Versioned `/api/v1`, described by OpenAPI, one consistent format; the UI uses a typed client generated from it; no old formats kept | P4 (clean form) |
| Live updates | Server-Sent Events built into .NET 10, fed by the single event stream with event IDs for catch-up; sends `X-Accel-Buffering: no` and a 30 s heartbeat so it works behind Nginx Proxy Manager and Authelia | UI-13, PERF-8; no client library needed (R8, R17) |
| Job queue | Sonarr's rules: 3 workers (tuned by load test), priority High (manual) / Normal (scheduled) / Low (maintenance), then oldest first; identical jobs merged; flags "needs disk", "exclusive", "long-running" | PERF-1–3 (R13) |
| Settings | One schema class generates `config.json`, wizard, validation, redaction, Compose variable docs and the update change list | SET-7, ARC-6, CON-5 |
| Extension runtime | Headless JVM extension host inside the image: Kotlin on Java 25 (LTS to 2031), built from Suwayomi v2.4.2366's loader and extension-store code, loading both Tachiyomi 1.4 and current Mihon extensions; no Suwayomi web server; AOT class cache from a build-time training run, kept only if the boot-time gate shows a gain | ARC-1, PIPE-7, N3 (R1, R3, R9, R26) |
| WebView and CAPTCHA | The host's JCEF browser (Xvfb started inside the container) is the one browser in the image: WebView, "Open in WebView" streamed into the UI, and the built-in solver; FlareSolverr and Byparr share one "FlareSolverr-compatible" connection that fetches cookies only and caches them per domain | SRC-5, CON-2 (R6, R21, R24, R25) |
| Repair and WebView runner | Child process locked down with Landlock (files: its own work folder only; network: only a local proxy that allows the source's domains and adds no `Via` header), `no_new_privs` and rlimits; refuses to run if the kernel lacks Landlock network rules; replaces the VM runner | REP-5, ARC-8; no host permissions; TrueNAS 25.10 runs Linux 6.12 (R4, R7, R28) |
| File formats | Repo `index.pb` and Mihon backups parsed by code generated from a `.proto` schema the pipeline derives from Mihon's backup classes each release | IMP-11, PIPE-9 (R2, R12) |
| Downloads | `Series/Chapter.cbz` + `cover.jpg` + `ComicInfo.xml`, readable by Mihon's local source, Kavita and Komga | CHAP, READ (R11, R22) |
| Metadata | MangaUpdates, AniList (paced to 30 requests/min) and MangaDex built in; Komf as optional connection for its other providers | META, CON-1, ARC-1 (R19, R27) |
| Web UI | Keep the vanilla JS UI (Sonarr v5 layout); one theme module with dark and light variants of every accent and a stronger border colour for controls; JS/CSS bundled and minified in CI (esbuild, not in the image) | UI-1–15, WCAG AA (R14, R15) |
| Image | One multi-stage image: ReadyToRun .NET 10 app, `jlink` Java 25 runtime, JCEF + Xvfb; no Python, no full Suwayomi server | PIPE-1, PERF-7 (S7, R5, R22) |
| Tests | xUnit (`dotnet test`), Playwright screenshots in the official Playwright image, contrast test, load and boot-time gates | PIPE-5, PIPE-6, PIPE-8 |
| Python | Removed at cutover; only kept if a step has no .NET equivalent, then reported | ARC-10 |

Working method (DEV-1 to DEV-5): one executor, one stage at a time. Each stage is pushed to the `conversion` branch, built and tested by GitHub Actions, and I read the result before the next stage. A release ZIP and knowledge-DB record are made at every phase checkpoint.

## Phases

Percent complete is the share of 0.5.0's estimated hours finished (the cutover is 100%); 0.6.0 is tracked the same way afterwards. Totals are recalculated when a phase's real time differs from its estimate, and once more after phase 4.

| # | Phase | Est. h | Done at % | Requirements |
| --- | --- | --- | --- | --- |
| 0 | Python 0.4.5 (closes Suwayomi port), Kotlin path, feature-folder restructure, port map, version check, CI skeleton, real-data export, green build | 15 | 10 | ARC-10, ARC-11, AUTH-5, PIPE-6 |
| 1 | Host, `config.json`, settings schema, env conversion, `/api/v1` + OpenAPI + generated UI client | 11 | 18 | ARC-5–7, SET-5, SET-7, CON-5 |
| 2 | Data layer: SQLite, FluentMigrator, schema stamp, Dapper, speed layer | 7 | 22 | PERF-5, META-6 (format) |
| 3 | Job queue (Sonarr rules), watchdog, event stream, SSE live updates, task list | 6 | 27 | PERF-1–3, PERF-6, PERF-8, PERF-10, UI-13 |
| 4 | Java 25 extension host (1.4 + current extensions), JCEF WebView, source back-off; Suwayomi-bound C# retired | 16 | 37 | ARC-1, ARC-3, SRC-5, SRC-6, CHAP-7, PIPE-7 |
| 5 | Accounts essentials on ASP.NET Core auth: login types, owner, roles, keys, security log, server reset + UI pages | 10 | 44 | AUTH-1–6, AUTH-8, AUTH-10 |
| 6 | Repos, sources, APK checks, health, Landlock repair runner + UI pages | 14 | 54 | SRC-1–4, SRC-7–9 (no benchmark), REP-1–8 |
| 7 | Connections: solver, metadata providers, Komf, MalwareBazaar, Suwayomi import + UI pages | 6 | 58 | CON-1–3, CON-5 |
| 8 | Matching: existing core + profiles, decision log, duplicates test set + UI pages | 15 | 68 | MAT-1–6, MAT-8–20, MAT-22 |
| 9 | Imports (`.tachibk` + next Mihon format), metadata, sharing format + UI pages | 12 | 76 | IMP-1–11, META-1–5, META-7 |
| 10 | Chapters, serving, CBZ + ComicInfo downloads, reader extension from own repo index + UI pages | 10 | 83 | CHAP-1–6, READ-1–4 |
| 11 | Web UI finish: layout, one theme module (WCAG AA unit test), wizard, bundling | 5 | 86 | UI-1–14, SET-1, SET-2, PERF-4, PERF-9 |
| 12 | Backup/restore format and automatic backup with rollback around updates | 5 | 90 | SET-3, SET-4, SET-6, SET-8, SET-9 |
| 13 | Pipeline completion: image, pins, browser/contrast/boot-time gates, backup-schema watch | 7 | 95 | PIPE-1–7, PIPE-9, ARC-2, ARC-4, ARC-8, PERF-7 |
| 14 | Migrate 0.4.x data, parity check, cutover, release 0.5.0 | 8 | 100 | ARC-7, IMP-9 |
|  | **0.5.0 total** | **147** |  |  |
| 15 | Guests, requests, reports, voting, find and merge, cached guest pages, load benchmark gate (0.6.0) | 14 | 40 | GST-1–9, PERF-11, PIPE-8 |
| 16 | Two-factor, owner transfer, AI providers and benchmark, Postgres, theme-grouped repair (0.6.0) | 10 | 69 | AUTH-7, AUTH-9, CON-4, SRC-9, MAT-21, ARC-9, META-6 |
| 17 | Notifications incl. web push, PWA install, QR reader setup, scheduled backups, recovery codes, profile sharing, release 0.6.0 | 11 | 100 | SET-10–12, UI-15, READ-5, MAT-7 |
|  | **0.6.0 total** | **35** |  |  |
|  | **Grand total** | **182** |  |  |

## Phase details

Each phase ends with a green CI run, a ZIP in `autosourcarr data`, a knowledge-DB record and a short report. From phase 1 on, each phase also moves its own UI pages onto the new API. "Done when" is the exit check.

**0 Baseline (15 h).** (1) Python 0.4.5: Suwayomi host port removed, "Open in WebView" on entries (you deploy it). (2) Shorten the Kotlin package path. (3) Restructure stages 1–26 into feature folders and write the Python function → C# class port map. (4) Check the versions behind R1–R28 (Mihon, Suwayomi, .NET, Java) and note what changed. (5) GitHub Actions skeleton on the `conversion` branch (build, xUnit, Postgres service); export your merge/reject decisions and a 1,000-entry sample (no keys or accounts). Also: port the 0.4.4 fixes, the schema stamp and the `tag_reviews.right_id` index. Done when: CI is green on Linux and every Python function has a target in the port map.

**1 Host and settings (11 h).** Settings schema class; `config.json` with encrypted secrets; one-time `autosourcarr.env` conversion kept as backup; Compose override variables; update change list. `/api/v1` with OpenAPI, the generated typed UI client, source-generated JSON and `[GeneratedRegex]`. Done when: an existing 0.4.4 env converts with no lost value, and the UI client is regenerated by CI from the API.

**2 Data layer (7 h).** FluentMigrator migrations for all tables on SQLite; Dapper repositories with page-first queries and changed-rows-only writes; engine-neutral export format; the two runtime databases stay separate. Postgres comes in phase 16 on the same migrations. Done when: the repository tests pass, and a 0.3.x and a 0.4.4 database upgrade cleanly.

**3 Jobs and events (6 h).** Job queue with Sonarr's rules (3 workers, High/Normal/Low then oldest, identical jobs merged, needs-disk/exclusive/long-running flags) plus run limit and ETA per type; watchdog; back-off; one event stream with IDs feeding SSE live updates (heartbeat, `X-Accel-Buffering: no`), task history and diagnostics; grouped errors. Done when: a load test shows no job type starved, frozen jobs become errors, and live updates arrive through nginx.

**4 Extension host (16 h).** Kotlin host on Java 25 from Suwayomi v2.4.2366's loader and extension-store code; loads Tachiyomi 1.4 and current Mihon extensions; source search, details, chapters and pages; JCEF WebView with its own Xvfb (user-agent presets, clear cookies); back-off 30 min, 2 h, 6 h, then 12 h; the Suwayomi-bound C# (`SuwayomiClient`, `SourceEngineController`, `NativeSourceOwnerConnection`) retired except a slim import client. Re-estimate the remaining phases afterwards (P6). Done when: 10 Keiyoushi sources search and load pages without Suwayomi, a 1.4 extension still works, and a Cloudflare source passes via WebView.

**5 Accounts essentials (10 h).** Built on ASP.NET Core's cookie authentication, authorization policies and password hasher: Basic, Forms and External login; owner on first boot; roles checked in one place; Keys page with per-device reader keys; cross-site write guard on every write; failed-login slow-down and security log; server-side owner reset; their UI pages. Two-factor and owner transfer follow in phase 16. Done when: every API route is covered by a role test and the four reset routes reject cross-site posts.

**6 Sources and repair (14 h).** Repos and sources as indexers (generated `index.pb` parser); APK drag-and-drop using the existing manifest and archive readers plus the domain and native-code rules; separate Installed/Not installed orphan headers; last-working code kept; structure check before repair; domain/API migration; Landlock repair runner with the domain-filtering proxy (no `Via` header, honest user agent), refusing to run without Landlock network rules; opt-in auto repair; paced source traffic; their UI pages. Done when: a known orphan is repaired and passes content checks, and the runner cannot read `/config` or reach a non-allowed domain.

**7 Connections (6 h).** One Connections menu (add, test, enable): FlareSolverr-compatible solver (FlareSolverr or Byparr, cookies only, per-domain cache); image CAPTCHAs sent to the built-in WebView; MangaUpdates, AniList (30 requests/min, `Retry-After`) and MangaDex; Komf; MalwareBazaar hash lookup; Suwayomi one-time import and change feed; their UI pages. AI providers and Postgres connections follow in phase 16. Done when: each connection type has a passing "Test" against a stub.

**8 Matching (15 h).** The existing C# scorer (`TextMatcher`, `IndelSimilarity`, alias and summary matching) kept and extended with the missing-word penalty at the 0.94 floor; identity evidence (author, year, cover); edition separation; profiles (ready-made, simple, advanced) with the default ranking official publisher sources first, then health and quality; scaled mixed-entry check; tag synonyms; one decision log with actor; duplicates test set from your exported decisions; their UI pages. Profile export/import follows in phase 17. Done when: the test set scores no wrong merges, and manual decisions survive a migration.

**9 Imports, metadata, sharing (12 h).** Import layout; Match/Search/Skip/Retry with latest click wins; fallback search without labels; `.tachibk` and Mihon's next backup format through the generated parser (hand-maintained schema, updated when the release watch reports a change); merge-log recheck after reset; per-field official-first metadata with overrides; the existing sharing, portable identity and merge/split code with a versioned format header and "Version mismatch: version X required"; their UI pages. Done when: a sample backup of every supported version imports and a crash leaves no stuck rows.

**10 Chapters and reader (10 h).** Numbering words and part order; season continuation naming; group priority; quality score and upgrades; the existing reader serving kept; downloads as `Series/Chapter.cbz` + `cover.jpg` + `ComicInfo.xml`; prebuilt reader extension signed with your existing key (added as a GitHub secret by you) and offered from AutoSourcarr's own repo index; 5 server slots; per-source downloads; Authelia and Nginx Proxy Manager examples; their UI pages. Done when: Mihon reads from two servers with separate device keys, and a downloaded series opens in Mihon's local source and Kavita.

**11 Web UI finish (5 h).** Responsive layouts and touch targets; one theme module replacing the two current palette systems, with dark and light variants of all six accents and a stronger control border (today: borders 1.4–1.7:1, light-style accents 1.5–2.1:1), checked by a unit test on the palette table; wizard required-then-optional; JS/CSS bundled and minified in CI; cache limit; diagnostics download. Done when: Playwright passes at phone, tablet and desktop in every theme and the contrast test reports AA everywhere.

**12 Backup and rollback (5 h).** Versioned backup format for settings, profiles and catalogues (secrets excluded); automatic backup before every update or migration with one-click rollback; old-file removal; their UI pages. Scheduled backups, notifications and recovery codes follow in phase 17. Done when: a backup restores on a clean instance and a failed update rolls back.

**13 Pipeline completion (7 h).** Multi-stage image: ReadyToRun .NET 10, `jlink` Java 25 runtime, JCEF + Xvfb, AOT class cache from a training run; NuGet and Gradle caches, Kotlin host built only when its files change, Playwright only when UI files change; pinned digests and hashes; image to `ghcr.io/littlehund/autosourcarr` (public); compatibility list; Playwright screenshots with diff in the official Playwright image; contrast and boot-time gates; Mihon release watch that reports backup-schema changes; job-queue tuning. Done when: one pipeline run produces the image and reader extension, with boot under 5 min measured.

**14 Cutover (8 h).** Migrate a snapshot of your 0.4.x data on your PC (you run one command); compare counts, manual decisions and subscriptions with Python; release 0.5.0 with CHANGELOG; deploy only after your explicit go; the Python app is retired. Done when: zero lost manual decisions and subscriptions in the comparison.

**15 Guests (14 h, release 0.6.0).** Guest login and device tokens; requests with auto-approve and limits; reports; merge voting with accuracy; find and merge page; my requests page; quick-review mode; cached read-only guest pages; their UI pages. Done when: 50 simulated guests do not slow admin page loads beyond the PIPE-8 budget; the load benchmark becomes a pipeline gate here.

**16 Two-factor, AI, Postgres, repair grouping (10 h, release 0.6.0).** Two-factor login with 8 recovery codes per user; owner recovery and transfer; AI provider connections with device-code or login-page sign-in, the opt-in AI benchmark and provisional AI chapter search; Postgres as a connection on the phase 2 migrations; theme-grouped repair (one fix tried on every orphan of a Keiyoushi theme, each passing its own content check); their UI pages. Done when: both engines pass the same repository tests, and device-code sign-in shows a code.

**17 Notifications, PWA and sharing (11 h, release 0.6.0).** Notifications (Discord, webhook, email, web push) with grouping and quiet hours; web push enabled from a button in the installed app; scheduled backups with retention; PWA install with HTTPS check; QR/link reader setup per server; recovery codes that unlock secrets in backups; matching-profile export and import as versioned files; release 0.6.0 with CHANGELOG. Done when: a backup restores on a clean instance with secrets via a recovery code.

## Progress and ETA reporting

Every update during the conversion starts with one status line, from the first stage of phase 0 to the 0.5.0 release:

```
0.5.0 · Phase 4/14 · Extension host · stage 3/6 · 31% done · stage ETA ~45 min · remaining to cutover ~101 h (≈ 4 weeks) · 0.6.0 after that ~35 h
```

- **% done** = finished estimated hours ÷ 147 for 0.5.0 (or the revised total), so it never jumps backwards when a stage runs long; after the cutover it restarts for 0.6.0 (÷ 35).
- **Stage ETA** is within about 1 hour (DEV-5); **remaining** is the sum of the open phases, re-estimated from actual speed after every phase and once more after phase 4.
- A revised total is called out once, with the reason (for example "phase 4 took 20 h instead of 16 h; 0.5.0 now 157 h").
- The same line goes into `claude/checkpoint.md` in this Project at every checkpoint, so a new session picks up the exact position.

## Risks and gates

The largest risk is still phase 4: if the extension host cannot run a source reliably, standalone slips. Phase 0's Python 0.4.5 already closes the open Suwayomi port, so a slip no longer leaves a security gap.

| Risk | Effect | Fallback |
| --- | --- | --- |
| Extension host fails for some sources (phase 4) | +10–20 h | Keep the bundled private Suwayomi engine for those sources only, behind the same API |
| Stage 1–26 code fits the feature-folder layout worse than expected | +10–20 h | Port only schema, matching and tests; rewrite the rest per area from the port map |
| Mihon's next release changes the extension API further than 1.4 removal | +5–10 h | Host keeps loading old and new formats; the release watch reports the change on the day it ships |
| Landlock unavailable on a user's kernel (below Linux 6.7) | Repair off for that user | Repair refuses to run and says why on the System page; the rest of the app works |
| JCEF + Xvfb make the image large or slow to boot | Boot-time gate fails | Start Xvfb and JCEF only on first WebView use; drop the AOT cache if it gives no gain |
| Metadata or solver services limit or change their API (AniList at 30/min now) | Slower metadata | Pacing, caching and `Retry-After`; Komf as alternative provider |
| Usage limits interrupt a long stage | Lost minutes, not work | Stages sized to about 1 hour, CI result and checkpoint after each |

I stop and ask before anything that cannot be undone or leaves the workspace:

1. Starting phase 0 (your "start the conversion").
2. Deploying Python 0.4.5 (you deploy it).
3. The first push to the `conversion` branch, merging to `main`, and the first image published to ghcr.io.
4. Any change on the NAS, and the 0.5.0 cutover (phase 15).
5. A requirement that turns out impossible or needs a different design: reported with options, then wait.

## Requirement coverage

All 151 product requirements (ARC through SET) are assigned to a phase; none is left out. The 8 DEV items are working rules applied throughout.

- **Done** items are ported with their existing tests, not rebuilt.
- **Changed, Partial and Open** items are built in their phase, as described above.
- **Research items** are answered by R1–R28 and only re-checked against current releases in phase 0.
- Review findings: #1 and #5–7 land in phases 0, 2 and 5; #2 in phase 0 (Python 0.4.5) and phase 4; #3–4 in prerequisites; #8–9 in phases 0 and 14; #10 at cutover.

## Recheck 2026-10-29: time reductions

Accepted 2026-10-29 and applied above: conversion first, new features after. Time changes:

|  | Before (27 Oct) | After (29 Oct) | Change |
| --- | --- | --- | --- |
| Work before the cutover (Python retired) | 193 h, 16 phases | **147 h**, 15 phases | **−46 h** (about 2 weeks) |
| Work after the cutover (0.6.0) | 0 h | 35 h, 3 phases | +35 h |
| Total | 193 h | **182 h** | **−11 h** |
| Range | 155–250 h | 120–195 h (0.5.0) + 30–45 h (0.6.0) |  |
| Calendar to cutover | 7–8 weeks | about 6 weeks | −1½ to 2 weeks |

Where the 46 h before the cutover went: 35 h moved to 0.6.0 (guests and load gate 14, two-factor/owner transfer/AI/Postgres/repair grouping 10, notifications/PWA/QR setup/scheduled backups/recovery codes/profile sharing 11) and 11 h cut by reuse and trimming (T2–T7, PROP-3, PROP-4, version check instead of a full research re-check). The backup format stays in 0.5.0 on purpose, so nothing is written twice.

**What the recheck measured.** `service.py` is one class with 624 methods over 19,616 lines; `main.py` registers 133 routes (115 admin, 17 reader). The stage-26 C# already contains working, tested versions of the matching core (`TextMatcher`, `IndelSimilarity`, `AliasExtraction`, `SummaryMatching`), catalogue merge/split/recovery, portable identity and sharing, reader page serving, the APK manifest and archive checks, the repair guest protocol, the settings defaults and setup fields. The earlier estimate treated some of these as new work.

| # | Finding | Change | Hours |
| --- | --- | --- | --- |
| T1 | About a quarter of the open requirements are new features that do not affect the conversion (guests, voting, two-factor, notifications, scheduled backups, recovery codes, Postgres, AI). Building them before the cutover keeps Python alive longer and adds risk to every phase. | Release 0.5.0 = conversion + standalone + security essentials; 0.6.0 = the rest (PROP-1 in the requirements doc). Phases 11 and the 0.6.0 parts of 5, 7, 13 move after the cutover. | Cutover −48 h; total 0 |
| T2 | Matching core already in C# with tests (`Core/Matching`, 30 KB, patterns and Unicode tables). | Phase 8 ports the profiles, decision log and test set onto it instead of re-engineering the scorer. | −3 |
| T3 | Sharing, portable identity, merge/split/recovery and review tasks exist (`CatalogueSharing`, `CatalogueStructure`, 160 KB). | Phase 9 reuses them; only the versioned format header and engine-neutral export are new. | −2 |
| T4 | APK manifest, checked archive and source package readers exist (`SourceRepair`, 60 KB). | Phase 6 adds the domain and native-code rules to them. | −1 |
| T5 | Reader serving and chapter resolution exist (`Reader`, 50 KB). | Phase 10 keeps them; CBZ writing and the repo index are the new parts. | −1 |
| T6 | Accounts: ASP.NET Core's cookie authentication, authorization policies and `PasswordHasher` cover login types, roles and hashing. | Phase 5 writes only the owner, keys and role mapping on top of them. | −2 |
| T7 | The theme contrast check can be a unit test on the palette table, not a browser run. | Phase 12 test is seconds, not minutes, on every push. | −1 |
| T8 | CI loops: cache NuGet and Gradle, build the Kotlin host only when its files change, run Playwright only on UI phases. | Each stage's CI result arrives in 2–3 min instead of 8–10. | About −5 h of waiting over the project; not counted |

**Not changed on purpose.** Loading Tachiyomi 1.4 extensions (R1), the Landlock runner (R4), the generated UI client (P4) and the per-phase UI migration all cost a little now and save more later; they stay.

**Requirements doc.** Updated today: UI-13 now says Server-Sent Events; CON-2, PIPE-7, PERF-8, MAT-6, READ-1, CHAP-7 and SRC-2 carry the research results; section 17 records that all research items are done; section 18 lists PROP-1–4 (release split, AI items marked Deferred, theme research clause dropped, hand-maintained backup schema).

## Decision log

The review and research sections below record why the plan looks the way it does, with sources. Everything in them is already applied to the sections above; hour figures in them are the totals at the time.

## Review 2026-10-27: needs and proposals

The plan has one hard gap: C# cannot be built where I work. Proposals P1–P6 were all accepted 2026-10-27 (P4 in its clean-API form), total about 196 h.

Checked: my cloud workspace reaches PyPI and npm but not NuGet, Maven or the .NET download site, and has no .NET SDK. I have file access to your PC folders but no shell there. The GitHub repo is **public**, MIT-licensed, with no workflow runs yet; CI results are readable from here.

**Needs (decide before or during phase 0)**

| # | Need | Why | Your action |
| --- | --- | --- | --- |
| N1 | A place to build and test C# | No NuGet/.NET here, no shell on the PC | None: P1 accepted |
| N2 | Public or private repo | Work-in-progress code would be public from the first push | Public for now; can switch to private any time |
| N3 | Java host for extensions | Phase 4 design depends on it | Decided: JVM host (most reliable and fast) |
| N4 | Reader signing key in GitHub secrets | A new key means everyone reinstalls the extension | You add the existing key at phase 10 |
| N5 | Licence notices | Suwayomi-derived files stay MPL-2.0 and Mihon parts Apache-2.0 inside the MIT repo | None; I add a NOTICE file |
| N6 | Commands on your PC | Data checks in phases 0 and 15 run where the 5 GB copies are | Agreed |

**Proposals**

| # | Proposal | Effect on ETA |
| --- | --- | --- |
| P1 | Accepted. CI from day one: build the GitHub workflow skeleton in phase 0 instead of 14. Each stage pushes to a `conversion` branch and I read the results. Postgres and Playwright run as CI services. Merging to `main` and publishing images stay gated. | +4 h phase 0, −4 h phase 14 |
| P2 | Accepted. Do all 7 research items as one research stage in phase 0, approved once, so later phases never stop for research. | 0 h (moved) |
| P3 | Accepted. Ship the interim Suwayomi step (ARC-11, review #2) now as Python 0.4.5, since cutover is weeks away. | +1 h |
| P4 | Accepted, clean form. A new versioned API (\`/api/v1\`, OpenAPI-described, one consistent format) from phase 1; no old formats kept. Each phase moves the UI pages for its own area onto the new API through a generated typed client, so every phase is still tested in the real UI; phase 12 keeps only layout, themes, PWA and wizard. | +2 h (client generator); UI work spread over phases 1–11, phase 12 −8 h |
| P5 | Accepted. In phase 0, export your merge/reject decisions and a 1,000-entry sample (no keys or accounts) once, so every phase tests against real data. | +1 h |
| P6 | Accepted. Fixed re-estimate after phase 4, the riskiest phase, reported with the new total. | 0 h |

Your review and feedback time is not in the 192 h; it is waiting time, so the status line shows it separately.

## Review 2026-10-27 (2): code structure and decisions

The existing stage-26 C# code is a good base, but it is organised by technical layer, still drives Suwayomi, and runs repairs in a VM that needs host permissions. Seven changes fix that; the total moves from 196 h to about **197 h**. Checked against the file listing of `autosourcarr/src` and the Python `app` folder; no code was changed.

**What the code looks like now**

| Part | Size | Finding |
| --- | --- | --- |
| Python `service.py` | 1.1 MB, one file | Cannot be ported file by file; needs a function-to-class map |
| Python `main.py`, `db.py` | 152 KB, 76 KB | Same |
| C# projects | Api, Bootstrap, Core, Infrastructure, Repair | Sources code is split over 8 folders across Core and Infrastructure |
| C# Suwayomi-bound code | `SuwayomiClient` (4 files, 62 KB), `SourceEngineController` (47 KB), `NativeSourceOwnerConnection` (65 KB) | Replaced by the JVM extension host in phase 4 |
| C# repair execution | `HardwareVmRuntime`, `HardwareVmSession` | A VM needs `/dev/kvm` from the host; conflicts with REP-5 and ARC-8 |

**Changes**

| # | Change | Phase | Hours |
| --- | --- | --- | --- |
| S1 | Feature folders, as Sonarr's `NzbDrone.Core`: one folder per area (Sources, Repair, Matching, Catalogue, Imports, Reader, Accounts, Guests, Jobs, Settings, Backup, Notifications) holding its models, logic and data access together. Projects: `Core`, `Api` (one endpoint file per area), `Host` (startup, from Bootstrap), `Repair.Runner` (separate low-rights process), plus the Kotlin `ExtensionHost` and `Tests` mirroring Core's folders. Replaces the 9-assembly layout in the Approach table. | 0 | +3 |
| S2 | Retire the Suwayomi-bound code instead of porting it; keep a slim Suwayomi client only for the CON-3 import connection. Python `suwayomi.py` and `source_engine.py` are not ported. | 4 | −4 |
| S3 | Replace the VM execution layer with a process sandbox (separate user, rlimits, no `/config`, network limited to the source's domains); keep the existing guest protocol and artifact store. | 6 | 0 (already estimated) |
| S4 | Generate a port map in phase 0: every Python function → target C# class and phase. Each phase ticks its rows, so nothing in `service.py` is missed. | 0 | +1 |
| S5 | Review rules: one public class per file, files under about 500 lines (larger ones split into partial files, as `CatalogueQueries.Search.cs` already does), a 5–10 line README per feature folder, and xUnit test classes named after the class they test. | all | 0 |
| S6 | Compile-time code instead of runtime reflection: source-generated JSON serialisers and `[GeneratedRegex]` for the matching patterns, so startup and matching are faster and the app can be published ReadyToRun. | 1, 8 | +1 |
| S7 | One multi-stage image: ReadyToRun .NET app plus a slim Java 21 runtime for the extension host; Python and the full Suwayomi server are no longer in the image. | 14 | 0 |

Kept on purpose: the two runtime databases stay separate files, because chapter-origin writes are frequent and a separate write log keeps them from blocking catalogue reads.

**Decisions taken (most efficient option)**

- **P3 timing:** first stage after "start the conversion" (about 1 h, you deploy 0.4.5). Closing the port needs no release: if you want it closed sooner, change `30058:30058` to `127.0.0.1:30058:30058` in the Compose file yourself; Suwayomi's own web page is then unreachable until 0.4.5 adds "Open in WebView".
- **PIPE-7 (Suwayomi v2.4.2366, "if still relevant"):** relevant. The extension host's loader code is taken from that version, so extensions behave as they do in Suwayomi today.
- **Database access:** Dapper replaces the hand-written `DatabaseRow`/`DatabaseSession` mapping; FluentMigrator replaces the SQLite-only `.sql` files so the same migrations run on Postgres.

## Research 2026-10-27: findings that change the plan

The biggest finding is Mihon v0.21.0 (released 11 Oct 2026): it is the last version that restores `.tachibk` backups, and the next one drops Tachiyomi 1.4 extensions. Six findings adjust the plan; the total goes from 197 h to about **196 h**.

| # | Finding | Change to the plan | Phase | Hours |
| --- | --- | --- | --- | --- |
| R1 | Mihon v0.21.0: last version restoring `.tachibk`; next version drops 1.4 extensions; extensions now install and update only from the repo they came from ([changelog](https://mihon.app/changelogs/v0.21.0)) | Import keeps `.tachibk` and adds the new format as soon as it ships (PIPE-9 watch). The extension host loads both 1.4 and current extensions, so orphaned old sources keep working inside AutoSourcarr even after Mihon drops them. The reader extension is offered from AutoSourcarr's own repo index, so Mihon updates it in-app. | 4, 9, 10 | +1 |
| R2 | Both protobuf formats (repo `index.pb`, backups) are parsed by hand-written code in Python and C#; the current code already reads Keiyoushi's `index.pb` (checked in `RepositoryManifest.cs`) | Generate parsers from Mihon's `.proto` schema files with Google.Protobuf instead of porting the hand-written parsers; a new backup format then means updating the schema, not code | 6, 9 | 0 |
| R3 | Suwayomi v2.4.2366 (about late Sept 2026) adds extension stores (`extensionRepos` → `extensionStores`), Extension API v1.6 support, JCEF WebView and updated Cloudflare handling ([release](https://newreleases.io/project/github/Suwayomi/Suwayomi-Server/release/v2.4.2366)) | Base the extension host on v2.4.2366's loader and store code, so API v1.6 and extension stores come with it; pin it and let the pipeline report new releases | 4 | 0 |
| R4 | Landlock lets an unprivileged process lock itself out of files and TCP ports; Docker's default seccomp profile allows its system calls ([kernel docs](https://docs.kernel.org/6.12/userspace-api/landlock.html), [Docker commit](https://git.causa-arcana.com/kotovalexarian-likes-github/moby--moby/commit/af819bf623a8d414289b24f9817c6317cf3f96d5)) | Repair and WebView runner: Landlock (file access to its own work folder only; TCP connect only to a local proxy port) + `no_new_privs` + rlimits. The local proxy allows only the source's domains. No root, capabilities or host steps. A separate user is dropped, since a non-root container cannot switch users. If the kernel lacks Landlock network rules (ABI 4, Linux 6.7+), repair refuses to run (fail closed). | 6 | 0 |
| R5 | .NET 10 is LTS, supported until 14 Nov 2028 (10.0.14 current); .NET 11 is due in November as a short-term release ([policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)). Native AOT cuts .NET start-up to about 0.1 s ([benchmarks](https://adaptivewebhosting.com/blog/native-aot-aspnet-core-10-production)), but start-up is dominated by the JVM host. | Stay on .NET 10 LTS. Use ReadyToRun, not Native AOT. | 14 | 0 |
| R6 | FlareSolverr struggles with 2026 Turnstile and managed challenges; Byparr is a drop-in with the same API and higher success rate, but 2–4× slower ([comparison](https://godberrystudios.com/posts/byparr-scrapling-flaresolverr-cloudflare-bypass-2026/)) | One "FlareSolverr-compatible solver" connection type covers both. The built-in basic solver is the extension host's own JCEF WebView, so the image carries one browser engine, not two. | 7 | −2 |

**Second round (R7–R10)**

| # | Finding | Change to the plan | Phase | Hours |
| --- | --- | --- | --- | --- |
| R7 | TrueNAS 25.10.5 ships Linux 6.12.95 ([release notes](https://update-master.ixsystems.com/scale/TrueNAS-SCALE-Goldeye/TrueNAS-SCALE-25.10.5.release-notes.txt)); Landlock network rules need 6.7+ | R4's sandbox works on your NAS (25.10.7 is the same 6.12 line); the start-up check stays as a safety net | 6 | 0 |
| R8 | .NET 10 has built-in Server-Sent Events (`Results.ServerSentEvents`); the browser's `EventSource` reconnects by itself and resends the last event ID ([article](https://www.milanjovanovic.tech/blog/server-sent-events-in-aspnetcore-and-dotnet-10)) | Live updates use SSE instead of SignalR: updates only flow server → browser, so no hub protocol or client library is needed in the vanilla JS UI, and the login cookie works as-is. The single event stream (PERF-8) gives each event an ID, so a reconnecting page catches up on what it missed. | 3 | −1 |
| R9 | JDK 25 (LTS) can store an ahead-of-time cache of loaded and linked classes from a training run and reuse it at start ([inside.java](https://inside.java/2026/01/09/run-aot-cache/), [JEP 514](https://openjdk.org/jeps/514)); the cache must match the exact JDK and JARs | Extension host on Java 25 instead of 21. The pipeline makes the cache with a training run during the image build, so it always matches. Extensions load later and are not cached; the gain is measured by the PIPE-8 boot-time gate before it is kept. | 14 | +1 |
| R10 | MalwareBazaar's hash lookup is free with a free key under fair-use terms ([API](https://bazaar.abuse.ch/api/)); common APK analysers (for example quark-engine) are Python | APK checks (SRC-2) stay in C#, building on the existing `AndroidBinaryManifest` reader (permissions, domains, native code). The optional hash lookup is a MalwareBazaar connection under Connections, so no Python returns to the image. | 6, 7 | 0 |

The total stays at about 196 h. Remaining research items stay in the phase 0 research stage (P2).

**Third round (R11–R12)**

| # | Finding | Change to the plan | Phase | Hours |
| --- | --- | --- | --- | --- |
| R11 | Kavita reads manga metadata from a `ComicInfo.xml` at the root of each CBZ: Series, LocalizedSeries (groups alternative names), Number, Volume, Title, Summary, Genre/Tags, LanguageISO, Web ([Kavita wiki](https://wiki.kavitareader.com/guides/metadata/comics/)) | Library downloads are written as CBZ with a `ComicInfo.xml` filled from the catalogue (Web = the source chapter address), so downloaded chapters work in Kavita, Komga and Komf without extra tagging | 10 | 0 |
| R12 | Mihon publishes no separate `.proto` file; backup converters read the field layout from Mihon's own backup model code ([example](https://github.com/Animeboynz/Mihon-Backup-Viewer)) | Refines R2: the pipeline derives the `.proto` schema from Mihon's backup model classes in each release (part of the PIPE-9 watch), and the C# parser is generated from it | 9, 14 | 0 |

Job-queue priorities and theme colours were researched in the fourth round below.

**Fourth round (R13–R22)**

| # | Finding | Change to the plan | Phase | Hours |
| --- | --- | --- | --- | --- |
| R13 | Sonarr's job queue (read from its source): 3 worker threads; priority High / Normal / Low, then oldest first; an identical job already queued or running is not queued twice; flags per job type: needs disk, exclusive (runs alone), long-running (exclusive jobs wait for it) ([Sonarr](https://github.com/Sonarr/Sonarr)) | PERF-1–3 job queue copies these rules: manual actions High, scheduled Normal, maintenance Low; duplicate jobs merged; imports and backups marked "needs disk"; full reset and restore "exclusive"; library refreshes "long-running". Worker count starts at 3 and is tuned by the PIPE-8 load test. | 3 | −1 |
| R14 | Theme check of the current UI (WCAG contrast computed from `ui.css`/`workbench.js`): all text colours pass AA (4.5:1) in dark and light styles, and all six accents pass on dark (6.4–10.8:1). Borders fail the 3:1 rule for controls (1.4–1.7:1). Accents used as text on the light Paper style fail (1.5–2.1:1); only Blue has a dark variant. | One palette module with two roles per colour (dark and light variants for all six accents) and a separate stronger border colour for inputs and buttons; a contrast test in the pipeline fails the build below AA | 12 | 0 |
| R15 | Two palette systems overwrite each other: `workbench.js` (hex) and `reimagined.js` (oklch, Neutral/Paper only). `ui.css` (180 KB) is the four other CSS files combined; 29 script files load on every page (`app.js` 331 KB) | One theme source of truth; the pipeline bundles and minifies JS/CSS into a few files (esbuild in CI only, nothing extra in the image) | 12, 14 | 0 |
| R16 | Public repos get free GitHub Actions runners and free public packages; private repos on Free get 2,000 minutes and 500 MB package storage a month ([Actions](https://docs.github.com/billing/managing-billing-for-your-products/managing-billing-for-github-actions), [Packages](https://docs.github.com/en/billing/concepts/product-billing/github-packages)) | Keep the repo and image public: the image (.NET + Java + browser) will exceed 500 MB, and per-stage CI with browser tests would use most of 2,000 minutes | 14 | 0 |
| R17 | SSE through nginx (Nginx Proxy Manager) needs the response header `X-Accel-Buffering: no` and a heartbeat about every 30 s ([guide](https://oneuptime.com/blog/post/2025-12-16-server-sent-events-nginx/view)) | The app sends both, so live updates work behind your NPM and Authelia without proxy changes | 3 | 0 |
| R18 | iOS web push needs iOS 16.4+, HTTPS, the app added to the Home Screen with a standalone manifest, and permission asked from a button tap ([OneSignal docs](https://documentation.onesignal.com/docs/en/web-push-for-ios)) | UI-15/SET-11 push: "Enable notifications" button in the installed app; the setup check warns when not on HTTPS | 12, 13 | 0 |
| R19 | Komf 2.1 already fetches metadata from 14 providers (MangaUpdates, AniList, MAL, MangaDex, Bangumi, BookWalker, Webtoons and more) and has a REST API ([Komf](https://klibs.io/project/Snd-R/komf)) | Build in only MangaUpdates, AniList and MangaDex; offer Komf as an optional metadata connection for the rest, matching ARC-1's "companion app" idea | 7, 9 | −2 |
| R20 | Keiyoushi has 1,431 source modules built on 67 shared themes (Madara, MangaThemesia, HeanCms and others), counted from its repository ([extensions-source](https://github.com/keiyoushi/extensions-source)) | Repair groups orphaned sources by theme: one theme fix is tried on every orphan using that theme, then each passes its own content check (REP-4) | 6 | 0 |
| R21 | Landlock for .NET exists as an MIT package (`Landlock`, 1 star, no releases) ([landlock-sharp](https://github.com/curiosity-ai/landlock-sharp)); Suwayomi's FlareSolverr support uses sessions, which Byparr does not yet support | Write a small own Landlock wrapper (3 system calls) using that package only as reference; the solver connection works without sessions by caching solved cookies per domain | 6, 7 | 0 |
| R22 | Mihon's local source reads one folder per series with one CBZ per chapter (file name = chapter title) and a cover image ([Mihon](https://mihon.app/docs/guides/local-source)); FluentMigrator 8.0.1 (Jan 2026) runs on .NET 10; Temurin recommends a `jlink` custom Java runtime for small images ([Adoptium](https://adoptium.net/installation/containers/)) | Download layout `Series/Chapter.cbz` + `cover.jpg` + `ComicInfo.xml`, readable by Mihon, Kavita and Komga; Java 25 runtime built with `jlink` (only needed modules) to keep the image small | 10, 14 | 0 |

Net effect of round four: −3 h. New total about **193 h**. R23: Keiyoushi (20 language folders, 410 English sources) includes official publisher sources: MANGA Plus, MANGA Plus Creators, MangaDex, Comikey, Webtoons, Tappytoon, MangaUp, K Manga, Kodansha, Mangamo, VIZ, Tapas and INKR (listed from its repository). The ready-made matching profile ranks these first for chapter choice, then other sources by health and quality score; this is the default for phase 8. R24: FlareSolverr's API is one endpoint (\`POST /v1\`) with \`request.get\`, \`request.post\` and optional \`sessions.\*\`; options include \`maxTimeout\` (60 s default) and \`returnOnlyCookies\`; its README says its image-CAPTCHA solvers do not work, so it only handles Cloudflare-style browser checks (\[FlareSolverr\](https://awesome.ecosyste.ms/projects/github.com%2FFlareSolverr%2FFlareSolverr)). The solver connection therefore asks for cookies only (\`returnOnlyCookies\`), caches them per domain with the solver's user agent, uses sessions only when the solver offers them, and sends image CAPTCHAs to the built-in WebView for you to solve. CAPTCHA research is complete; nothing is left for the phase 0 research stage except checking these findings against live sources.

R25: Suwayomi's JCEF WebView needs an X display (Xvfb in Docker) and five X11 libraries (libxrender, libxcomposite, libxdamage, libxkbcommon, libxtst) ([Unraid template](https://ca.unraid.net/apps/tachidesk-docker-0atgtlf1l317dr)). The image includes these, and the low-rights runner starts its own Xvfb inside the container, so no host step is needed (ARC-8). "Open in WebView" streams that browser into the Web UI. 0 h.

R26: Temurin Java 25 is LTS (released Sept 2025, supported until at least Sept 2031; Java 21 until at least Dec 2029) ([Adoptium](https://adoptium.net/es/support)). R9's move to Java 25 also adds about two years of support. 0 h.

R27: AniList's API is temporarily limited to 30 requests per minute, and its `X-RateLimit-Remaining` header does not show that lower cap; going over gives a 1-minute block ([AniList](https://docs.anilist.co/guide/rate-limiting)). The metadata client paces itself to 30 per minute, honours `Retry-After` and caches answers. 0 h.

R28: MangaDex allows about 5 requests per second per IP, requires an honest `User-Agent`, rejects requests that carry a `Via` header, and blocks hotlinked images ([MangaDex](https://api.mangadex.org/docs/2-limitations)). The R4 domain-filtering proxy must not add `Via` or change the user agent, and images keep going through AutoSourcarr's own page serving. 0 h.

**Phase 0 stage 4 re-check (31 Oct 2026).** Mihon is now v0.21.1 (12 Oct): still the last major that restores `.tachibk`, the next one drops 1.4 extensions; v0.20 added the `tachiyomix` 1.6 extension and index format, so the extension host loads 1.4 and 1.6. Suwayomi v2.4.2366 is still the latest. .NET 10.0.14 (25 Oct, out-of-band security: Brotli decoder CVE-2026-111497) with SDK 10.0.403 — `global.json` rolls forward to it; the image never ships an older runtime. Temurin 25.0.5+8 (22 Oct) is the jlink pin. TrueNAS 25.10.7 runs Linux 6.12.105. FluentMigrator 8.0.1 and FlareSolverr v3.5.2 unchanged. AniList: the 30/min cap is still in force plus an undocumented burst limiter, so the client spaces requests evenly. MangaDex: `GET /at-home/server/{id}` is capped at 40/min, so phase 10 paces and caches page-server lookups. No hours change. Full table: `docs/version-check-2026-10-31.md` in the repo.
