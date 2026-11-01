# AutoSourcarr — handoff for Claude Code

Written 2026-11-01 (Europe/Amsterdam) at the end of a Claude cloud session. Facts below were
checked against the repo, CI results or the project docs on that date; anything not checked is
marked **unverified**. When this file and an explicit user instruction disagree, the user wins.

## 1. Project summary

AutoSourcarr ("AutoSourcarr Reimagined") is a self-hosted Sonarr/Radarr-style manager for manga
and similar media. It uses Tachiyomi/Mihon/Keiyoushi extension sources and repositories as
indexers, matches their titles into one catalogue (entries, source records, chapters), serves that
catalogue to Mihon/Suwayomi through its own reader extension, and repairs orphaned/broken sources
inside the instance. The catalogue must be shareable/migratable into other people's instances.

- Repo: `LittleHund/autosourcarr` on GitHub (**public**). Branches: `main` (initial commit only),
  `conversion` (the migration), `ci-probe` (scratch compile/test branch, see §4).
- Released app: **0.4.5** (`VERSION`), Python 3.12 FastAPI + SQLite, UI "Reimagined"
  (vanilla JS, Sonarr v5 style), image built on `ghcr.io/suwayomi/suwayomi-server:v2.3.2243`,
  deployed by TrueNAS Compose behind Authelia, UID/GID 568:568, Europe/Amsterdam. Whether 0.4.5 is
  deployed on the NAS: **unverified** (it was an open user item on 2026-10-31).
- Target: **0.5.0** = the Python → C#/.NET 10 conversion, standalone (Suwayomi optional),
  settings in `config.json`. **0.6.0** = new features (phases 15–17) after the Python app retires.
  C# assembly version is still `0.4.3-migration` (`Directory.Build.props`, `ApplicationRuntime.Version`).

### Migration state (plan: Claude Docs "AutoSourcarr conversion plan", 14 phases to cutover)

The old "stage 1–26" migration (2026-10-25, Codex) was folded into this phase plan; stage numbers
are no longer used. Plan doc: Claude Docs artifact `4cf80085-d5a6-40e1-9c4e-c40a0e4fbbba`
(search node `9b1de541-77e7` with `{"kind":"search","text":"<n> <Name>"}`).

| Phase | Status (2026-11-01) | Last `conversion` commit / CI |
| --- | --- | --- |
| 0 Foundation, 1 Host+settings, 2 Data layer, 3 Jobs/events | done | see checkpoint |
| 4 Extension host (Kotlin source host, WebView) | done | 9822947 |
| 5 Accounts (forms/basic/external, roles, keys) | done | 2940c0c |
| 6 Sources/repair (index.pb, APK checks, orphans, domain repair, Landlock runner) | done | 73834a2, CI 41267266674 green |
| 7 Connections (menu, solvers, WebView, metadata providers, Suwayomi feed) | done | 6acfff5, CI 41272743279 green |
| 8 Matching (scorer, decisions log, duplicates, mixed entries, workbench) | done | 26ef573, CI 41309786259 green |
| 9 Imports, metadata, sharing | done | 1cc573b, CI 41321311062 green |
| **10 Chapters/reader** | **in progress, paused** | local commit 3ed0b4e only (see below) |
| 11 Web UI finish, 12 Backup, 13 Pipeline/image, 14 Cutover | open | — |
| 15–17 (0.6.0) | open | — |

Progress: 131 of 173 plan-hours (76 %); estimate ≈ 8.2 h to cutover, ≈ 5.5 h for 0.6.0
(measured pace 0.156 h per plan-hour, phases 0–9).

**Phase 10 state:** four parts were written by sub-agents and saved in local WIP commit `3ed0b4e`
(not pushed, never compiled): 10A chapters/serving/quality (`Core/Catalogue/{Chapters,Serving,Browse}`),
10B CBZ library + downloads (`Core/Library`, migration 2026110192, `LIBRARY_DIR`), 10C imaging,
hydration, tags, translation (`Core/Imaging`, `Core/Translation`, `Core/Catalogue/Metadata/Hydration`),
10D reader extension with 5 server slots, own repo index, QR setup, reverse-proxy docs.
Wiring them into `ApplicationRuntime`/`ApiV1Endpoints` was started by an integration agent that the
user interrupted; **which wiring edits landed is unverified** — diff `3ed0b4e` against `1cc573b`
before continuing. The full wiring brief is in this session's scratch (lost); redo it by reading each
area's endpoint/services classes. Phase-10 also contains `tests/AutoSourcarr.Tests/Fixtures/matching/duplicate-pairs.user.json`
(see §6).

