# AutoSourcarr — Claude guidance

Self-hosted Sonarr/Radarr-style manager for manga and similar media. It matches
Tachiyomi/Mihon/Keiyoushi sources into one catalogue, serves it to Mihon/Suwayomi
through a reader extension, and repairs orphaned sources inside the instance.

This file replaces the Codex-specific parts of `AGENTS.md` for Claude sessions.
`AGENTS.md` stays for Codex. When the two disagree, the newest explicit user
instruction wins, then this file.

## Layout

| Path | What it is |
| --- | --- |
| `autosourcarr/` | Application project (release ZIP root). Backend in `autosourcarr/autosourcarr/app`. |
| `autosourcarr/autosourcarr/app/static` | Vanilla JS UI (Reimagined, Sonarr v5 style since 0.4.0). |
| `autosourcarr/extension` | Kotlin reader extension (APK for phones, JAR for Suwayomi). |
| `autosourcarr/repair-worker`, `source_tools` | Canonical repair/source tools. `autosourcarr/bundled` is generated: edit canonical copies, then run `scripts/sync-bundled-tools.py`. |
| `autosourcarr/src`, `tests/AutoSourcarr.Tests`, `native/` | C#/.NET 10 migration. **Paused at stage 26 — do not continue without an explicit resume.** |
| `PROJECT_CONTEXT.md` | Long history (142 KB). Read the section you need, not all of it. |
| `../autosourcarr data/` | Archive: release ZIPs, migration stages, full backup, knowledge DB. |
| `../autosourcarr data/AutoSourcarr-knowledge.sqlite3` | Development knowledge DB (not a runtime DB). Update in place; never create a second one. |

Runtime: Python 3.12 FastAPI + SQLite (`catalog.sqlite`, `chapter-origins.sqlite`),
inside an image built on `ghcr.io/suwayomi/suwayomi-server:v2.3.2243`. Deployment:
TrueNAS Compose, Authelia in front, Europe/Amsterdam, UID/GID 568:568.

## How to work

- One executor. No parallel agents unless the user asks.
- Propose changes and wait for approval before editing, unless the user already
  approved that scope. Distinguish verified results from estimates.
- Write short, plain text. No padding.
- Before any app edit, look up the affected feature in the knowledge DB
  (requirements, open findings, decisions) and read the matching records. After the
  change, record what was verified and what was not. From the workspace root:
  `python -B autosourcarr/scripts/knowledge-db.py --db "../autosourcarr data/AutoSourcarr-knowledge.sqlite3" search "term"`
  (`show ID`, `stats --summary`, `check`). `codex-tools/research_gate.py` gives
  the formal before/after receipt; use it when a shell on the PC is available.

## Requirements doc

The agreed requirements live in the Docs artifact "AutoSourcarr requirements"
(tab "Requirements") with a copy in the Claude Project at `claude/requirements.md`.
After every requirements change, automatically: compact the doc, put every item in
its area list (ARC, PIPE, SRC, REP, CON, MAT, IMP, META, CHAP, READ, AUTH, GST, UI, PERF, SET,
DEV), renumber, remove decisions already made, keep area names logical, then
recheck for new optimisations and list them as PROP items.

## Never change

- Subscriptions, manual titles/matches/groups, rejected matches, separate entries,
  import decisions, chapter provenance. Reset only on an explicit confirmed reset.
- Automatic title floor ≥ 0.94 plus edition/identity checks (colored/monochrome,
  manga/webtoon, animated/still). Summaries never lower the floor.
- A local timeout or capacity deferral is not a source outage or a missing manga.
- Reader API key protection and the Authelia rule `^/api/reader/v1([/?].*)?$`.
- Both runtime databases, active env files, signing keys, source profiles.
  Never index live config, accounts, keys or cookies into the knowledge DB.
- Do not deploy, reset data or overwrite live configuration while preparing a release.

## Deployment rule (binding)

Every feature runs inside its own AutoSourcarr container and is usable from the
Web UI. Normal deployment = AutoSourcarr + its main Suwayomi (two instances = four
services). No helper Compose services, Docker socket or sibling containers. Put
defaults and enforcement in the image; host steps only for kernel/container
permissions. Repair code stays isolated from app config, data, live source
profiles and other jobs, and fails closed without host isolation.

## Validate

```bash
# Linux, from autosourcarr/ (pinned deps: requirements.txt + requirements-test.txt)
PYTHONPATH=autosourcarr python -m pytest -q autosourcarr/tests
bash scripts/check-project.sh        # version alignment, bundle sync, Linux text, UI suites, pytest
python scripts/sync-bundled-tools.py --check
```

Known baseline (0.4.3 and 0.4.4, Linux): 40 stale tests fail; see CHANGELOG 0.4.4.
Report APK/JAR builds, Docker builds, rendered-browser checks and NAS checks only
if they were actually run.

## Release

- Keep `VERSION` (3 copies), static asset `?v=`, extension version/code,
  `check-project.sh`, README, docs and CHANGELOG aligned.
- One updating `CHANGELOG.md` with results and limits. No review/verification
  reports, logs, diffs or audit folders in the ZIP.
- Save release ZIPs to `../autosourcarr data/`.