**Checkpoint:** Claude Project doc `claude/checkpoint.md` (time table, per-phase checklist results,
carried items, open user decisions). Port map with per-phase ticks: `docs/port-map.md` (copy in the
Project as `claude/port-map.md`). Requirements: Claude Project `claude/requirements.md` (copy of the
Docs artifact "AutoSourcarr requirements"; IDs ARC, PIPE, SRC, REP, CON, MAT, IMP, META, CHAP, READ,
AUTH, GST, UI, PERF, SET, DEV). Knowledge DB records per phase: `RELEASE-0.5.0-PHASE-n`,
`VERIFY-…`, `DECISION-…`, `FINDING-…`, `LIMIT-…`.

## 2. Hard rules (never violate)

1. Never change subscriptions, manual titles/matches/groups, rejected matches, separate entries,
   import decisions or chapter provenance except through an explicit, confirmed reset (preview token + confirm).
2. Automatic matching never goes below the 0.94 title floor and always applies edition (colour/mono,
   manga/webtoon/novel, animated/still) and identity (creators, year, cover, provider ids) checks; summaries never lower the floor.
3. A local timeout or capacity deferral is never evidence of a source outage or a missing manga.
4. Repairs preserve original code/source provenance; missing code is handled by explicit
   project/repository input or an "Unavailable" choice, never by guessed module metadata.
5. Every repair keeps the exact last working package/jar so rollback restores the same bytes; a repair that fails its content check is rolled back.
6. "Fixed" means user-confirmed working; installed, compiled or sampled-healthy are separate, weaker states and must be reported as such.
7. Repair forms, jobs, logs, evidence, install and rollback live under "Tasks → Repairs"; "Sources → Repair AI" is only provider/account setup and login.
8. APK/JAR uploads are checked (archive structure, manifest, entry point, permissions, domains); native code is refused and nothing installs before the admin confirms the preview.
9. Reader API key protection and the Authelia rule `^/api/reader/v1([/?].*)?$` never change (the public repo path `/api/reader/repo/` is an additional, GET/HEAD-only bypass added in phase 10).
10. Never disable JVM bytecode verification (`-XX:-BytecodeVerificationRemote` was refused as security weakening; do not work around it).
11. Never push env files, keys, cookies, accounts or runtime databases, never read or store secret values, and never index live config into the knowledge DB.
12. Never touch the NAS (no deploy, restart, log requests, host policy changes) unless the user asks; the earlier shutdown-incident investigation is closed by the user.
13. No helper Compose services, Docker socket or sibling containers: every feature runs inside the AutoSourcarr container and is usable from the Web UI; repair code is isolated and fails closed without host isolation.
14. Use GitHub only when really necessary (the user does not want notification mail): compile/test on `ci-probe`, push `conversion` once per finished phase.
15. Gates — stop and ask the user before: merge to `main`, publishing to ghcr, any NAS change, cutover, and the 0.6.0 release.
16. Do not deploy, reset data or overwrite live configuration while preparing a release.
17. Report honestly: say what was run and what was not; APK/JAR builds, Docker builds, browser checks and live checks are reported only if actually run.

## 3. Architecture

| Path | What it is |
| --- | --- |
| `autosourcarr/app` (PC workspace: `autosourcarr/autosourcarr/app`) | Python 0.4.5 backend (FastAPI, `service.py` ≈19.6k lines, `main.py` routes) — the behaviour reference for porting. |
| `autosourcarr/app/static` | Vanilla JS UI shared by both backends; during conversion each phase moves its calls from `/api/admin/...` to `/api/v1/...`. `static/generated/api-client.js` is generated from `api/openapi.json`. |
| `src/AutoSourcarr.Core` | C# domain: Accounts, Catalogue (Structure, Titles, Metadata, Sharing, Maintenance, Chapters…), Connections, Imports, Jobs, Matching (Scoring, Ingest, Manual, Duplicates, Mixed, Review, Decisions, Wiring), Persistence, Repair, Sources (Runtime, Inventory, Library, Discovery), Settings, Compatibility. |
| `src/AutoSourcarr.Api` | ASP.NET Core minimal APIs. Composition root `ApplicationRuntime.cs` (+ `ApplicationRuntime.Matching.cs`); all routes under `/api/v1` in `Endpoints/*V1Endpoints.cs` with OpenAPI metadata; `Security/AccessControl.cs` (AccessPolicy: Public/Guest/Admin/Owner in one place); `Http/RequestPolicy.cs`. |
| `src/AutoSourcarr.Host` | Unprivileged native startup resolver (to be removed with the native launcher in phase 13). |
| `tests/AutoSourcarr.Tests` | Custom test runner (`TestSuite.cs`, `suite.Add`, `Check.*`; registration in `Program.cs`). Also tool modes `--write-openapi`, `--write-settings-docs`, `--filter`, `--score-pairs`, `--duplicate-test-set`. |
| `source-host/` | Kotlin JVM worker ("source host") built inside a pinned Suwayomi-Server checkout (commit `40bc5da5`, v2.4.2366) by `scripts/build-source-host.sh`. |
| `extension/` | Kotlin reader extension (Tachiyomi extensions-lib 1.4, built with the Keiyoushi build). |
| `native/` | C launchers: `repair-runner.c` (Landlock ABI ≥ 6 sandbox, connect gate to egress proxy, signal gate), `repair-launcher.c` (KVM microVM; no guest image yet), container entry. |
| `docs/` | `port-map.md` (Python→C# map, per-phase ticks), `settings.md` (generated), `reverse-proxy/` (Authelia/NPM, phase 10), user guides. |

**JVM boundary.** All extension (third-party) code runs only in the source host JVM, never in the
.NET process. The app starts it as a supervised child (`SourceHostProcess`: per-run token,
health wait, restart ladder, own process group, own Xvfb for JCEF) on `127.0.0.1:4567` and talks to
it over HTTP (`/v1/...`, header `X-Source-Host-Token`) through `SuwayomiClient`. The host loads
extensions via dex2jar, keeps installed JARs, exposes browse/search/details/chapters/pages, source
preferences, WebView (Cloudflare solver, interactive sessions streamed to the app's WebSocket),
external FlareSolverr/Byparr fallback (cookies only) and per-host pacing (`SOURCE_MIN_INTERVAL_MS`).
Domain-move repairs rewrite class-file string constants in the host JAR; code rebuilds (future) run
in `native/repair-runner` behind the C# `EgressProxy`. Running the host as a separate low-rights
user is deferred to phase 13. A user's own main Suwayomi is optional (connection + change feed).

**Databases and stores.**
- `catalog.sqlite` — catalogue: entries, source records, titles/aliases, manual decisions, reviews,
  imports, `decision_log` (every matching decision with actor), display metadata, tags, library/downloads (phase 10).
- `chapter-origins.sqlite` — chapter provenance/origins.
- Migrations: FluentMigrator per file; 0.4.4 baseline `2026103101`, `2026103102`; then `2026110180`
  (decision_log), `2026110184` (mixed_entry_findings), `2026110192` (library, phase 10 WIP).
  0.3.27 and 0.4.4 files upgrade in place (fixtures in `tests/AutoSourcarr.Tests/Fixtures/legacy`).
- `config.json` + `config.key` — settings and Connections (secrets encrypted per field); 0.4.x cannot read a
  `config.json` that contains `connections` (rollback note in CHANGELOG). Accounts: `config/accounts/accounts.json`.
- Postgres: planned for 0.6.0 (phase 16); not implemented.
- **`AutoSourcarr-knowledge.sqlite3`** (in `F:\claude projects\autosourcarr data\`) — development
  knowledge DB for agent continuity (requirements, decisions, findings, releases, indexed ZIPs).
  It is **not** a runtime database. Update it in place; never create a second one. Tool:
  `scripts/knowledge-db.py` (`search`, `show ID`, `stats --summary`, `check`, `index <zip>`, `import <records.json>`).

## 4. Build, test, verify

There is no .NET SDK in the Claude cloud container and the PC VM cannot reach nuget/dotnet;
**all C# compiling and testing happens in GitHub Actions**.

- **`ci-probe` workflow** (`.github/workflows/probe.yml`, always succeeds, results committed under `probe/`):
  `git checkout ci-probe && git pull --no-rebase origin ci-probe && git merge -X theirs conversion && git push && git checkout conversion`.
  Read `probe/result.txt` (restore/build/test/openapi/client/source_host_build/source_host_check/source_host_csharp/repair_runner),
  `probe/build-errors.txt`, `probe/test.log` (grep `^FAIL`), `probe/source-host-*.txt`,
  `probe/repair-runner-check.txt`, `probe/mihon-backup-watch.txt`.
  After a green probe take regenerated files from it:
  `git checkout origin/ci-probe -- '**/packages.lock.json' api/openapi.json autosourcarr/app/static/generated/api-client.js docs/settings.md`.
- **`conversion` workflow** (`.github/workflows/conversion.yml`): `dotnet restore --locked-mode`,
  `dotnet build -c Release` (warnings are errors), tests, repair-runner check, OpenAPI/client
  freshness diff, settings doc diff, Python baseline suite, project checks.
- Before every push to `conversion`, locally (from repo root):
  ```bash
  (set -a; . config/autosourcarr.env.example; set +a; AUTOSOURCARR_SKIP_PYTEST=1 bash scripts/check-project.sh)   # must end "Project checks completed."
  bash scripts/ci-pytest.sh          # must say "No new Python failures (baseline 27 known)"
  node scripts/generate-api-client.mjs   # after taking openapi.json from ci-probe
  ```
- Other checks: `bash scripts/repair-runner-check.sh` (needs Landlock ABI ≥ 6, non-root),
  `python3 scripts/source-host-check.py` (real extensions, run in probe), `python3 scripts/mihon-backup-watch.py`,
  `python3 scripts/print-compatibility.py --check`, `bash scripts/check-v1-matching-paths.sh`,
  `python3 scripts/check-cbz-library.py` + `scripts/kavita-check.sh` (phase 10, not yet run).
- **Expected counts (last green, 2026-11-01, phase 9):** C# 1146 tests passed, 0 failed (probe
  `3d85867`); source-host C# tests 4/4 (supervised host, moved-domain repair, interactive WebView,
  external solver); repair-runner check 22 PASS; Python 2706 passed, 27 failed (the baseline in
  `tests/python-baseline-failures.txt`), 4 skipped. Known flaky (retried by `ci-pytest.sh`):
  `test_v094_runtime.py::test_websocket_relays_text_binary_and_closes_remote`.
- Analyzer note: the ASP.NET RouteHandlerAnalyzer crashes intermittently (AD0001) — kept as a warning
  in `AutoSourcarr.Api.csproj`; always pass Map* handlers as cast lambdas `(Func<...>)(...)`.
  `InvalidDataException` is sealed in .NET 10; `$$"""` raw strings must not contain `}}}`.

## 5. Working process

1. Before a phase: write its checklist from (a) the plan text and "Done when", (b) its requirement IDs,
   (c) its port-map rows and route groups, (d) the UI pages to move to `/api/v1`.
2. Before any app edit, look up the feature in the knowledge DB and requirements; read the Python original.
3. Propose changes and wait for approval for anything destructive or wide (deletes, resets, schema
   changes outside a phase, merges, deploys); inside an approved phase, work step by step.
   The user's standing preference (2026-10-09) is one executor and no parallel agents unless asked;
   the 2026-10-31/11-01 conversion run used parallel sub-agents per area after the user asked to go
   through the phases automatically — whether that is approved going forward is **unverified**: ask.
4. Quality checks before reporting done: green probe (build + all tests), local check-project +
   ci-pytest, an independent audit by a fresh agent over `git diff <prev-phase>..HEAD`, fix its findings
   (or record them as limitations), then green `conversion` CI.
5. Phase end (update the checkpoint):
   - push `conversion` once; confirm its CI run is green (record the run id);
   - ZIP `git archive --format=zip --prefix=autosourcarr/ HEAD` → `F:\claude projects\autosourcarr data\autosourcarr-0.5.0-phase-N.zip` (record sha256);
   - knowledge DB: write `knowledge-records/phaseN-records.json` (kinds release/verification/decision/finding/limitation;
     states implemented/verified/resolved/open — not "accepted"; fields id, kind, state, title, body (JSON string), origin,
     source_ref, applies_to, confidence), then on the PC VM: copy the DB, `index <zip>`, `import <records>`, `check`, copy back;
   - tick `docs/port-map.md` (section 0, "Phase N"), upload it to the Project as `claude/port-map.md`;
   - PC sync: zip `git diff --name-only <prev>..HEAD` (minus `.github`) → `conversion-export/phaseN-sync.zip`,
     extract into `F:\claude projects\autosourcarr\autosourcarr` (deleted files → move to `_to_delete/`, deleting is not allowed there);
   - re-estimate with actual hours; update `claude/checkpoint.md`;
   - report to the user: time table, progress bar with percentage, current phase, remaining time.
6. Write short, plain text; tables over prose; distinguish verified results from estimates.

## 6. Known issues, deferred items, open questions

| Date | Item | State |
| --- | --- | --- |
| 2026-11-01 | Phase 10 integration interrupted; WIP commit 3ed0b4e is uncompiled and possibly half-wired. | open |
| 2026-11-01 | `duplicate-pairs.user.json` (2,680 pairs from the user's decisions) committed locally for CI; user agreed it need not be private but **must be removed once everything functions**. | open (remove before cutover) |
| 2026-11-01 | Reader extension signing: user must add GitHub secrets `AUTOSOURCARR_EXT_KEYSTORE_B64`, `_KEYSTORE_PASSWORD`, `_KEY_PASSWORD`, `_KEY_ALIAS` (= `autosourcarr`); without them CI signs with a throwaway key. | waiting on user |
| 2026-11-01 | Reader extension slot count is fixed at build time (5); "add more from Mihon" is not possible with the Keiyoushi plugin. | limitation |
| 2026-11-01 | Mihon's next backup format is unpublished; only a probe sample exists; `mihon-backup-watch.py` tracks releases. | open |
| 2026-11-01 | Split paths are not written in the same transaction as their decision-log entry (merges are). | open |
| 2026-11-01 | `CatalogueDiscovery.SourceResult` (source health) not wired. | open |
| 2026-11-01 | Phase 10 carried in: import exact-thumbnail tie-break, machine translation in search, gallery identity, page colour sampling (`IPageImageSource`). | in WIP |
| 2026-11-01 | The workspace-root `F:\claude projects\autosourcarr\CLAUDE.md` and Project doc `claude/CLAUDE.md` still say the migration is "paused at stage 26" — outdated; this file supersedes them. | to update |
| 2026-10-31 | Real Cloudflare managed challenge not passed from CI (GitHub network); WebView passes the local challenge. | open, needs user network |
| 2026-10-31 | Source host runs as the same user as the app; separate low-rights user + runner binary in image → phase 13. | deferred |
| 2026-10-31 | Two-factor (AUTH-7), owner transfer/recovery (AUTH-9), AI providers + Postgres connections (CON-4, phase 16), SRC-9 AI benchmark. | deferred to 0.6.0 |
| 2026-10-31 | Open user decisions: (2) custom C# test runner vs xUnit; (3) SET-5 default `/config` vs `/data`; (1) host entry: default taken (stays in Api). | open |
| 2026-10-31 | User items: delete old `src\eu` folders (stage 2), run `scripts\stage3-remove-old.ps1`, delete `src\AutoSourcarr.Core\Text`, deploy 0.4.5, optionally allow nuget/dotnet domains, delete `ci-probe` at the end, add the `/api/reader/repo/` Authelia/NPM bypass at 0.5.0 deploy. | open |
| 2026-10-29 | Requirements proposals PROP-1..4 (release split, defer CON-4/SRC-9/MAT-21, drop UI-3 theme research, hand-maintained backup schema) — status in `claude/requirements-changes-2026-10-29.md`; PROP-1/PROP-4 were applied in practice (unverified that the user formally accepted them). | unverified |
| 2026-10-25 | C# runtime version constant still `0.4.3-migration`; the compatibility list describes 0.5.0. | fix at release |

## 7. User skills used on this project

From `AGENTS.md` in the PC workspace (Codex desktop skill pack, 22 skills installed 2026-10-09).
Descriptions are taken from how AGENTS.md assigns them; their internal contents are **unverified** here.

| Skill | Used for |
| --- | --- |
| AutoSourcarr Developer | Project-specific development: coordinating changes, backend (FastAPI/SQLite, scheduling, imports). |
| Codex Coordinator | Scope, write ownership, integration and packaging (Coordinator role). |
| Engineering Guardrails (Code Work / Code Verification) | Coding rules for backend/UI work and verification passes. |
| Release Verifier | Independent release review: requirements, fresh tests, migrations, compatibility, limitations. |
| Self-hosting Engineer | Deployment advice: TrueNAS Compose, mounts, Suwayomi, Authelia, logs. |
| CodebaseDesign | UI engineering on the existing vanilla JS UI. |
| Atlas Scout | Backend/performance investigation, when its runtime is available (unverified whether ever available). |
| Fallow | JavaScript tooling for the UI, when installed (unverified whether installed). |
| desktop-preference-learner | Records durable user preferences; invoked only explicitly (from user memory, not AGENTS.md). |

The Claude cloud sessions of 2026-10-31/11-01 did not use these Codex skills; they used Claude's
own tools (Claude Docs for the plan/requirements, the Claude Project for checkpoint/port-map, the
PC device bridge for ZIPs, sync and the knowledge DB).
